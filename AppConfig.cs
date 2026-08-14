using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RewireGuard.Services;

namespace RewireGuard;

public class AppConfig
{
    /// <summary>How often to grab a screenshot and classify it.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>Path to the exported ONNX model, relative to the app's output directory.</summary>
    public string ModelPath { get; set; } = "Models/model.onnx";

    /// <summary>Model's expected square input size (AdamCodd/vit-base-nsfw-detector uses 384).</summary>
    public int InputSize { get; set; } = 384;

    /// <summary>
    /// Index of the "nsfw" class in the model's output. Check the model's config.json id2label
    /// mapping after exporting -- HF label ordering isn't guaranteed to be [sfw, nsfw].
    /// </summary>
    public int NsfwLabelIndex { get; set; } = 1;

    /// <summary>Probability threshold above which the coarse whole-frame pass counts as a detection.</summary>
    public double DetectionThreshold { get; set; } = 0.60;

    /// <summary>Master on/off switch for tiled scanning. Coarse whole-frame pass always runs regardless.</summary>
    public bool TilingEnabled { get; set; } = true;

    /// <summary>
    /// Probability threshold for an individual tile (or the coarse pass) to count as "hot".
    /// Kept separate from DetectionThreshold and set slightly higher by default: running many
    /// tiles per poll gives many independent chances to false-positive, so a single hot tile
    /// needs more confidence behind it than a single whole-frame classification does.
    /// </summary>
    public double TileDetectionThreshold { get; set; } = 0.75;

    /// <summary>Square tile edge length in screen pixels, before the tile is resized down to InputSize.</summary>
    public int TileSize { get; set; } = 500;

    /// <summary>Fraction of tile size that adjacent tiles overlap by, so no content region falls entirely in a seam.</summary>
    public double TileOverlapFraction { get; set; } = 0.3;

    /// <summary>
    /// Fraction of screen width, centered, that tiles are generated over. Full screen height is
    /// always covered. Meant to roughly track a browser's content column and skip chrome/sidebars
    /// -- adjust to match your actual window layout.
    /// </summary>
    public double ContentRegionWidthFraction { get; set; } = 1.0;

    /// <summary>
    /// Hard ceiling on tile inferences per poll, after change detection has pruned the list.
    /// A ViT-base pass is expensive; without a cap, a large or multi-monitor desktop can queue
    /// more work per poll than fits in the poll interval. Tiles over the cap are deferred to
    /// the next poll rather than dropped, so coverage stays complete over a few polls.
    /// </summary>
    public int MaxTilesPerPoll { get; set; } = 12;

    /// <summary>
    /// Skip re-classifying tiles whose pixels are unchanged since the previous poll, and reuse
    /// the previous probability for them. A static desktop then costs almost nothing. Turn off
    /// if you suspect the change detector is masking detections.
    /// </summary>
    public bool ChangeDetectionEnabled { get; set; } = true;

    /// <summary>
    /// Mean per-pixel luma delta (0-255) above which a tile counts as changed. Low enough to
    /// catch a scroll or a new image, high enough to ignore a blinking cursor or clock tick.
    /// </summary>
    public double ChangeDetectionThreshold { get; set; } = 1.5;

    /// <summary>Capture every monitor (virtual desktop) rather than just the primary display.</summary>
    public bool CaptureAllMonitors { get; set; } = true;

    /// <summary>
    /// Inference runtime: Auto, OpenVINO, DirectML or CPU. Takes effect on the next launch,
    /// because the native runtime cannot be replaced once it is loaded.
    ///
    /// Auto prefers an NPU when one is present. A discrete GPU is faster, but this app polls
    /// continuously, and while a game is running every tile changes every frame, so the full
    /// inference budget runs on every poll. That is sustained load taken from whatever is on
    /// screen; an NPU is otherwise idle and costs the GPU nothing.
    /// </summary>
    public string Accelerator { get; set; } = "Auto";

    /// <summary>
    /// Which DirectML adapter to run inference on. -1 means measure each one at first startup and
    /// keep the fastest, which is then written back here so later launches skip the probe.
    ///
    /// This is not cosmetic. DirectML adapter 0 is usually the integrated GPU, so simply taking
    /// the default costs a laptop with a discrete card most of its performance -- measured on one
    /// Core Ultra 7 155H with an RTX 4060, adapter 0 (Arc iGPU) ran 144 ms per inference against
    /// adapter 1 (RTX 4060) at 37 ms.
    /// </summary>
    public int DirectMLDeviceId { get; set; } = -1;

