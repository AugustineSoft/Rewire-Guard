# Rewire Guard

A Windows tray app that watches your screen for explicit content and steps in when it finds it.

It takes a screenshot every few seconds, runs it through an image classifier on your own machine,
and escalates an on-screen intervention if content keeps appearing. If you have a
[Pavlok](https://pavlok.com) wearable, it can send a vibration or zap alongside the overlay.

**Your screen never leaves your computer, and no data is collected.** Classification runs locally
against a model stored on disk. There is no server, no account, no telemetry, no per-image cost,
and it works with no internet connection. The only thing that ever leaves your machine is the
optional Pavlok API call, which sends a stimulus command and nothing else — never an image.

### [⬇ Download for Windows](https://github.com/AugustineSoft/Rewire-Guard/releases/latest/download/RewireGuard-Setup.exe)

No administrator rights required. Windows will warn you the first time because the build is not
code-signed — choose **More info → Run anyway**. See [Installing](#installing) for the portable
version and checksums.

---

## Contents

- [Installing](#installing)
- [System requirements](#system-requirements)
- [First run](#first-run)
- [How it behaves](#how-it-behaves)
- [Settings](#settings)
- [Pavlok setup](#pavlok-setup)
- [Tuning performance](#tuning-performance)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)

---

## Installing

**Installer (recommended)** — download `RewireGuard-Setup.exe` and run it. It installs for your
user account only, so there is no administrator prompt, and it adds a Start Menu entry plus an
uninstaller in Settings → Apps.

**Portable** — download `RewireGuard-Portable.zip`, extract it anywhere, and run `RewireGuard.exe`.
Nothing is written outside the folder except your settings and logs. Good for a USB stick or for
trying it without installing.

Neither version needs the .NET runtime, Visual Studio, or Python. Everything required is included.

> The downloads are not code-signed, so Windows SmartScreen will warn you the first time.
> Choose **More info → Run anyway**.

### The app has no window

Rewire Guard lives in the system tray, next to the clock. After the first run, launching it just
places an icon there — that is normal, not a failed start. Right-click the icon for the menu, or
double-click to pause and resume.

---

## System requirements

### Minimum

| | |
|---|---|
| OS | Windows 10 or Windows 11, 64-bit |
| CPU | Any modern x64 processor |
| RAM | 4 GB free |
| Disk | ~620 MB installed |
| Display | Any; multi-monitor supported |

### What actually determines performance

The classifier is a Vision Transformer (ViT-Base) running at 384×384. One pass is a real piece of
compute, and the app may run several per screen check, so the hardware that matters is whatever
can accelerate that.

The installer ships two inference runtimes and picks one for you at first launch. **Settings →
Accelerator** lets you override it:

| Setting | Runs on | Works with |
|---|---|---|
| **Auto** (default) | NPU if present, otherwise GPU | Anything |
| **Intel NPU** | Intel Core Ultra NPU, or Intel GPU | Core Ultra Series 1 and 2 |
| **GPU** | Any DirectX 12 GPU | NVIDIA, AMD, Intel Arc / Iris Xe |
| **CPU** | Processor only | Anything |

Changing it takes effect after a restart, because the runtime cannot be replaced while it is
loaded. The tray status line and the log both show which one is actually in use.

Measured on one Core Ultra 7 155H laptop with an RTX 4060, same model and input size:

| Running on | Per inference |
|---|---|
| CPU | 262 ms |
| Intel Arc integrated GPU | 144 ms |
| Intel NPU | 115 ms |
| NVIDIA RTX 4060 | 37 ms |

Treat those as one data point rather than a promise.

### Why Auto prefers the NPU over a faster GPU

A discrete GPU is several times quicker, but Rewire Guard runs continuously in the background.
While a game or a video is on screen, everything changes every frame, so the change-detection
optimisation saves nothing and a full set of inferences runs on every check. On the GPU that is
sustained load competing with whatever you are actually doing. An NPU is otherwise idle silicon
and costs the GPU nothing.

If you would rather have the speed — or you have no NPU — set **Accelerator** to **GPU**. On a
laptop with both integrated and discrete graphics, the app measures each adapter once and keeps
the faster one, so it will not silently settle for the integrated GPU.

### Memory and battery

Expect roughly **1 GB** of working set while running, most of which is the model file mapped into
memory rather than allocated.

On a laptop, continuous screen checking will shorten battery life. The NPU path is markedly
gentler than the CPU or GPU paths. If you are running on battery, raising **Poll interval** and
lowering **Max tiles per poll** in Settings makes a large difference.

> The **portable** build carries a single runtime and has no accelerator picker. Bundling native
> libraries into a one-file executable means they are extracted to a temporary folder at launch,
> which the runtime swap cannot work around. Use the installer if you need to choose.

---

## First run

The start screen appears the first time you launch, showing what is ready and what is not.

**Detection model.** The installer includes it, so this usually reads *Ready*. If it says
*Not installed* — which happens with the portable build or a source checkout — click
**Download model (330 MB)**. It is fetched once, verified against a known checksum, and
monitoring begins immediately without a restart.

**Pavlok device.** Optional. Click **Connect Pavlok** to link one, or **Continue without it** to
run overlay-only. You can connect a device later from the tray menu.

The start screen only reappears when something needs attention. Once the model is present and any
device is connected, the app starts silently into the tray. You can reopen it any time from
**tray icon → Start screen**.

---

## How it behaves

When content is detected on consecutive checks, Rewire Guard escalates through three levels. Each
level dims the screen further, shifts colour from amber toward red, and — if a Pavlok is connected
— sends a stronger stimulus.

| Level | Overlay | Default stimulus |
|---|---|---|
| 1 | Amber, light dim | Vibration |
| 2 | Orange, heavier dim | Zap, intensity 30 |
| 3 | Red, near-opaque | Zap, intensity 70 |

Every escalation level offers two actions:

- **Close active tab** sends Ctrl+W to whatever window has focus, which closes the tab in a
  browser, the window in File Explorer, and so on. At levels 2 and 3 the overlay then holds for a
  short pause before releasing you. It is skipped for a small list of applications where Ctrl+W
  closes a *document* rather than a tab — Office, code editors and IDEs, terminals, and creative
  tools — because there it can discard unsaved work or kill a running process. Edit
  `ProtectedProcessNames` in `appsettings.json` to change that list, or empty it to send Ctrl+W
  everywhere.
- **Override** dismisses the warning immediately. It confirms first, and applies a level 3
  stimulus.

The overlay clears on its own once the screen has stayed clean for the configured time. Pausing
from the tray resets escalation to zero, so resuming always starts fresh rather than picking up
where it left off.

With a Pavlok connected, a stimulus fires on **every check that still detects content**, not only
when the level increases. Closing the content stops it on the very next check.

---

## Settings

Open from **tray icon → Settings**. Changes apply immediately; there is no restart.

**Monitoring** — how often the screen is checked, how confident a detection must be, whether to
watch all monitors, and the two performance switches described below.

**Escalation** — how many consecutive checks raise a level, how long the screen must stay clean
before clearing, and the length of the pause after closing a tab. The escalation slider shows how
long your settings actually take to reach level 3, which is worth reading before changing it.

**Pavlok** — the minimum gap between stimuli, and the type and intensity for each level.

**Application** — start with Windows, whether the overlay pulls itself back to the front, and a
shortcut to the log folder.

Settings are stored in `appsettings.json` beside the executable. An upgrade never overwrites it.

---

## Pavlok setup

Entirely optional; the overlay works without it.

The most reliable method is an **API key** from your Pavlok dashboard, entered on the start
screen. Unlike a login session, it does not expire. Signing in with email and password also works.

Your credential is encrypted with your Windows account and stored at
`%LOCALAPPDATA%\RewireGuard\token.bin`. It is never written to the settings file. If Pavlok
rejects it later, the app clears it and prompts you to reconnect instead of silently failing.

---

## Tuning performance

If checks take longer than your poll interval, the app logs it and simply checks less often — it
will not queue up work or freeze. But you can bring the cost down considerably:

| If you want | Change |
|---|---|
| Much lower CPU use | Raise **Poll interval** to 5–10 seconds |
| Lower peak load per check | Lower **Max tiles per poll** |
| The cheapest possible mode | Turn **Tiled scanning** off — whole-screen checks only |
| Fewer wasted checks | Keep **Skip unchanged regions** on (default) |

Two features do the heavy lifting by default. **Skip unchanged regions** compares each part of the
screen against the previous check and reuses the earlier result where nothing moved, so an idle
desktop costs almost nothing. **Max tiles per poll** caps how much work a single check can do;
anything over the cap is carried to the next check rather than dropped, so coverage stays complete.

Turning **Tiled scanning** off is the largest saving, at a real cost in accuracy: a whole-screen
check shrinks your entire desktop to 384×384, which can wash out a small image in a feed. Tiling
exists to catch exactly that.

---

## Troubleshooting

**Nothing happens when I launch it.** Expected — it starts in the system tray. Check for the icon
near the clock; it may be hidden behind the tray's overflow arrow.

**It says the model is missing.** Open **tray icon → Start screen** and use the Download button.

**It never detects anything.** Lower **Detection threshold** in Settings. Check the tray status
line, which shows the highest score from the most recent check — if that number is well below your
threshold, the threshold is the problem.

**It triggers on innocent content.** Raise **Detection threshold**, and raise
**Polls before escalating a level** so a single bad check cannot escalate.

**It is using too much CPU.** Check the tray status line for which accelerator is in use. If it
reads *CPU*, open **Settings → Accelerator** and pick one explicitly; the log records why the
automatic choice was rejected. Otherwise see [Tuning performance](#tuning-performance).

**It is slowing down my games.** Set **Settings → Accelerator** to **Intel NPU** if you have one,
which leaves the GPU untouched. Failing that, raise **Poll interval**.

**Full-screen video appears black to it.** Some hardware-accelerated video paths cannot be
captured by the screen-grab method used here. This is a known limitation.

**"Close active tab" did nothing.** The focused application is on the `ProtectedProcessNames` list
in `appsettings.json`, where Ctrl+W would close a document instead of a tab. The overlay says
which application it was. Remove it from the list if you want the keystroke sent there anyway.

**Something crashed.** Logs are at `%LOCALAPPDATA%\RewireGuard\logs`, reachable from
**tray icon → Open log folder**. They record startup, accelerator selection, timings and errors —
they contain no images and no screen content.

---

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Visual Studio is
optional and only helps for editing XAML.

```bash
git clone https://github.com/AugustineSoft/Rewire-Guard.git
```

```bash
dotnet build -c Release
```

```bash
dotnet run -c Release
```

Run the tests with:

```bash
dotnet test
```

### The model is not in the repository

At ~330 MB it exceeds GitHub's file size limit, so `Models/*.onnx` is excluded. Use the in-app
download on first run, or export it yourself following `scripts/convert_to_onnx.md`:

```bash
optimum-cli export onnx --model AdamCodd/vit-base-nsfw-detector --task image-classification onnx_out/
```

Copy `onnx_out/model.onnx` to `Models/model.onnx`. If you export it yourself, check `id2label` in
the exported `config.json` and confirm `NsfwLabelIndex` in `appsettings.json` points at the "nsfw"
class — the ordering is not guaranteed.

### Building the installer

```bash
./installer/build.ps1 -Version 1.0.0
```

This publishes the app, then produces `dist/RewireGuard-Setup.exe` and
`dist/RewireGuard-Portable.zip`. WiX is restored automatically as a NuGet package; nothing needs
installing first. Add `-IncludeModel:$false` for a much smaller build that relies on the in-app
download, or `-PortableOnly` to skip the installer while iterating.

### Using an NVIDIA GPU or DirectML

Replace the `Intel.ML.OnnxRuntime.OpenVino` package reference in `RewireGuard.csproj` with
`Microsoft.ML.OnnxRuntime.Gpu` (CUDA) or `Microsoft.ML.OnnxRuntime.DirectML`, then rebuild. The
app queries ONNX Runtime for available accelerators at startup and will pick up the new one with
no code change.

---

## Known limitations

- Hardware-accelerated full-screen video can capture as black.
- The scan region is a fixed centred band on each monitor, which assumes a roughly centred
  window. A tiled or off-centre layout may not line up.
- Builds are unsigned, so SmartScreen warns on first run.
- Skipping unchanged regions can miss content that fades in very gradually. Turn the setting off
  to check everything on every pass.
