using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using RewireGuard.Services;
using RewireGuard.UI;

namespace RewireGuard;

public partial class App : Application
{
    private const string AutostartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutostartValue = "RewireGuard";

    private TaskbarIcon? _trayIcon;
    private DispatcherTimer? _timer;
    private ScreenCaptureService? _capture;
    private NsfwClassifier? _classifier;
    private HierarchicalScanner? _scanner;
    private EscalationManager? _escalation;
    private OverlayWindow? _overlay;
    private PavlokClient? _pavlok;
    private AppConfig _config = new();
    private bool _paused;
    private bool _busy;
    private bool _shuttingDown;
    private AcceleratorChoice _activeAccelerator = AcceleratorChoice.Auto;

    // Held for the lifetime of the process; releasing it is what lets the next instance start.
    private Mutex? _singleInstanceMutex;

    private MenuItem? _pauseItem;
    private MenuItem? _statusItem;
    private MenuItem? _autostartItem;
    private SettingsWindow? _settingsWindow;
    private StartWindow? _startWindow;

    // Pavlok rate limiting and suppression while "sit-with-it" is active.
    private DateTime _lastPavlokUtc = DateTime.MinValue;
    private bool _pavlokSuppressedForSit;
    private bool _reauthInFlight;
    private DateTime _lastReauthUtc = DateTime.MinValue;

