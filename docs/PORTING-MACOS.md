# Porting Rewire Guard to macOS

A working plan for getting Rewire Guard running on macOS, written against the v1.0.0 Windows
codebase.

The short version: about a third of the code moves over untouched, the image and inference layer
needs its graphics calls swapped, and everything that touches the operating system — screen
capture, keystrokes, credentials, tray icon, the whole UI — gets rewritten. Budget for rewriting
the shell around a core that survives.

---

## 1. What actually ports

Measured against the current source, not guessed.

### Moves with no changes (~1,130 lines)

| File | Why it is safe |
|---|---|
| `AppConfig.cs` | Plain properties, `System.Text.Json`, validation |
| `Services/EscalationManager.cs` | Pure logic, no I/O |
| `Services/TileGrid.cs` | Only uses `Rectangle`, which lives in `System.Drawing.Primitives` — cross-platform, unlike `System.Drawing.Common` |
| `Services/Log.cs` | `SpecialFolder.LocalApplicationData` resolves to `~/Library/Application Support` |
| `Services/ModelDownloader.cs` | `HttpClient` plus `SHA256` |
| `Services/PavlokClient.cs` | RestSharp over HTTPS |
| `Services/DotEnvLoader.cs` | File reads only |

The escalation ramp, tile geometry, config validation and Pavlok integration are already
portable. So are the 46 unit tests covering them — retarget the test project from
`net8.0-windows` to `net8.0` and they should pass unmodified.

### Needs its graphics layer swapped (~700 lines, perhaps 200 of them affected)

`Services/NsfwClassifier.cs` and `Services/HierarchicalScanner.cs` depend on `Bitmap`,
`Graphics`, `LockBits` and `ImageAttributes` — all GDI+, all Windows-only since .NET 7.

ONNX inference itself is fully cross-platform. What you are replacing is the pixel handling
around it: the two-stage downscale, the tensor fill, and the 8x8 luma signatures used for change
detection.

**Use SkiaSharp.** Cross-platform, maintained, and it maps closely onto what the code already
does:

| Current (GDI+) | SkiaSharp |
|---|---|
| `Bitmap` | `SKBitmap` |
| `Graphics.DrawImage` with `InterpolationMode` | `SKCanvas.DrawBitmap` with `SKSamplingOptions` |
| `LockBits` and `BitmapData.Scan0` | `SKBitmap.GetPixelSpan()` |
| `ImageAttributes` with `WrapMode.TileFlipXY` | `SKShaderTileMode.Mirror`, or clamp the source rect |

Keep the two-stage downscale. The reason it exists — a single 7x reduction aliases badly and
measurably shifts the classifier output — applies to Skia too.

### Gets rewritten (~3,300 lines)

Everything else: all of `UI/`, `App.xaml.cs`, `ScreenCaptureService`, `TokenStore`,
`AcceleratorRuntime`.

---

## 2. Restructure first, on Windows

Do this before writing a line of macOS code, and keep the Windows build green throughout. A
refactor is far easier to verify when you can still run the thing.

Split into three projects:

```
RewireGuard.Core/       net8.0           no platform dependencies
RewireGuard.Windows/    net8.0-windows   WPF, existing code
RewireGuard.Mac/        net8.0           Avalonia, new
```

`Core` holds the seven portable files above, the platform-neutral half of the classifier and
scanner, and interfaces for the rest:

```csharp
public interface IScreenCapture     // Capture() -> frame plus per-display rects
public interface IInputSender       // SendCloseShortcut()
public interface IForegroundApp     // Identify() -> app identifier
public interface ICredentialStore   // Save / Load / Clear the Pavlok token
public interface IAutostart         // IsEnabled / SetEnabled
public interface IAcceleratorPolicy // which execution provider to use
```

The Windows implementations already exist inside the current classes. This is mostly moving code
behind an interface, not writing new logic.

---

## 3. Component-by-component mapping

### Screen capture to ScreenCaptureKit

`CopyFromScreen` becomes `SCScreenshotManager` or `SCStream` (macOS 12.3+). The earlier APIs,
`CGWindowListCreateImage` and `CGDisplayStream`, are deprecated and best avoided in new code.

