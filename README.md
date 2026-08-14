# Rewire Guard (Windows client)

Windows tray app: periodically screenshots the desktop, runs it through a
local ONNX-exported NSFW classifier (AdamCodd/vit-base-nsfw-detector), and
escalates an on-screen intervention if it keeps firing. Classification is fully
local -- no image ever leaves the machine, no per-request cost, works offline.
The optional Pavlok integration is the only thing that talks to the network.

This replaces the earlier iOS/Safari-extension plan, which hit a hard wall:
Xcode is macOS-only and there's no supported way around that. Watching the
screen directly on Windows also sidesteps the browser-extension-to-native-app
bridge entirely -- it works regardless of which app or browser tab is on screen.

## Prerequisites

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 (or `dotnet build` from the CLI) -- Visual Studio is not
  required but makes debugging the WPF XAML easier
- Python 3.9+ (only needed once, to export the model)

## 1. Get the model

The ONNX model is ~330 MB, past GitHub's 100 MB per-file limit, so it is **not** in this
repository (`Models/*.onnx` is gitignored). Two ways to get one:

**Let the app fetch it.** On first run, the start screen shows a *Download model* button. It
pulls the published export from Hugging Face, verifies it against a known SHA-256, and starts
monitoring without a restart. Nothing else is needed.

**Or export it yourself**, per `scripts/convert_to_onnx.md`:

```bash
pip install optimum[exporters] transformers torch
```

```bash
optimum-cli export onnx --model AdamCodd/vit-base-nsfw-detector --task image-classification onnx_out/
```

Copy `onnx_out/model.onnx` to `Models/model.onnx`.

Note that your own export will not be byte-identical to the published one -- different
optimum/opset versions serialize the graph differently -- but the weights and label order are the
same. Check `onnx_out/config.json`'s `id2label` and make sure `NsfwLabelIndex` in
`appsettings.json` actually points at the "nsfw" class; don't assume it's 1. The classifier fails
loudly at startup if the index is out of range for the model's output, rather than silently
reading the wrong class.

## 2. Build and run

```bash
dotnet build -c Release
```

```bash
dotnet run -c Release
```

Or open `RewireGuard.sln` in Visual Studio and hit F5. The app has no main
window -- it starts minimized to the system tray. Double-click the tray icon
to pause/resume; right-click for the menu.

Run the tests with:

```bash
dotnet test
```

## 3. Autostart

Settings -> **Start with Windows**, or the same toggle in the tray menu. This writes the exe path to
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Untick it to remove the
entry. (A real installer -- MSIX or Squirrel -- is still the better answer if
you want this to feel like a shipped app rather than a build output, but the
registry entry covers the actual need.)

## The interface

All windows share one dark theme (`UI/Theme.xaml`) and draw their own chrome, because the
Win32 title bar is light-themed and can't be recoloured from WPF.

- **Start screen** (`UI/StartWindow.xaml`) -- what the app does, whether the model and device
  are actually ready, then a connect pane taking either an API key or email/password. Shown
  automatically when there's no credential, and any time from the tray menu.
- **Settings** (`UI/SettingsWindow.xaml`) -- monitoring, escalation, per-level stimulus type and
  intensity, and app behaviour. Edits a clone, so Cancel costs nothing. Saving copies onto the
  shared `AppConfig` every service holds, which is what makes changes apply without a restart;
  the poll timer, capture service and tile cache are rebuilt explicitly.
- **Overlay** (`UI/OverlayWindow.xaml`) -- escalation level drives the accent colour, the filled
  segment count, and how far the backdrop dims.
- **Confirm dialog** (`UI/ConfirmDialog.xaml`) -- replaces `MessageBox`, which rendered as a
  white system window in the middle of a dark full-screen overlay.

`UI/Theme.xaml` is loaded by absolute pack URI rather than a relative one so the windows can
also be constructed from a separate assembly, which is how the design renders were reviewed
offscreen instead of by putting a full-screen overlay on a real desktop.

## Pavlok

Off unless `PavlokEnabled` is true in `appsettings.json` (it is, by default). Everything below
is also editable from Settings.

Credentials are read in this order and never come from the config file:

1. `PAVLOK_API_KEY` environment variable (preferred -- dashboard-issued key)
2. `PAVLOK_EMAIL` + `PAVLOK_PASSWORD` environment variables
3. A cached token in `%LOCALAPPDATA%\RewireGuard\token.bin`, DPAPI-encrypted
   to the current Windows user
4. A login dialog

A `.env` in the repo root (or up to six directories above the build output) is
loaded into the process environment for dev convenience.

A stimulus fires on **every poll that is still positive**, not only when the
escalation level increases -- so the cadence is set by
`PavlokMinSecondsBetweenStimuli` (default 3s) rather than by the escalation
ramp. Closing the content stops stimuli on the very next poll, because the
trigger is gated on the current poll being positive rather than on the overlay
level, which lingers by design.