    /// <summary>How many consecutive positive frames before escalation level increases.</summary>
    public int ConsecutiveHitsToEscalate { get; set; } = 2;

    /// <summary>Minutes of clean frames required before escalation resets to level 0.</summary>
    public double CleanMinutesToReset { get; set; } = 2;

    /// <summary>
    /// Master switch for the Pavlok integration. Credentials come from PAVLOK_API_KEY (preferred)
    /// or PAVLOK_EMAIL/PAVLOK_PASSWORD environment variables, never from this file.
    /// </summary>
    public bool PavlokEnabled { get; set; } = true;

    /// <summary>
    /// Minimum seconds between stimuli. A stimulus fires on every poll that is still positive
    /// (not only when the level increases), so this is the real cadence limiter -- with the
    /// default poll interval it works out to roughly one stimulus per poll while content is up.
    /// </summary>
    public double PavlokMinSecondsBetweenStimuli { get; set; } = 3;

    public string PavlokLevel1Type { get; set; } = "vibe";
    public int PavlokLevel1Value { get; set; } = 100;

    public string PavlokLevel2Type { get; set; } = "zap";
    public int PavlokLevel2Value { get; set; } = 30;

    public string PavlokLevel3Type { get; set; } = "zap";
    public int PavlokLevel3Value { get; set; } = 70;

    /// <summary>Seconds the overlay makes you wait after closing the tab, at level 2 / level 3.</summary>
    public int SitSecondsLevel2 { get; set; } = 20;
    public int SitSecondsLevel3 { get; set; } = 60;

    /// <summary>
    /// Pull the overlay back to the foreground if it loses focus, and swallow Alt+Tab / Alt+F4.
    /// Was hardcoded off for debugging; off remains the default because a focus-stealing loop is
    /// genuinely unpleasant if anything else goes wrong.
    /// </summary>
    public bool OverlayFocusLock { get; set; } = false;

    /// <summary>
    /// Applications that "Close active tab" will never send Ctrl+W to. Process names, no .exe.
    ///
    /// Ctrl+W is sent to whatever window has focus, because in browsers, File Explorer, image
    /// viewers, chat clients and most other things it closes a tab or window and costs nothing.
    /// This list is the exception: in these applications the same keystroke closes a document,
    /// project or session, which can discard unsaved work or kill a running process.
    ///
    /// Empty the list to send Ctrl+W everywhere with no exceptions.
    /// </summary>
    public List<string> ProtectedProcessNames { get; set; } = new()
    {
        // Office: closes the document
        "winword", "excel", "powerpnt", "onenote", "msaccess", "outlook", "visio", "mspub",

        // Editors and IDEs: closes the file or the project
        "devenv", "code", "code - insiders", "vscodium", "rider64", "idea64", "pycharm64",
        "webstorm64", "phpstorm64", "clion64", "goland64", "rubymine64", "datagrip64",
        "studio64", "eclipse", "netbeans", "sublime_text", "notepad++", "notepad",

        // Terminals: closes the tab and takes any running process with it
        "windowsterminal", "wt", "powershell", "pwsh", "cmd", "conhost", "mintty", "alacritty",

        // Creative tools: closes the open document
        "photoshop", "illustrator", "indesign", "premiere", "afterfx", "blender", "krita", "gimp"
    };

    /// <summary>
    /// Obsolete allowlist, replaced by <see cref="ProtectedProcessNames"/>. Read only so an older
    /// appsettings.json does not fail to parse; the value is ignored, since an allowlist cannot be
    /// meaningfully converted into a blocklist.
    /// </summary>
    public List<string>? BrowserProcessNames { get; set; }

    /// <summary>Valid Pavlok stimulus types; anything else is rejected during validation.</summary>
    private static readonly HashSet<string> ValidStimulusTypes =
        new(StringComparer.OrdinalIgnoreCase) { "vibe", "beep", "zap" };

    /// <summary>
    /// Loads config, never throwing. A malformed appsettings.json used to take down OnStartup
    /// with no message at all; now it falls back to defaults and reports why.
    /// </summary>
    public static AppConfig Load(string relativePath, out string? loadError)
    {
        loadError = null;
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);

        if (!File.Exists(path))
        {
            Log.Info($"No config at {path}; using defaults.");
            return new AppConfig().Validated();
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) ?? new AppConfig();

