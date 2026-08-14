using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RewireGuard.Services;

namespace RewireGuard.UI;

/// <summary>
/// Options window. Edits a clone of the live config so Cancel is genuinely free; on Save the
/// values are validated, copied onto the shared instance (which every service holds a reference
/// to, making the change immediate) and written to appsettings.json.
/// </summary>
public partial class SettingsWindow : ThemedWindow
{
    private readonly AppConfig _draft;

    /// <summary>Populated on Save so the host can re-apply anything that needs more than a field write.</summary>
    public AppConfig? Result { get; private set; }

    private readonly string _acceleratorAtOpen;

    public SettingsWindow(AppConfig current, bool autostartEnabled, bool pavlokConnected,
                          string activeProvider = "")
    {
        InitializeComponent();

        _draft = current.Clone();
        _acceleratorAtOpen = _draft.Accelerator;
        SetUpAccelerator(activeProvider);

        MakeDraggable(HeaderBar);
        CloseButton.Click += (_, _) => Close();
        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => Save();
        OpenLogsButton.Click += (_, _) => OpenLogs();

        DiagnosticsHint.Text = Log.Directory;
        PavlokStatusHint.Text = pavlokConnected
            ? "Device connected."
            : "No device connected. Stimuli are skipped until you connect one.";

        LoadInto(autostartEnabled);
        WireLiveLabels();
    }

    /// <summary>
    /// Populates the accelerator picker, hiding options this install cannot actually provide.
    /// A single-runtime build (portable, or a plain dotnet build) ships one runtime baked in, so
    /// offering a choice there would be a lie.
    /// </summary>
    private void SetUpAccelerator(string activeProvider)
    {
        if (!AcceleratorRuntime.IsSupported)
        {
            AcceleratorCard.Visibility = Visibility.Collapsed;
            return;
        }

        var installed = AcceleratorRuntime.Installed();
        AccelNpu.IsEnabled = installed.Contains("OpenVINO", StringComparer.OrdinalIgnoreCase);
        AccelGpu.IsEnabled = installed.Contains("DirectML", StringComparer.OrdinalIgnoreCase);
        AccelCpu.IsEnabled = installed.Contains("CPU", StringComparer.OrdinalIgnoreCase);

        switch (_draft.AcceleratorChoice)
        {
            case AcceleratorChoice.OpenVINO: AccelNpu.IsChecked = true; break;
            case AcceleratorChoice.DirectML: AccelGpu.IsChecked = true; break;
            case AcceleratorChoice.Cpu: AccelCpu.IsChecked = true; break;
            default: AccelAuto.IsChecked = true; break;
        }

        AcceleratorHint.Text = AcceleratorRuntime.HasNpu()
            ? "Auto prefers your NPU, which keeps the GPU free for games and other work."
            : "No NPU detected, so Auto uses your GPU.";

        AcceleratorNote.Text = string.IsNullOrEmpty(activeProvider)
            ? "Takes effect after a restart."
            : $"Currently running on {activeProvider}. Takes effect after a restart.";

        foreach (var option in new[] { AccelAuto, AccelNpu, AccelGpu, AccelCpu })
            option.Checked += (_, _) => UpdateRestartNote(activeProvider);
    }

    private void UpdateRestartNote(string activeProvider)
    {
        bool changed = ReadAccelerator() != _acceleratorAtOpen;
        AcceleratorNote.Text = changed
            ? "Restart Rewire Guard to switch. The runtime cannot be changed while it is loaded."
            : string.IsNullOrEmpty(activeProvider)
                ? "Takes effect after a restart."
                : $"Currently running on {activeProvider}. Takes effect after a restart.";
    }

    private string ReadAccelerator() =>
        AccelNpu.IsChecked == true ? "OpenVINO"
        : AccelGpu.IsChecked == true ? "DirectML"
        : AccelCpu.IsChecked == true ? "CPU"
        : "Auto";