If the API rejects the token, it is cleared and re-authentication is attempted
once a minute at most, rather than firing doomed requests forever.

## How it works

- `Services/ScreenCaptureService.cs` -- grabs the whole virtual desktop via GDI
  every `PollIntervalSeconds` into a reused buffer, and reports each monitor's
  rectangle so the content band is computed per display
- `Services/HierarchicalScanner.cs` -- coarse whole-frame pass plus an
  overlapping tile grid; skips tiles whose pixels haven't changed and caps
  inferences per poll (see Performance below)
- `Services/TileGrid.cs` -- the tile geometry, kept pure so it can be tested
- `Services/NsfwClassifier.cs` -- resizes, normalizes, runs the ONNX model,
  returns a probability
- `Services/EscalationManager.cs` -- tracks consecutive positive frames,
  raises escalation level (0-3), resets after `CleanMinutesToReset` of clean
  frames
- `Services/Log.cs` -- rolling log at `%LOCALAPPDATA%\RewireGuard\logs`
- `UI/OverlayWindow.xaml(.cs)` -- overlay spanning every monitor, whose
  backdrop opacity and accent colour scale with escalation level
- `App.xaml.cs` -- wires it all together, owns the tray icon and polling timer

## Performance

A ViT-base forward pass is not cheap, and a naive tiling of a 2560x1440 desktop
is ~26 inferences per poll. Three things keep that survivable:

- **Change detection** (`ChangeDetectionEnabled`): each tile carries an 8x8 luma
  signature; unchanged tiles reuse their previous probability instead of paying
  for another pass. An idle desktop costs roughly one inference per poll.
- **A per-poll budget** (`MaxTilesPerPoll`, default 12): changed tiles are
  scanned first, and anything over the budget is deferred to the next poll
  rather than dropped, so coverage stays complete over a few polls.
- **Provider selection**: OpenVINO NPU/GPU and DirectML are used when the
  runtime reports them, checked against `GetAvailableProviders()` rather than
  by attempting each one blind.

The tray menu's status line shows the active execution provider, the last
frame's peak probability, tile count and scan duration. If scans routinely
exceed the poll interval, that gets logged.

## Configuration

Everything lives in `appsettings.json`, is range-checked at load, and falls back
to defaults (with a tray warning) if the file is malformed. Notable knobs beyond
the detection thresholds:

| Key | Default | Notes |
| --- | --- | --- |
| `MaxTilesPerPoll` | 12 | Inference budget per poll |
| `ChangeDetectionEnabled` | true | Skip unchanged tiles |
| `CaptureAllMonitors` | true | Virtual desktop vs. primary only |
| `PavlokMinSecondsBetweenStimuli` | 3 | Real cadence limiter |
| `SitSecondsLevel2` / `SitSecondsLevel3` | 20 / 60 | "Sit with it" pause |
| `OverlayFocusLock` | false | Pull overlay back to front, swallow Alt+Tab |
| `BrowserProcessNames` | chrome, msedge, ... | Allowlist for "Close active tab" |

## Design notes

- **No dismiss button on the overlay by design** -- it's meant to sit there
  until the escalation manager clears on its own. "Override" exists but costs a
  level 3 stimulus, and says so before you confirm.
- **"Close active tab" only targets browsers.** Ctrl+W closes the current
  document in Word, the current file in Visual Studio, and the current window in
  Explorer. Sending it at whatever happens to be focused is a good way to lose
  unsaved work, so the foreground process must be on `BrowserProcessNames`.
  Anything else is refused with a message rather than guessed at.
- **Pausing resets escalation to 0.** Level used to survive a pause, so the
  first positive poll after resuming fired a level 3 stimulus with no ramp and
  no overlay.

## Known limitations / things to revisit

- **Full-desktop capture, not content-region capture.** Tiling mitigates this
  (small thumbnails in a feed get their own near-native-resolution pass), but
  the app still doesn't know which window is the browser. Capturing just the
  foreground window's client area would be more precise and cheaper.
- **GDI capture doesn't see hardware-overlay video.** Some full-screen
  accelerated video paths render black to `BitBlt`. A Desktop Duplication API
  capture path would fix that and be faster, at the cost of more code.
- **`ContentRegionWidthFraction` is a fixed centered band per monitor.** It
  assumes a roughly centered browser window; a maximized window on an
  ultrawide, or a tiled half-screen layout, will not line up.
- **No installer and no code signing.** SmartScreen will warn on first run.
- **Change detection can mask a slow fade.** Content that changes below
  `ChangeDetectionThreshold` per poll reuses a stale clean probability. The
  threshold is deliberately low (1.5/255 mean luma delta) but it is a tradeoff;
  set `ChangeDetectionEnabled: false` to scan everything every poll.