**Requires Screen Recording permission**, granted in System Settings → Privacy & Security →
Screen Recording. Two consequences worth designing around:

- The app must handle denial, and historically needed a relaunch after the grant. The start
  screen is the natural place to detect and explain this; it already has a readiness checklist.
- macOS shows a screen-recording indicator in the menu bar while capturing. For an accountability
  tool that is arguably a feature, but it cannot be suppressed.

Enumerate displays with `SCShareableContent.displays` to replace `EnumDisplayMonitors`. Keep the
per-monitor content band — the reasoning behind it is identical on macOS.

### Closing a tab: CGEvent, and it is Command-W

Not Ctrl+W. `CGEventCreateKeyboardEvent` with `kVK_ANSI_W` and `.maskCommand`, posted through
`CGEventPost(.cghidEventTap, ...)`.

**Requires Accessibility permission** (System Settings → Privacy & Security → Accessibility),
which is separate from Screen Recording and must be requested separately. Check it with
`AXIsProcessTrustedWithOptions`.

**The blocklist changes shape.** `ProtectedProcessNames` holds Windows process names; macOS
identifies apps by bundle identifier via `NSWorkspace.shared.frontmostApplication`. Rewrite it:

```
com.microsoft.Word        com.microsoft.Excel      com.microsoft.Powerpoint
com.apple.dt.Xcode        com.microsoft.VSCode     com.jetbrains.*
com.apple.Terminal        com.googlecode.iterm2    dev.warp.Warp-Stable
com.adobe.Photoshop       com.apple.TextEdit       com.apple.Notes
```

Same principle: Command-W closes a document in those and a tab everywhere else. Note that
`com.apple.finder` should **not** be on the list — Command-W closes a Finder window, exactly as
Ctrl+W closes an Explorer one.

### The overlay: NSWindow, and mind fullscreen

Avalonia gives you a window; making it behave like the WPF overlay needs native tweaks:

- `NSWindow.level = .screenSaver`, above almost everything
- `collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]`

The second line is the one people miss. Without `.fullScreenAuxiliary` the overlay will not appear
over a fullscreen app — which is exactly when it is needed. Test against a fullscreen video and a
fullscreen game specifically.

For multi-display, iterate `NSScreen.screens`. There is no single virtual-desktop rectangle the
way Windows has one, so create one overlay window per screen rather than one spanning window.

### Token storage to Keychain

DPAPI has no equivalent. Use Keychain Services with `kSecClassGenericPassword`. It is strictly
better for this: the item is protected by the login keychain.

### Autostart to SMAppService

`SMAppService.mainApp.register()` on macOS 13+, or a `LaunchAgent` plist in
`~/Library/LaunchAgents` for older systems. Do not reach for `LSSharedFileList`; it is long
deprecated.

### Accelerators: CoreML, and the argument carries over

Delete `AcceleratorRuntime.cs` wholesale. The runtime-swapping mechanism exists only because
OpenVINO and DirectML ship conflicting `onnxruntime.dll` builds. On macOS there is one sensible
provider: **CoreML**.

The interesting part is that the reasoning for preferring the NPU transfers exactly. On Apple
Silicon, CoreML can dispatch to the **Apple Neural Engine**, which like the Intel NPU leaves the
GPU free for whatever the user is actually doing:

```
MLComputeUnits = CPUAndNeuralEngine   // ANE, keeps the GPU free — prefer this
MLComputeUnits = All                  // lets CoreML use the GPU as well
```

So the Settings picker survives as a simpler two-option choice with the same trade-off behind it.
Expect ANE performance broadly comparable to the Intel NPU figure, but measure rather than trust
that — the same benchmark harness applies.

### Paths

`LocalApplicationData` maps to `~/Library/Application Support`, so `Log.cs` and the credential
store land in the right place already.

**One thing that must change:** the Windows build writes `appsettings.json` beside the executable.
On macOS, modifying anything inside a signed `.app` bundle invalidates its signature. Move config
to `~/Library/Application Support/RewireGuard/` before packaging.

---

## 4. UI: Avalonia

Avalonia is the pragmatic choice — XAML-based, close enough to WPF that the structure of
`Theme.xaml` and the three windows carries across, and it keeps the C# core.