            Log.Info($"Loaded config from {path}.");
            return config.Validated();
        }
        catch (Exception ex)
        {
            loadError = ex.Message;
            Log.Error($"Failed to read {path}; falling back to defaults.", ex);
            return new AppConfig().Validated();
        }
    }

    /// <summary>
    /// Writes the current values back to appsettings.json. Serializes to a temp file and replaces
    /// the original, so a crash or a full disk mid-write cannot leave a truncated config that the
    /// app then refuses to start from.
    /// </summary>
    public bool TrySave(string relativePath, out string? error)
    {
        error = null;
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
        var temp = path + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temp, json);

            if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
            else File.Move(temp, path);

            Log.Info($"Settings saved to {path}.");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Error($"Failed to save settings to {path}.", ex);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort */ }
            return false;
        }
    }

    /// <summary>
    /// Copies every setting from <paramref name="other"/> onto this instance.
    ///
    /// The scanner, escalation manager and overlay all hold a reference to one shared AppConfig,
    /// so mutating it in place is what makes a settings change take effect immediately instead of
    /// at the next restart. Replacing the object would leave them pointed at the old one.
    /// </summary>
    public void CopyFrom(AppConfig other)
    {
        PollIntervalSeconds = other.PollIntervalSeconds;
        ModelPath = other.ModelPath;
        InputSize = other.InputSize;
        NsfwLabelIndex = other.NsfwLabelIndex;
        DetectionThreshold = other.DetectionThreshold;
        TilingEnabled = other.TilingEnabled;
        TileDetectionThreshold = other.TileDetectionThreshold;
        TileSize = other.TileSize;
        TileOverlapFraction = other.TileOverlapFraction;
        ContentRegionWidthFraction = other.ContentRegionWidthFraction;
        MaxTilesPerPoll = other.MaxTilesPerPoll;
        ChangeDetectionEnabled = other.ChangeDetectionEnabled;
        ChangeDetectionThreshold = other.ChangeDetectionThreshold;
        CaptureAllMonitors = other.CaptureAllMonitors;
        ConsecutiveHitsToEscalate = other.ConsecutiveHitsToEscalate;
        CleanMinutesToReset = other.CleanMinutesToReset;
        PavlokEnabled = other.PavlokEnabled;
        PavlokMinSecondsBetweenStimuli = other.PavlokMinSecondsBetweenStimuli;
        PavlokLevel1Type = other.PavlokLevel1Type;
        PavlokLevel1Value = other.PavlokLevel1Value;
        PavlokLevel2Type = other.PavlokLevel2Type;
        PavlokLevel2Value = other.PavlokLevel2Value;
        PavlokLevel3Type = other.PavlokLevel3Type;
        PavlokLevel3Value = other.PavlokLevel3Value;
        SitSecondsLevel2 = other.SitSecondsLevel2;
        SitSecondsLevel3 = other.SitSecondsLevel3;
        OverlayFocusLock = other.OverlayFocusLock;
        Accelerator = other.Accelerator;
        DirectMLDeviceId = other.DirectMLDeviceId;
        ProtectedProcessNames = new List<string>(other.ProtectedProcessNames);
    }

    /// <summary>Independent copy, so a settings window can be cancelled without side effects.</summary>
    public AppConfig Clone()
    {
        var copy = new AppConfig();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>
    /// Clamps every value into a range the rest of the app can actually honour, logging anything
    /// it had to correct. Bad numbers here used to surface far away -- a zero poll interval spins
    /// the timer, a negative tile size yields no tiles, an out-of-range stimulus value 422s.
    /// </summary>
    public AppConfig Validated()
    {
        PollIntervalSeconds = Clamp(PollIntervalSeconds, 1, 3600, nameof(PollIntervalSeconds));
        InputSize = Clamp(InputSize, 32, 2048, nameof(InputSize));
        NsfwLabelIndex = Clamp(NsfwLabelIndex, 0, 1000, nameof(NsfwLabelIndex));
        DetectionThreshold = Clamp(DetectionThreshold, 0.0, 1.0, nameof(DetectionThreshold));
        TileDetectionThreshold = Clamp(TileDetectionThreshold, 0.0, 1.0, nameof(TileDetectionThreshold));
        TileSize = Clamp(TileSize, 64, 4096, nameof(TileSize));
        TileOverlapFraction = Clamp(TileOverlapFraction, 0.0, 0.9, nameof(TileOverlapFraction));
        ContentRegionWidthFraction = Clamp(ContentRegionWidthFraction, 0.05, 1.0, nameof(ContentRegionWidthFraction));
        MaxTilesPerPoll = Clamp(MaxTilesPerPoll, 1, 512, nameof(MaxTilesPerPoll));
        ChangeDetectionThreshold = Clamp(ChangeDetectionThreshold, 0.0, 255.0, nameof(ChangeDetectionThreshold));
        ConsecutiveHitsToEscalate = Clamp(ConsecutiveHitsToEscalate, 1, 1000, nameof(ConsecutiveHitsToEscalate));
        CleanMinutesToReset = Clamp(CleanMinutesToReset, 0.0, 1440.0, nameof(CleanMinutesToReset));
        PavlokMinSecondsBetweenStimuli = Clamp(PavlokMinSecondsBetweenStimuli, 0.5, 3600.0, nameof(PavlokMinSecondsBetweenStimuli));
        SitSecondsLevel2 = Clamp(SitSecondsLevel2, 1, 3600, nameof(SitSecondsLevel2));
        SitSecondsLevel3 = Clamp(SitSecondsLevel3, 1, 3600, nameof(SitSecondsLevel3));

        PavlokLevel1Value = Clamp(PavlokLevel1Value, 1, 100, nameof(PavlokLevel1Value));
        PavlokLevel2Value = Clamp(PavlokLevel2Value, 1, 100, nameof(PavlokLevel2Value));
        PavlokLevel3Value = Clamp(PavlokLevel3Value, 1, 100, nameof(PavlokLevel3Value));

        PavlokLevel1Type = ValidType(PavlokLevel1Type, "vibe", nameof(PavlokLevel1Type));
        PavlokLevel2Type = ValidType(PavlokLevel2Type, "zap", nameof(PavlokLevel2Type));
        PavlokLevel3Type = ValidType(PavlokLevel3Type, "zap", nameof(PavlokLevel3Type));

        if (string.IsNullOrWhiteSpace(ModelPath))
        {
            Log.Warn($"{nameof(ModelPath)} was empty; using Models/model.onnx.");
            ModelPath = "Models/model.onnx";
        }

        if (TileDetectionThreshold < DetectionThreshold)
        {
            Log.Warn($"{nameof(TileDetectionThreshold)} ({TileDetectionThreshold:0.##}) is below " +
                     $"{nameof(DetectionThreshold)} ({DetectionThreshold:0.##}); a single tile can now " +
                     "trigger more readily than the whole-frame pass. That is allowed but rarely intended.");
        }

        if (BrowserProcessNames is { Count: > 0 })
        {
            // Not migrated on purpose: the old setting listed the only apps allowed to receive
            // Ctrl+W, which is the opposite of what ProtectedProcessNames means. Carrying the
            // values across would silently block every browser instead.
            Log.Warn("BrowserProcessNames is obsolete and ignored. Ctrl+W now goes to any window " +
                     "except those in ProtectedProcessNames; edit that list instead.");
            BrowserProcessNames = null;
        }

        ProtectedProcessNames ??= new List<string>();

        if (!Enum.TryParse<AcceleratorChoice>(
                Accelerator?.Replace("CPU", "Cpu", StringComparison.OrdinalIgnoreCase) ?? "",
                ignoreCase: true, out _))
        {
            Log.Warn($"Accelerator='{Accelerator}' is not one of Auto/OpenVINO/DirectML/CPU; using Auto.");
            Accelerator = "Auto";
        }

        return this;
    }

    /// <summary>The <see cref="Accelerator"/> string as an enum, defaulting to Auto.</summary>
    public AcceleratorChoice AcceleratorChoice =>
        Enum.TryParse<AcceleratorChoice>(
            Accelerator?.Replace("CPU", "Cpu", StringComparison.OrdinalIgnoreCase) ?? "",
            ignoreCase: true, out var choice)
            ? choice
            : Services.AcceleratorChoice.Auto;

    private static int Clamp(int value, int min, int max, string name)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped != value) Log.Warn($"{name}={value} out of range [{min}, {max}]; using {clamped}.");
        return clamped;
    }

    private static double Clamp(double value, double min, double max, string name)
    {
        if (double.IsNaN(value))
        {
            Log.Warn($"{name} was NaN; using {min}.");
            return min;
        }
        var clamped = Math.Clamp(value, min, max);
        if (Math.Abs(clamped - value) > double.Epsilon)
            Log.Warn($"{name}={value} out of range [{min}, {max}]; using {clamped}.");
        return clamped;
    }

    private static string ValidType(string value, string fallback, string name)
    {
        if (!string.IsNullOrWhiteSpace(value) && ValidStimulusTypes.Contains(value.Trim()))
            return value.Trim().ToLowerInvariant();

        Log.Warn($"{name}='{value}' is not one of vibe/beep/zap; using '{fallback}'.");
        return fallback;
    }
}
