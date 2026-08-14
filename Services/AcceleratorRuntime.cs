using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace RewireGuard.Services;

/// <summary>Which inference runtime the app should load.</summary>
public enum AcceleratorChoice
{
    /// <summary>Pick based on the hardware present. Prefers the NPU.</summary>
    Auto,
    /// <summary>Intel NPU or Intel GPU.</summary>
    OpenVINO,
    /// <summary>Any DirectX 12 GPU, including NVIDIA and AMD.</summary>
    DirectML,
    /// <summary>No accelerator.</summary>
    Cpu
}

/// <summary>
/// Swaps the ONNX Runtime native libraries before inference starts.
///
/// Only one runtime can be present at a time: OpenVINO and DirectML each ship their own
/// onnxruntime.dll, built with different execution providers compiled in, so they cannot coexist
/// in one folder. Rather than shipping separate downloads per accelerator, the installer carries
/// both sets under Accelerators\ and this copies the selected one next to the executable.
///
/// The copy has to happen before anything touches ONNX Runtime, because once the native library
/// is loaded it cannot be replaced while the process is alive. Everything here therefore runs
/// early in startup, and changing the setting takes effect on the next launch.
///
/// Only the native libraries are swapped. RewireGuard.deps.json describes whichever runtime the
/// build referenced and is deliberately left alone; the managed wrapper resolves "onnxruntime"
/// through the normal loader search, which finds whatever sits beside the executable. Verified by
/// running a DirectML native set against an OpenVINO build's deps.json and confirming the
/// DirectML provider loads and performs identically to a native DirectML build.
/// </summary>
public static class AcceleratorRuntime
{
    private const string AcceleratorsFolder = "Accelerators";
    private const string ActiveMarkerFile = ".active";

    /// <summary>Device setup class for NPUs; Intel's NPU registers here as "Intel(R) AI Boost".</summary>
    private const string ComputeAcceleratorClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}";

    private static string BaseDirectory => AppContext.BaseDirectory;
    private static string AcceleratorsRoot => Path.Combine(BaseDirectory, AcceleratorsFolder);
    private static string MarkerPath => Path.Combine(AcceleratorsRoot, ActiveMarkerFile);

    /// <summary>True when the build ships swappable runtimes rather than a single baked-in one.</summary>
    public static bool IsSupported => Directory.Exists(AcceleratorsRoot);

    /// <summary>Which runtime set is currently copied into place, or null if unknown.</summary>
    public static string? Active =>
        File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null;

    /// <summary>
    /// True when an NPU is present. Read from the device class registry rather than WMI so this
    /// costs nothing at startup and needs no extra dependency.
    /// </summary>
    public static bool HasNpu()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ComputeAcceleratorClassKey);
            if (key == null) return false;

            foreach (var name in key.GetSubKeyNames())
            {
                using var device = key.OpenSubKey(name);
                var description = device?.GetValue("DriverDesc") as string;
                if (!string.IsNullOrEmpty(description))
                {
                    Log.Info($"Compute accelerator present: {description}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check for an NPU: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Resolves Auto to a concrete runtime.
    ///
    /// Prefers the NPU whenever one exists, even though a discrete GPU is measurably faster. This
    /// app polls continuously in the background, and while a game is running every tile changes
    /// every frame, so change detection saves nothing and the full inference budget runs on every
    /// poll. On the GPU that is sustained load competing with whatever is on screen; the NPU is
    /// otherwise idle silicon and costs the GPU nothing.
    /// </summary>
    public static AcceleratorChoice Resolve(AcceleratorChoice requested)
    {
        if (requested != AcceleratorChoice.Auto) return requested;
        return HasNpu() ? AcceleratorChoice.OpenVINO : AcceleratorChoice.DirectML;
    }

    /// <summary>
    /// Copies the chosen runtime's native libraries next to the executable, if they are not
    /// already the active set. Returns the runtime actually in place.
    ///
    /// Must be called before any ONNX Runtime type is touched.
    /// </summary>
    public static AcceleratorChoice Apply(AcceleratorChoice requested)
    {
        var resolved = Resolve(requested);

        if (!IsSupported)
        {
            // Single-runtime build (a plain dotnet build, or the portable single-file exe).
            // Whatever was compiled in is what runs.
            Log.Info("No swappable accelerator runtimes present; using the built-in runtime.");
            return resolved;
        }

        var folder = Path.Combine(AcceleratorsRoot, FolderNameFor(resolved));
        if (!Directory.Exists(folder))
        {
            Log.Warn($"Accelerator '{resolved}' is not installed; leaving the current runtime in place.");
            return FromFolderName(Active) ?? resolved;
        }

        // The packaged build deliberately ships no native runtime beside the executable -- it
        // would mean shipping the default set twice. So the marker matching is not enough; the
        // files themselves have to be there. This also self-heals if antivirus quarantines one.
        bool runtimePresent = File.Exists(Path.Combine(BaseDirectory, "onnxruntime.dll"));

        if (runtimePresent &&
            string.Equals(Active, FolderNameFor(resolved), StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"Accelerator runtime already set to {resolved}.");
            return resolved;
        }

        try
        {
            int copied = 0;
            foreach (var source in Directory.GetFiles(folder))
            {
                var destination = Path.Combine(BaseDirectory, Path.GetFileName(source));
                File.Copy(source, destination, overwrite: true);
                copied++;
            }

            File.WriteAllText(MarkerPath, FolderNameFor(resolved));
            Log.Info($"Applied {resolved} accelerator runtime ({copied} files).");
            return resolved;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not apply the {resolved} runtime.", ex);

            if (!File.Exists(Path.Combine(BaseDirectory, "onnxruntime.dll")))
            {
                // Nothing usable is present, so inference will fail with a DllNotFoundException
                // far from here. Say so plainly now, while the cause is still visible.
                Log.Error($"No inference runtime is installed next to {BaseDirectory}. " +
                          $"Copy the contents of Accelerators\\{FolderNameFor(resolved)} there manually, " +
                          "or reinstall to a location this account can write to.");
            }

            return FromFolderName(Active) ?? resolved;
        }
    }

    /// <summary>Runtimes actually present in this install.</summary>
    public static string[] Installed() =>
        IsSupported
            ? Directory.GetDirectories(AcceleratorsRoot).Select(Path.GetFileName).OfType<string>().ToArray()
            : Array.Empty<string>();

    private static string FolderNameFor(AcceleratorChoice choice) => choice switch
    {
        AcceleratorChoice.DirectML => "DirectML",
        AcceleratorChoice.Cpu => "CPU",
        _ => "OpenVINO"
    };

    private static AcceleratorChoice? FromFolderName(string? name) => name?.ToLowerInvariant() switch
    {
        "directml" => AcceleratorChoice.DirectML,
        "cpu" => AcceleratorChoice.Cpu,
        "openvino" => AcceleratorChoice.OpenVINO,
        _ => null
    };
}
