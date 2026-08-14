using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace RewireGuard.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Redirect logging before any test touches AppConfig. Validation logs a warning per clamped
    /// value, and without this the test suite writes those into the real app's log directory,
    /// where they look like production warnings from a run that never happened.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RewireGuard.Tests", "logs");
        Environment.SetEnvironmentVariable("REWIREGUARD_LOG_DIR", dir);
    }
}