    private void LoadInto(bool autostartEnabled)
    {
        PollSlider.Value = _draft.PollIntervalSeconds;
        ThresholdSlider.Value = _draft.DetectionThreshold;
        AllMonitorsToggle.IsChecked = _draft.CaptureAllMonitors;
        TilingToggle.IsChecked = _draft.TilingEnabled;
        ChangeDetectionToggle.IsChecked = _draft.ChangeDetectionEnabled;

        HitsSlider.Value = _draft.ConsecutiveHitsToEscalate;
        CleanSlider.Value = _draft.CleanMinutesToReset;
        Sit2Box.Text = _draft.SitSecondsLevel2.ToString(CultureInfo.InvariantCulture);
        Sit3Box.Text = _draft.SitSecondsLevel3.ToString(CultureInfo.InvariantCulture);

        PavlokToggle.IsChecked = _draft.PavlokEnabled;
        CadenceSlider.Value = _draft.PavlokMinSecondsBetweenStimuli;

        SetSegment(_draft.PavlokLevel1Type, L1Vibe, L1Beep, L1Zap);
        SetSegment(_draft.PavlokLevel2Type, L2Vibe, L2Beep, L2Zap);
        SetSegment(_draft.PavlokLevel3Type, L3Vibe, L3Beep, L3Zap);
        L1Slider.Value = _draft.PavlokLevel1Value;
        L2Slider.Value = _draft.PavlokLevel2Value;
        L3Slider.Value = _draft.PavlokLevel3Value;

        AutostartToggle.IsChecked = autostartEnabled;
        FocusLockToggle.IsChecked = _draft.OverlayFocusLock;
    }

    /// <summary>
    /// Keeps the numeric readouts and the derived "what this actually means" hints in sync. The
    /// escalation hint matters most: the gap between "2 polls" and time-to-level-3 is the single
    /// setting most likely to surprise someone.
    /// </summary>
    private void WireLiveLabels()
    {
        PollSlider.ValueChanged += (_, _) => { UpdatePollLabels(); UpdateRampHint(); UpdateCadenceHint(); };
        ThresholdSlider.ValueChanged += (_, _) => ThresholdValue.Text = $"{ThresholdSlider.Value:P0}";
        HitsSlider.ValueChanged += (_, _) => { HitsValue.Text = $"{(int)HitsSlider.Value}"; UpdateRampHint(); };
        CleanSlider.ValueChanged += (_, _) => CleanValue.Text = FormatMinutes(CleanSlider.Value);
        CadenceSlider.ValueChanged += (_, _) => { CadenceValue.Text = $"{(int)CadenceSlider.Value}s"; UpdateCadenceHint(); };

        L1Slider.ValueChanged += (_, _) => L1ValueText.Text = $"intensity {(int)L1Slider.Value}";
        L2Slider.ValueChanged += (_, _) => L2ValueText.Text = $"intensity {(int)L2Slider.Value}";
        L3Slider.ValueChanged += (_, _) => L3ValueText.Text = $"intensity {(int)L3Slider.Value}";

        PavlokToggle.Checked += (_, _) => UpdatePavlokEnabledState();
        PavlokToggle.Unchecked += (_, _) => UpdatePavlokEnabledState();

        UpdatePollLabels();
        ThresholdValue.Text = $"{ThresholdSlider.Value:P0}";
        HitsValue.Text = $"{(int)HitsSlider.Value}";
        CleanValue.Text = FormatMinutes(CleanSlider.Value);
        CadenceValue.Text = $"{(int)CadenceSlider.Value}s";
        L1ValueText.Text = $"intensity {(int)L1Slider.Value}";
        L2ValueText.Text = $"intensity {(int)L2Slider.Value}";
        L3ValueText.Text = $"intensity {(int)L3Slider.Value}";
        UpdateRampHint();
        UpdateCadenceHint();
        UpdatePavlokEnabledState();
    }

    private void UpdatePollLabels()
    {
        int seconds = (int)PollSlider.Value;
        PollValue.Text = seconds == 1 ? "every second" : $"every {seconds}s";
        PollHint.Text = seconds <= 2
            ? "Fast, but each poll costs a full model pass."
            : "How often the screen is checked.";
    }

    private void UpdateRampHint()
    {
        int hits = (int)HitsSlider.Value;
        int poll = (int)PollSlider.Value;

        // Level 1 on the first hit, then one level per `hits` further polls -> 2*hits + 1 polls
        // from first detection to level 3.
        int pollsToMax = (2 * hits) + 1;
        RampHint.Text = $"About {pollsToMax * poll}s of continuous detection to reach level 3.";
    }