    // Last-scan diagnostics for the tray status readout.
    private double _lastMaxProbability;
    private long _lastScanMs;
    private int _lastTilesScanned;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            // Two copies polling the same screen means two stimuli per detection.
            MessageBox.Show(
                "Rewire Guard is already running. Look for its icon in the system tray.",
                "Rewire Guard",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        InstallCrashHandlers();

        Log.Info($"Rewire Guard starting (pid {Environment.ProcessId}).");

        // Load .env into process environment variables for local dev convenience.
        DotEnvLoader.Load();

        // The installer does not ship appsettings.json, so an upgrade can never overwrite settings
        // the user changed. Write the defaults out on first run instead, so there is still a file
        // to hand-edit and to see the full set of keys in.
        bool hadConfigFile = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));

        _config = AppConfig.Load("appsettings.json", out var configError);

        if (!hadConfigFile && configError == null)
        {
            if (_config.TrySave("appsettings.json", out var writeError))
                Log.Info("Wrote a default appsettings.json.");
            else
                Log.Warn($"Could not write a default appsettings.json: {writeError}");
        }

        _trayIcon = (TaskbarIcon)FindResource("TrayIcon");
        // Double-click pauses only when pause is enabled; in the commitment build it does nothing.
        if (_config.AllowPause)
            _trayIcon.TrayMouseDoubleClick += (_, _) => TogglePause();
        _trayIcon.ContextMenu = BuildTrayMenu();

        if (configError != null)
        {
            Notify($"appsettings.json could not be read ({configError}). Running with defaults.", BalloonIcon.Warning);
        }

        // Must happen before anything touches ONNX Runtime: once the native library is loaded it
        // cannot be replaced for the life of the process.
        _activeAccelerator = AcceleratorRuntime.Apply(_config.AcceleratorChoice);

        _capture = new ScreenCaptureService(_config.CaptureAllMonitors);

        _escalation = new EscalationManager(_config);
        _escalation.LevelChanged += OnEscalationLevelChanged;

        _ = ContinueStartupAsync();
    }

    /// <summary>
    /// The part of startup that can block on I/O or on the user.
    ///
    /// Sequenced deliberately: credential resolution has to finish before we can decide whether
    /// onboarding is needed, and the start screen has exactly one caller. Previously both this
    /// path and the Pavlok initialiser could open one, so a first run with neither a model nor a
    /// token showed two windows back to back.
    /// </summary>
    private async Task ContinueStartupAsync()
    {
        bool modelReady = TryLoadClassifier();

        bool pavlokReady = false;
        if (_config.PavlokEnabled)
            pavlokReady = await InitializePavlokAsync();
        else
            Log.Info("Pavlok integration disabled by config.");

        // A missing model is recoverable, not a dead end: the start screen downloads one and
        // StartMonitoring picks up from there, no restart needed.
        if (modelReady) StartMonitoring();

        bool needsOnboarding = !modelReady || (_config.PavlokEnabled && !pavlokReady);
        if (needsOnboarding) ShowStartScreen();
    }

    /// <summary>Begins polling. Safe to call again after a model arrives late.</summary>
    private void StartMonitoring()
    {
        if (_classifier == null || _scanner == null) return;

        if (_timer == null)
        {
            _timer = new DispatcherTimer();
            _timer.Tick += async (_, _) => await Tick();
        }

        _timer.Interval = TimeSpan.FromSeconds(_config.PollIntervalSeconds);
        _timer.Start();

        Log.Info($"Monitoring started, polling every {_config.PollIntervalSeconds}s.");
    }

    private string ResolvedModelPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.ModelPath);

    private bool ClaimSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\RewireGuard.SingleInstance", out bool createdNew);
            return createdNew;
        }
        catch (Exception ex)
        {
            // Never let the guard itself prevent startup.
            Log.Warn($"Single-instance check failed, continuing anyway: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// A tray app with no main window dies invisibly on an unhandled exception -- the icon just
    /// disappears. These handlers make sure the reason lands in the log, and that a recoverable
    /// dispatcher fault does not take the process down.
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled dispatcher exception.", args.Exception);
            args.Handled = true;
            Notify("Something went wrong. Details are in the log (tray menu > Open log folder).", BalloonIcon.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("Unhandled domain exception.", ex);
            else Log.Error($"Unhandled domain exception: {args.ExceptionObject}");
            Log.Shutdown();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    private bool TryLoadClassifier()
    {
        var modelPath = ResolvedModelPath();
        if (!File.Exists(modelPath))
        {
            Log.Info($"No model at {modelPath}.");
            return false;
        }

        try
        {
            _classifier = new NsfwClassifier(modelPath, _config.InputSize, _config.NsfwLabelIndex,
                                             _config.DirectMLDeviceId);
            _scanner = new HierarchicalScanner(_classifier, _config);

            // Remember which adapter won the probe so later launches skip it. Adapter 0 is
            // typically the integrated GPU, so this is not a cosmetic saving.
            if (_classifier.SelectedDeviceId != _config.DirectMLDeviceId)
            {
                _config.DirectMLDeviceId = _classifier.SelectedDeviceId;
                _config.TrySave("appsettings.json", out _);
            }

            return true;
        }
        catch (Exception ex)
        {
            // A corrupt export or a bad NsfwLabelIndex used to throw straight out of OnStartup,
            // killing the app before the tray icon could say anything.
            Log.Error("Failed to load the ONNX model.", ex);
            Notify($"The model could not be loaded: {ex.Message}", BalloonIcon.Error);
            return false;
        }
    }

    /// <summary>
    /// Resolves a Pavlok credential from the environment, then the cached token. Returns whether
    /// one was found. Shows no UI of its own -- onboarding is the start screen's job, and having
    /// two places able to open it produced duplicate windows on first run.
    /// </summary>
    private async Task<bool> InitializePavlokAsync()
    {
        _pavlok?.Dispose();
        _pavlok = new PavlokClient();

        // dashboard-issued API key (preferred)
        var apiKey = Environment.GetEnvironmentVariable("PAVLOK_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _pavlok.UseExistingToken(apiKey);
            TokenStore.Save(apiKey);
            Log.Info("Pavlok configured from PAVLOK_API_KEY.");
            return true;
        }

        // env-based email/password (power-user shortcut)
        var envEmail = Environment.GetEnvironmentVariable("PAVLOK_EMAIL");
        var envPassword = Environment.GetEnvironmentVariable("PAVLOK_PASSWORD");

        if (!string.IsNullOrWhiteSpace(envEmail) && !string.IsNullOrWhiteSpace(envPassword))
        {
            try
            {
                await _pavlok.LoginAsync(envEmail, envPassword);
                if (_pavlok.Token != null)
                {
                    TokenStore.Save(_pavlok.Token);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Pavlok login from environment credentials failed.", ex);
                // fall through to the cached token
            }
        }

        var cached = TokenStore.Load();
        if (cached != null)
        {
            _pavlok.UseExistingToken(cached);
            Log.Info("Pavlok configured from cached token.");
            return true;
        }

        // Nothing available. The caller decides whether to open onboarding.
        Log.Info("No Pavlok credential found; escalation will run without a device.");
        _pavlok.Dispose();
        _pavlok = null;
        return false;
    }

    /// <summary>
    /// Fires a stimulus for the given level, subject to sit suppression and the configured
    /// minimum interval. Called on every poll that is still positive -- not only when the level
    /// increases -- so this interval is what sets the real cadence.
    /// </summary>
    private async Task TryTriggerPavlokAsync(int level)
    {
        if (_pavlok == null || !_pavlok.IsLoggedIn) return;
        if (_pavlokSuppressedForSit) return;

        var now = DateTime.UtcNow;
        if ((now - _lastPavlokUtc) < TimeSpan.FromSeconds(_config.PavlokMinSecondsBetweenStimuli)) return;

        _lastPavlokUtc = now;

        var (type, value) = StimulusForLevel(level);
        var result = await _pavlok.SendStimulusAsync(type, value, $"RewireGuard escalation level {level}");

        if (result.IsSuccess)
        {
            Log.Info($"Stimulus sent: {type} {value} (level {level}).");
            return;
        }

        Log.Warn($"Stimulus failed ({result.Outcome}): {result.Detail}");
        if (result.Outcome == StimulusOutcome.Unauthorized)
            await HandleUnauthorizedAsync();
    }

    private (string Type, int Value) StimulusForLevel(int level) => level switch
    {
        1 => (_config.PavlokLevel1Type, _config.PavlokLevel1Value),
        2 => (_config.PavlokLevel2Type, _config.PavlokLevel2Value),
        _ => (_config.PavlokLevel3Type, _config.PavlokLevel3Value),
    };

    /// <summary>
    /// Pavlok tokens expire. Previously a 401 just logged and the app kept firing doomed requests
    /// forever, silently doing nothing. Drop the stale token and re-authenticate, at most once a
    /// minute so a genuinely revoked credential cannot spin the login dialog.
    /// </summary>
    private async Task HandleUnauthorizedAsync()
    {
        if (_reauthInFlight) return;
        if (DateTime.UtcNow - _lastReauthUtc < TimeSpan.FromMinutes(1)) return;

        _reauthInFlight = true;
        _lastReauthUtc = DateTime.UtcNow;
        try
        {
            Log.Warn("Pavlok rejected the token; clearing it and re-authenticating.");
            TokenStore.Clear();
            _pavlok?.ClearToken();
            await InitializePavlokAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Pavlok re-authentication failed.", ex);
        }
        finally
        {
            _reauthInFlight = false;
        }
    }

    private async Task ApplyOverridePenaltyAsync()
    {
        if (_pavlok == null || !_pavlok.IsLoggedIn) return;

        var (type, value) = StimulusForLevel(3);
        var result = await _pavlok.SendStimulusAsync(type, value, "Override penalty (level 3)");
        _lastPavlokUtc = DateTime.UtcNow;   // so the next poll doesn't immediately re-fire

        if (!result.IsSuccess)
        {
            Log.Warn($"Override penalty failed ({result.Outcome}): {result.Detail}");
            if (result.Outcome == StimulusOutcome.Unauthorized) await HandleUnauthorizedAsync();
        }
    }

    private async Task Tick()
    {
        if (_paused || _busy || _shuttingDown || _scanner == null) return;
        _busy = true;
        try
        {
            var sw = Stopwatch.StartNew();

            // Capture and inference both run off the UI thread; grabbing an 11 MB frame on the
            // dispatcher was enough to show up as periodic input lag.
            var result = await Task.Run(() =>
            {
                var frame = _capture!.Capture();
                // Frame is owned by the capture service and stays valid until the next Capture().
                return frame == null ? (ScanResult?)null : _scanner.Scan(frame, _capture.MonitorRegions);
            });

            if (result == null) return;   // locked screen / UAC prompt

            var scan = result.Value;
            _lastMaxProbability = scan.MaxProbability;
            _lastScanMs = sw.ElapsedMilliseconds;
            _lastTilesScanned = scan.TilesScanned;

            if (scan.TilesDeferred > 0 || _lastScanMs > _config.PollIntervalSeconds * 1000)
            {
                Log.Info($"Scan took {_lastScanMs} ms: {scan.TilesScanned} scanned, " +
                         $"{scan.TilesReused} reused, {scan.TilesDeferred} deferred to next poll.");
            }

            bool isNsfw = scan.MaxProbability >= _config.DetectionThreshold;
            _escalation!.ReportDetection(isNsfw, scan.HotTileCount);

            // Fire on every poll that is still positive, at whatever level we are now at.
            // Gating on isNsfw is what guarantees that closing the content stops stimuli on the
            // very next poll, regardless of what the overlay level is still doing.
            if (isNsfw)
                await TryTriggerPavlokAsync(_escalation.Level);
        }
        catch (Exception ex)
        {
            Log.Error("Poll tick failed.", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnEscalationLevelChanged(int level)
    {
        Log.Info($"Escalation level -> {level}.");

        Dispatcher.Invoke(() =>
        {
            if (level == 0)
            {
                DetachOverlay(closeIt: true);
                return;
            }

            if (_overlay == null)
            {
                _overlay = new OverlayWindow(_config);
                _overlay.SitStarted += Overlay_SitStarted;
                _overlay.SitEnded += Overlay_SitEnded;
                _overlay.OverrideConfirmed += Overlay_OverrideConfirmed;
                _overlay.Closed += Overlay_Closed;
                _overlay.Show();
            }

            _overlay.SetLevel(level);
        });

        // No stimulus here on purpose. Tick() already fires one for this poll at this level, and
        // firing from both paths meant the two calls raced, with only the rate limiter keeping it
        // to a single stimulus by accident.
    }

    private void Overlay_SitStarted() => _pavlokSuppressedForSit = true;
    private void Overlay_SitEnded() => _pavlokSuppressedForSit = false;

    private void Overlay_Closed(object? sender, EventArgs e) => DetachOverlay(closeIt: false);

    private void DetachOverlay(bool closeIt)
    {
        var overlay = _overlay;
        _overlay = null;
        _pavlokSuppressedForSit = false;

        if (overlay == null) return;

        overlay.SitStarted -= Overlay_SitStarted;
        overlay.SitEnded -= Overlay_SitEnded;
        overlay.OverrideConfirmed -= Overlay_OverrideConfirmed;
        overlay.Closed -= Overlay_Closed;

        if (closeIt) overlay.ForceClose();
    }

    private void Overlay_OverrideConfirmed()
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (_pavlok == null || !_pavlok.IsLoggedIn)
                {
                    Notify("Pavlok not connected -- opening login dialog...", BalloonIcon.Info);
                    await InitializePavlokAsync();
                    if (_pavlok == null || !_pavlok.IsLoggedIn)
                    {
                        Notify("Pavlok still not connected. Cannot send override penalty.", BalloonIcon.Warning);
                        return;
                    }
                }

                await ApplyOverridePenaltyAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Override penalty flow failed.", ex);
            }
        });
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu
        {
            // Without the explicit style the tray menu renders in WPF's stock grey gradient,
            // which looks nothing like the rest of the app or the Windows 11 shell.
            Style = (Style)FindResource("ThemedContextMenu")
        };

        _statusItem = new MenuItem { Header = "Status", IsEnabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());

        // Pause is omitted entirely unless AllowPause is set. Quit (below) is always present, so
        // the app can still be stopped deliberately -- just not paused on impulse.
        if (_config.AllowPause)
        {
            _pauseItem = new MenuItem { Header = "Pause" };
            _pauseItem.Click += (_, _) => TogglePause();
            menu.Items.Add(_pauseItem);
        }

        var settingsItem = new MenuItem { Header = "Settings..." };
        settingsItem.Click += (_, _) => ShowSettings();
        menu.Items.Add(settingsItem);

        var startItem = new MenuItem { Header = "Start screen..." };
        startItem.Click += (_, _) => ShowStartScreen();
        menu.Items.Add(startItem);

        menu.Items.Add(new Separator());

        _autostartItem = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        _autostartItem.Click += (_, _) => SetAutostart(_autostartItem.IsChecked);
        menu.Items.Add(_autostartItem);

        var logsItem = new MenuItem { Header = "Open log folder" };
        logsItem.Click += (_, _) => OpenLogFolder();
        menu.Items.Add(logsItem);

        menu.Items.Add(new Separator());

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(quitItem);

        // Refresh live values as the menu opens rather than leaving whatever was there at build time.
        menu.Opened += (_, _) => RefreshTrayMenu();

        return menu;
    }

    private void ShowSettings()
    {
        try
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(
                _config, IsAutostartEnabled(), _pavlok?.IsLoggedIn == true,
                _classifier?.ExecutionProvider ?? "");

            var accepted = _settingsWindow.ShowDialog() == true;
            var result = _settingsWindow.Result;
            var autostart = _settingsWindow.AutostartRequested;
            var acceleratorChanged = _settingsWindow.AcceleratorChanged;
            _settingsWindow = null;

            if (!accepted || result == null) return;

            ApplyConfig(result);
            SetAutostart(autostart);

            if (acceleratorChanged) OfferRestart();
        }
        catch (Exception ex)
        {
            _settingsWindow = null;
            Log.Error("Settings window failed.", ex);
        }
    }

    /// <summary>
    /// Applies edited settings to the running app.
    ///
    /// Services hold a reference to the one shared AppConfig, so copying values onto it is what
    /// makes most changes take effect immediately. The three things that own derived state --
    /// the timer interval, the capture service's monitor selection, and the scanner's cached tile
    /// grid -- have to be rebuilt explicitly.
    /// </summary>
    private void ApplyConfig(AppConfig updated)
    {
        bool monitorsChanged = updated.CaptureAllMonitors != _config.CaptureAllMonitors;
        bool pavlokTurnedOn = updated.PavlokEnabled && !_config.PavlokEnabled;
        bool pavlokTurnedOff = !updated.PavlokEnabled && _config.PavlokEnabled;

        _config.CopyFrom(updated);

        if (_timer != null)
            _timer.Interval = TimeSpan.FromSeconds(_config.PollIntervalSeconds);

        if (monitorsChanged)
        {
            _capture?.Dispose();
            _capture = new ScreenCaptureService(_config.CaptureAllMonitors);
        }

        // Thresholds and tile geometry both changed underneath the cache; drop it so nothing is
        // judged against a probability computed under the old settings.
        _scanner?.Reset();

        if (pavlokTurnedOff)
        {
            _pavlok?.Dispose();
            _pavlok = null;
            Log.Info("Pavlok disabled from settings.");
        }
        else if (pavlokTurnedOn)
        {
            _ = InitializePavlokAsync();
        }

        if (!_config.TrySave("appsettings.json", out var saveError))
            Notify($"Settings applied for this session but could not be saved: {saveError}", BalloonIcon.Warning);
    }

    /// <summary>
    /// Offers to restart after an accelerator change. A restart is genuinely required: the ONNX
    /// Runtime native library is already loaded and cannot be replaced in a live process.
    /// </summary>
    private void OfferRestart()
    {
        var restart = ConfirmDialog.Show(
            null,
            "Restart to switch accelerator",
            "The inference runtime can only be changed while it is not loaded, so Rewire Guard " +
            "needs to restart. Monitoring resumes as soon as it comes back.",
            confirmText: "Restart now",
            cancelText: "Later");

        if (!restart) return;

        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Notify("Could not determine the executable path; restart manually.", BalloonIcon.Warning);
                return;
            }

            // Release the single-instance mutex before the new process tries to claim it,
            // otherwise the replacement exits immediately as a duplicate.
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;

            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Restart failed.", ex);
            Notify("Could not restart automatically. Quit and reopen Rewire Guard.", BalloonIcon.Warning);
        }
    }

    private void ShowStartScreen()
    {
        try
        {
            if (_startWindow != null)
            {
                _startWindow.Activate();
                return;
            }

            _startWindow = new StartWindow(_classifier != null, _pavlok?.IsLoggedIn == true, ResolvedModelPath());
            var connected = _startWindow.ShowDialog() == true;
            var token = _startWindow.Token;
            var wantsSettings = _startWindow.SettingsRequested;
            var gotModel = _startWindow.ModelDownloaded;
            _startWindow = null;

            if (connected && !string.IsNullOrWhiteSpace(token))
            {
                TokenStore.Save(token);
                _pavlok ??= new PavlokClient();
                _pavlok.UseExistingToken(token);
                Notify("Pavlok connected.", BalloonIcon.Info);
            }

            // A model arriving mid-session is the whole point of the download button; load it and
            // start polling rather than telling the user to restart.
            if (gotModel && _classifier == null && TryLoadClassifier())
            {
                StartMonitoring();
                Notify("Model installed. Monitoring is active.", BalloonIcon.Info);
            }

            if (wantsSettings) ShowSettings();
        }
        catch (Exception ex)
        {
            _startWindow = null;
            Log.Error("Start screen failed.", ex);
        }
    }

    private void RefreshTrayMenu()
    {
        if (_statusItem != null)
        {
            var state = _paused ? "Paused"
                : _classifier == null ? "No model loaded"
                : $"Watching - level {_escalation?.Level ?? 0}";

            var detail = _classifier == null
                ? ""
                : $"  |  {_classifier.ExecutionProvider}  |  last {_lastMaxProbability:P0} " +
                  $"({_lastTilesScanned} tiles, {_lastScanMs} ms)";

            var pavlok = !_config.PavlokEnabled ? "  |  Pavlok off"
                : _pavlok?.IsLoggedIn == true ? "  |  Pavlok connected"
                : "  |  Pavlok not connected";

            _statusItem.Header = state + detail + pavlok;
        }

        if (_pauseItem != null) _pauseItem.Header = _paused ? "Resume" : "Pause";
        if (_autostartItem != null) _autostartItem.IsChecked = IsAutostartEnabled();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        Log.Info(_paused ? "Paused." : "Resumed.");

        if (_trayIcon != null)
            _trayIcon.ToolTipText = _paused ? "Rewire Guard (paused)" : "Rewire Guard";

        if (_pauseItem != null) _pauseItem.Header = _paused ? "Resume" : "Pause";

        if (_paused)
        {
            DetachOverlay(closeIt: true);

            // Reset rather than just hiding the overlay. Level used to survive the pause, so the
            // first positive poll after resuming fired a level 3 stimulus with no ramp-up -- and
            // no overlay, because the level had not changed and so raised no event.
            _escalation?.Reset();
            _scanner?.Reset();
        }
    }

    private static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutostartKey, writable: false);
            return key?.GetValue(AutostartValue) != null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the autostart registry value: {ex.Message}");
            return false;
        }
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutostartKey, writable: true);
            if (key == null)
            {
                Notify("Could not open the Windows startup registry key.", BalloonIcon.Warning);
                return;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    Notify("Could not determine the executable path for autostart.", BalloonIcon.Warning);
                    return;
                }

                key.SetValue(AutostartValue, $"\"{exePath}\"");
                Log.Info($"Autostart enabled -> {exePath}");
            }
            else
            {
                key.DeleteValue(AutostartValue, throwOnMissingValue: false);
                Log.Info("Autostart disabled.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to update the autostart setting.", ex);
            Notify($"Could not change the autostart setting: {ex.Message}", BalloonIcon.Warning);
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            Process.Start(new ProcessStartInfo { FileName = Log.Directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the log folder.", ex);
        }
    }

    private void Notify(string message, BalloonIcon icon)
    {
        try { _trayIcon?.ShowBalloonTip("Rewire Guard", message, icon); }
        catch (Exception ex) { Log.Warn($"Balloon tip failed: {ex.Message}"); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        Log.Info("Shutting down.");

        _timer?.Stop();
        DetachOverlay(closeIt: true);
        _classifier?.Dispose();
        _capture?.Dispose();
        _pavlok?.Dispose();
        _trayIcon?.Dispose();

        _singleInstanceMutex?.Dispose();

        Log.Shutdown();
        base.OnExit(e);
    }
}
