using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RewireGuard.Services;

namespace RewireGuard.UI;

/// <summary>
/// Full-screen overlay used to intervene at escalation levels 1-3. Sized to the whole virtual
/// desktop rather than maximized, so a second monitor is not left showing the content the
/// overlay exists to interrupt.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly AppConfig _config;

    // When true, Deactivated handler will not force the window back to foreground.
    private bool _suppressReactivation;

    // When true, suppression remains in effect until the window actually closes
    // to avoid races when Close() is queued but Deactivated fires before close runs.
    private bool _suppressReactivationUntilClosed;

    // Tracks whether we're already running the closing animation so OnClosing
    // doesn't cancel the actual close call.
    private bool _isClosing;

    // Set by ForceClose so app shutdown is never blocked waiting on a fade-out.
    private bool _forceClosing;

    private int _currentLevel;

    // Sit-with-it timer (used for levels 2 and 3).
    private DispatcherTimer? _sitTimer;
    private int _sitSecondsRemaining;
    private int _sitSecondsTotal;

    // Events consumed by hosting code to suppress Pavlok while user "sits with it".
    public event Action? SitStarted;
    public event Action? SitEnded;
    public event Action? OverrideConfirmed;

    public OverlayWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();

        // Start hidden for the fade-in.
        Opacity = 0;

        CloseTabButton.Click += async (_, _) => await CloseActiveTabAsync();
        OverrideButton.Click += (_, _) => OverrideAction();

        if (_config.OverlayFocusLock)
        {
            Deactivated += OverlayWindow_Deactivated;
            PreviewKeyDown += OverlayWindow_PreviewKeyDown;
        }

        SourceInitialized += (_, _) => CoverAllMonitors();
        Loaded += (_, _) => StartFadeIn();
    }

    /// <summary>
    /// Spans every monitor. WindowState=Maximized only ever covers the display the window
    /// happens to be on, which on a multi-monitor desk left the other screens fully usable.
    /// SystemParameters.VirtualScreen* is already in device-independent units, so this stays
    /// correct under display scaling without any manual DPI maths.
    /// </summary>
    private void CoverAllMonitors()
    {
        try
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not size overlay to the virtual desktop: {ex.Message}");
            WindowState = WindowState.Maximized;
        }
    }

    private void StartFadeIn()
    {
        var anim = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void StartFadeOutAndClose()
    {
        if (_isClosing) return;

        var anim = new DoubleAnimation(Opacity, 0.0, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };

        anim.Completed += (_, _) =>
        {
            _isClosing = true;
            Dispatcher.BeginInvoke((Action)(() => Close()));
        };

        BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>
    /// Closes immediately, skipping the fade-out.
    ///
    /// OnClosing cancels the first close request so it can animate, and re-issues the real close
    /// from the animation's Completed callback. During Application.Shutdown() nothing pumps that
    /// callback to completion reliably, so the cancel could leave the process alive with its tray
    /// icon gone. Quit and pause go through here instead.
    /// </summary>
    public void ForceClose()
    {
        _forceClosing = true;
        _isClosing = true;
        BeginAnimation(OpacityProperty, null);   // drop any in-flight animation holding Opacity
        try { Close(); }
        catch (Exception ex) { Log.Warn($"ForceClose failed: {ex.Message}"); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isClosing && !_forceClosing)
        {
            e.Cancel = true;
            StartFadeOutAndClose();
            return;
        }

        StopSitTimer();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _suppressReactivation = false;
        _suppressReactivationUntilClosed = false;
        base.OnClosed(e);
    }

    private void OverlayWindow_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Swallow common window-switching keys that can move focus away (best-effort).
        if ((e.KeyboardDevice.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt &&
            (e.SystemKey == Key.Tab || e.SystemKey == Key.F4))
        {
            e.Handled = true;
        }

        // Keyboard shortcuts, but only for buttons that are actually actionable right now --
        // the old handler fired Click on disabled buttons during a sit.
        if (e.Key == Key.C && CloseTabButton.IsEnabled && CloseTabButton.IsVisible)
        {
            e.Handled = true;
            CloseTabButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        else if (e.Key == Key.O && OverrideButton.IsEnabled && OverrideButton.IsVisible)
        {
            e.Handled = true;
            OverrideButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
    }

    private void OverlayWindow_Deactivated(object? sender, EventArgs e)
    {
        if (_suppressReactivation || _suppressReactivationUntilClosed || _isClosing) return;

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            }), DispatcherPriority.Input);
        }
        catch { /* best-effort */ }
    }

    public void SetLevel(int level)
    {
        _currentLevel = level;

        // An escalation while the user is mid-sit ends the sit.
        StopSitTimer();

        // Must run after StopSitTimer: StartSitTimer disables both buttons, and nothing used to
        // re-enable them. A level change during a sit therefore left an overlay whose only two
        // controls were permanently greyed out -- no way to close the tab, no way to override.
        RestoreActionButtons();

        var accent = AccentFor(level);

        switch (level)
        {
            case 1:
                Backdrop.Opacity = 0.50;
                AccentWash.Opacity = 0.08;
                MessageText.Text = "It's not worth it.";
                SubText.Text = "Close the tab and this clears on its own.";
                break;
            case 2:
                Backdrop.Opacity = 0.72;
                AccentWash.Opacity = 0.12;
                MessageText.Text = "Take a breath. You can turn back.";
                SubText.Text = "Close the tab now and sit with it for " +
                               $"{_config.SitSecondsLevel2} seconds before you carry on.";
                break;
            default: // level 3+
                Backdrop.Opacity = 0.90;
                AccentWash.Opacity = 0.16;
                MessageText.Text = "Step away for a minute.";
                SubText.Text = "This clears on its own once you do. Closing the tab here starts a " +
                               $"{_config.SitSecondsLevel3}-second pause.";
                break;
        }

        int shown = Math.Clamp(level, 1, 3);
        LevelText.Text = $"LEVEL {shown}";

        Dot.Fill = accent;
        WashStop.Color = accent.Color;
        CloseTabButton.Tag = accent;

        // Dark ink reads well on the amber and orange accents but goes muddy on the level 3 red,
        // so the primary button flips to white there.
        CloseTabButton.Foreground = shown >= 3
            ? Brushes.White
            : new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x13));

        // Segments fill up to the current level; the rest stay inert.
        var inert = (Brush)FindResource("BorderBrushSoft");
        Seg1.Background = shown >= 1 ? accent : inert;
        Seg2.Background = shown >= 2 ? accent : inert;
        Seg3.Background = shown >= 3 ? accent : inert;
    }

    private SolidColorBrush AccentFor(int level) => (SolidColorBrush)FindResource(level switch
    {
        1 => "AccentLevel1Brush",
        2 => "AccentLevel2Brush",
        _ => "AccentLevel3Brush",
    });

    private void RestoreActionButtons()
    {
        CloseTabButton.Visibility = Visibility.Visible;
        CloseTabButton.IsEnabled = true;
        OverrideButton.Visibility = Visibility.Visible;
        OverrideButton.IsEnabled = true;
        SitPanel.Visibility = Visibility.Collapsed;
    }

    private void OverrideAction()
    {
        // Prevent the Deactivated handler from stealing focus while the dialog is up.
        _suppressReactivation = true;
        try
        {
            var stimulus = $"{_config.PavlokLevel3Type} at intensity {_config.PavlokLevel3Value}";
            var ok = ConfirmDialog.Show(
                this,
                "Confirm override",
                $"This clears the warning and immediately applies a level 3 {stimulus}.",
                confirmText: "Override anyway",
                cancelText: "Go back");

            if (!ok)
            {
                _suppressReactivation = false;
                return;
            }

            _suppressReactivationUntilClosed = true;

            try { OverrideConfirmed?.Invoke(); }
            catch (Exception ex) { Log.Error("OverrideConfirmed handler threw.", ex); }

            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Override action failed.", ex);
            try { if (!IsVisible) Show(); } catch { /* best-effort */ }
            _suppressReactivation = false;
            _suppressReactivationUntilClosed = false;
        }
    }

    private async Task CloseActiveTabAsync()
    {
        CloseTabButton.IsEnabled = false;
        _suppressReactivation = true;

        try
        {
            // Hide the overlay briefly so the real foreground window can become active.
            Hide();
            await Task.Delay(120);

            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                Show();
                CloseTabButton.IsEnabled = true;
                return;
            }

            if (IsProtected(hwnd, out var processName))
            {
                // Ctrl+W closes the open document in Word, the current file in an IDE, and the
                // active tab (plus whatever is running in it) in a terminal. Everywhere else it
                // closes a tab or window and costs nothing, so only these are held back.
                Show();
                CloseTabButton.IsEnabled = true;
                SubText.Text = $"Not closing anything in {processName} — Ctrl+W would discard your work there. " +
                               "Close it yourself, or edit ProtectedProcessNames in appsettings.json.";
                Log.Info($"Refused to send Ctrl+W to protected process '{processName}'.");
                return;
            }

            SetForegroundWindow(hwnd);
            SendCtrlW();
            Log.Info($"Sent Ctrl+W to '{processName}'.");

            // Level 1: dismiss the overlay. Levels 2/3: stay up and run the sit countdown.
            if (_currentLevel <= 1)
            {
                _suppressReactivationUntilClosed = true;
                await Dispatcher.InvokeAsync(() =>
                {
                    try { Close(); } catch { /* best-effort */ }
                });
                return;
            }

            await Task.Delay(120);
            Show();
            SetForegroundWindow(new WindowInteropHelper(this).Handle);

            StartSitTimer(_currentLevel == 2 ? _config.SitSecondsLevel2 : _config.SitSecondsLevel3);
        }
        catch (Exception ex)
        {
            Log.Error("CloseActiveTab failed.", ex);
            try { Show(); CloseTabButton.IsEnabled = true; } catch { /* best-effort */ }
        }
        finally
        {
            if (!_suppressReactivationUntilClosed)
                _suppressReactivation = false;
        }
    }

    /// <summary>
    /// True when the foreground window belongs to an application where Ctrl+W would close a
    /// document rather than a tab.
    ///
    /// If the process cannot be identified this returns false, so the keystroke is still sent.
    /// The protected list is a short, specific set of applications; an unidentifiable window is
    /// far more likely to be an ordinary one than a copy of Word, and refusing in that case would
    /// make the button quietly stop working for no visible reason.
    /// </summary>
    private bool IsProtected(IntPtr hwnd, out string processName)
    {
        processName = "unknown";
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            processName = name;

            return _config.ProtectedProcessNames
                .Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not identify the foreground process: {ex.Message}");
            return false;
        }
    }

    private void StartSitTimer(int seconds)
    {
        StopSitTimer();

        _sitSecondsTotal = Math.Max(1, seconds);
        _sitSecondsRemaining = _sitSecondsTotal;

        CloseTabButton.IsEnabled = false;
        OverrideButton.IsEnabled = false;
        SitPanel.Visibility = Visibility.Visible;

        UpdateSitMessage();

        _sitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sitTimer.Tick += SitTimer_Tick;
        _sitTimer.Start();

        try { SitStarted?.Invoke(); }
        catch (Exception ex) { Log.Error("SitStarted handler threw.", ex); }
    }

    private void SitTimer_Tick(object? sender, EventArgs e)
    {
        _sitSecondsRemaining--;
        if (_sitSecondsRemaining <= 0)
        {
            StopSitTimer();
            _suppressReactivationUntilClosed = true;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                try { Close(); }
                catch (Exception ex) { Log.Error("Sit timer close failed.", ex); }
            }));
            return;
        }

        UpdateSitMessage();
    }

    private void UpdateSitMessage()
    {
        MessageText.Text = "Good — you closed the tab.";
        SubText.Text = "Stay here for a moment before you carry on.";
        SitCountdown.Text = $"{_sitSecondsRemaining}s";

        // Width-driven fill rather than a ProgressBar: the stock control ignores the themed
        // corner radius and paints its own chrome underneath.
        double progress = 1.0 - ((double)_sitSecondsRemaining / _sitSecondsTotal);
        SitFill.Width = Math.Max(0, SitTrack.ActualWidth * progress);
    }

    private void StopSitTimer()
    {
        if (_sitTimer == null) return;

        _sitTimer.Stop();
        _sitTimer.Tick -= SitTimer_Tick;
        _sitTimer = null;
        SitPanel.Visibility = Visibility.Collapsed;

        try { SitEnded?.Invoke(); }
        catch (Exception ex) { Log.Error("SitEnded handler threw.", ex); }
    }

    #region Native input

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_W = 0x57;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Sends Ctrl+W as one atomic SendInput batch. keybd_event (the previous approach) is
    /// deprecated, and issuing four separate calls let other input interleave between the
    /// modifier and the key.
    /// </summary>
    private static void SendCtrlW()
    {
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, false),
            KeyInput(VK_W, false),
            KeyInput(VK_W, true),
            KeyInput(VK_CONTROL, true),
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            Log.Warn($"SendInput delivered {sent} of {inputs.Length} events (error {Marshal.GetLastWin32Error()}).");
    }

    private static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    #endregion
}