    private void UpdateCadenceHint()
    {
        int cadence = (int)CadenceSlider.Value;
        int poll = (int)PollSlider.Value;
        int effective = Math.Max(cadence, poll);

        CadenceHint.Text = $"A stimulus fires on every poll that is still positive, so in practice " +
                           $"about one every {effective}s while content is on screen.";
    }

    private void UpdatePavlokEnabledState()
    {
        bool on = PavlokToggle.IsChecked == true;
        PavlokDetail.IsEnabled = on;
        PavlokDetail.Opacity = on ? 1.0 : 0.45;
    }

    private static string FormatMinutes(double minutes) =>
        minutes <= 0 ? "immediately"
        : minutes < 1 ? $"{minutes * 60:0}s"
        : $"{minutes:0.#} min";

    private static void SetSegment(string type, RadioButton vibe, RadioButton beep, RadioButton zap)
    {
        switch (type?.Trim().ToLowerInvariant())
        {
            case "beep": beep.IsChecked = true; break;
            case "zap": zap.IsChecked = true; break;
            default: vibe.IsChecked = true; break;
        }
    }

    private static string ReadSegment(RadioButton vibe, RadioButton beep, RadioButton zap) =>
        beep.IsChecked == true ? "beep" : zap.IsChecked == true ? "zap" : vibe.IsChecked == true ? "vibe" : "vibe";

    private void Save()
    {
        _draft.PollIntervalSeconds = (int)PollSlider.Value;
        _draft.DetectionThreshold = Math.Round(ThresholdSlider.Value, 2);
        _draft.CaptureAllMonitors = AllMonitorsToggle.IsChecked == true;
        _draft.TilingEnabled = TilingToggle.IsChecked == true;
        _draft.ChangeDetectionEnabled = ChangeDetectionToggle.IsChecked == true;

        _draft.ConsecutiveHitsToEscalate = (int)HitsSlider.Value;
        _draft.CleanMinutesToReset = CleanSlider.Value;
        _draft.SitSecondsLevel2 = ParseSeconds(Sit2Box.Text, _draft.SitSecondsLevel2);
        _draft.SitSecondsLevel3 = ParseSeconds(Sit3Box.Text, _draft.SitSecondsLevel3);

        _draft.PavlokEnabled = PavlokToggle.IsChecked == true;
        _draft.PavlokMinSecondsBetweenStimuli = (int)CadenceSlider.Value;
        _draft.PavlokLevel1Type = ReadSegment(L1Vibe, L1Beep, L1Zap);
        _draft.PavlokLevel2Type = ReadSegment(L2Vibe, L2Beep, L2Zap);
        _draft.PavlokLevel3Type = ReadSegment(L3Vibe, L3Beep, L3Zap);
        _draft.PavlokLevel1Value = (int)L1Slider.Value;
        _draft.PavlokLevel2Value = (int)L2Slider.Value;
        _draft.PavlokLevel3Value = (int)L3Slider.Value;

        _draft.OverlayFocusLock = FocusLockToggle.IsChecked == true;

        if (AcceleratorRuntime.IsSupported)
        {
            var chosen = ReadAccelerator();
            if (chosen != _draft.Accelerator)
            {
                _draft.Accelerator = chosen;
                // The DirectML adapter probe result belongs to the old runtime; force a re-probe.
                _draft.DirectMLDeviceId = -1;
                AcceleratorChanged = true;
            }
        }

        // Same validator the config loader runs, so the UI cannot write something the app would
        // then quietly clamp behind the user's back.
        _draft.Validated();

        AutostartRequested = AutostartToggle.IsChecked == true;
        Result = _draft;
        DialogResult = true;
    }

    /// <summary>Autostart lives in the registry, not the config file, so it is reported separately.</summary>
    public bool AutostartRequested { get; private set; }

    /// <summary>Set when the accelerator changed, so the host can offer a restart.</summary>
    public bool AcceleratorChanged { get; private set; }

    private static int ParseSeconds(string text, int fallback) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private void OpenLogs()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Log.Directory);
            Process.Start(new ProcessStartInfo { FileName = Log.Directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the log folder.", ex);
            SaveStatus.Text = "Could not open the log folder.";
        }
    }
}