Transfers well: the theme dictionary, control templates, and the overall layout of the overlay,
settings and start screens.

Does not transfer: `Hardcodet.NotifyIcon.Wpf` (use Avalonia's `TrayIcon`, which wraps
`NSStatusItem`), anything using `System.Windows.*` types, and the chromeless-window drag handling
in `ThemedWindow.cs`.

Two honest caveats:

- Avalonia's macOS support is good but not identical to native. Menu bar behaviour, window levels
  and fullscreen interaction will need native interop through a small Objective-C shim or
  equivalent bindings.
- If you end up writing a lot of native interop anyway, consider whether a native Swift app
  calling the ONNX model directly would be less total work. You would lose the tested escalation
  logic, which is the main argument against.

---

## 5. Packaging and distribution

This is where macOS costs real money, unlike Windows.

1. **`.app` bundle.** `dotnet publish -r osx-arm64` produces an executable, not a bundle. You
   assemble `RewireGuard.app/Contents/{MacOS,Resources,Info.plist}` yourself.
2. **`Info.plist`.** Set `LSUIElement = true` so the app runs as a menu bar item with no Dock
   icon — the equivalent of the current "no main window" behaviour.
3. **Universal binary, or arm64 only.** Build `osx-arm64` and `osx-x64` and join them with `lipo`
   if you want Intel Mac support. Most Macs in use are Apple Silicon now.
4. **Code signing.** A Developer ID Application certificate, which requires the **Apple Developer
   Program at $99/year**. Sign with `--options runtime` (hardened runtime) or notarization will
   reject it.
5. **Notarization.** `xcrun notarytool submit --wait`, then `xcrun stapler staple`. Takes minutes,
   and must be repeated for every build you distribute.
6. **`.dmg`** for distribution, via `create-dmg` or `hdiutil`.

**Without paying the $99**, macOS is harsher than Windows. SmartScreen at least offers a "Run
anyway" link; Gatekeeper on recent macOS removed the old right-click → Open shortcut, so users
must visit System Settings → Privacy & Security and click "Open Anyway" after being blocked.
Expect to lose most casual users there.

**The App Store is not an option.** Sandboxed apps cannot post synthetic keyboard events into
other applications. Direct distribution only.

---

## 6. Suggested order

Each step leaves something you can check.

1. **Split out `RewireGuard.Core`** on Windows. Keep the WPF app working and the tests green.
2. **Retarget the tests** to `net8.0` and confirm they pass — this proves the core is portable.
3. **Swap GDI+ for SkiaSharp** in the classifier and scanner, still on Windows. Verify that
   probabilities match the current output on a few fixed images. Doing this on Windows means any
   difference is Skia's fault, not macOS's.
4. **Console spike on the Mac:** load the ONNX model, run CoreML inference on a static image,
   print the probability. No UI. This is where you find out how CoreML and the ANE behave.
5. **Screen capture spike:** ScreenCaptureKit into an `SKBitmap`, handling the permission prompt.
6. **Wire the core to those two** and log detections with no UI at all. The product works at this
   point, invisibly.
7. **Avalonia UI:** menu bar item first, then the overlay, then settings and the start screen.
8. **Package, sign, notarize.**

Steps 1 to 3 happen on Windows and de-risk everything after them. Step 4 is the one most likely
to surprise you.

---

## 7. What to drop

- `AcceleratorRuntime.cs` and the whole runtime-swap mechanism — one provider on macOS
- The WiX installer, `app.manifest`, and the DPI awareness declarations
- The DirectML adapter probe
- All `Microsoft.Win32.Registry` usage
- The single-file portable build; on macOS a `.app` bundle is already the portable unit

---

## 8. Rough effort

Not a promise, but an honest shape:

| Phase | Feel |
|---|---|
| Core extraction and SkiaSharp swap | A weekend, low risk, done on Windows |
| CoreML and ScreenCaptureKit spikes | A few days, moderate risk, most of the unknowns |
| Avalonia UI | The longest stretch: three windows, a theme, native interop |
| Signing and notarization | A day of fighting, plus $99 |

The core logic being genuinely portable is the good news. The bad news is that a screen monitor
is, almost by definition, mostly platform integration.
