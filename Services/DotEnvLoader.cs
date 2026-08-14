using System;
using System.IO;

namespace RewireGuard.Services;

public static class DotEnvLoader
{
    /// <summary>
    /// Load .env from the app output folder or parent folders (dev convenience).
    /// Accepts a few common dev key variants and normalizes them to the expected
    /// runtime names (PAVLOK_API_KEY / PAVLOK_EMAIL / PAVLOK_PASSWORD).
    /// </summary>
    public static void Load(string fileName = ".env")
    {
        try
        {
            // Look in the base directory and up a few levels so a .env in repo root is found
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            string? found = null;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                var path = Path.Combine(dir, fileName);
                if (File.Exists(path))
                {
                    found = path;
                    break;
                }

                var parent = Directory.GetParent(dir);
                dir = parent?.FullName;
            }

            if (found == null) return;

            foreach (var raw in File.ReadAllLines(found))
            {
                var line = raw?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                var idx = line.IndexOf('=');
                if (idx <= 0) continue;

                var key = line.Substring(0, idx).Trim();
                var val = line.Substring(idx + 1).Trim();

                // Trim common delimiters a dev might have included accidentally
                val = val.Trim().Trim('\"', '\'', ';', ' ');
                // If someone pasted a JSON-like {"token"} or {yourtoken} -> strip braces
                if (val.StartsWith("{") && val.EndsWith("}"))
                {
                    val = val.Substring(1, val.Length - 2).Trim().Trim('\"', '\'');
                }

                // Normalize common dev names to what the app expects
                if (string.Equals(key, "API Key", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "API_KEY", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "APIKEY", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "PAVLOK_KEY", StringComparison.OrdinalIgnoreCase))
                {
                    key = "PAVLOK_API_KEY";
                }
                else if (string.Equals(key, "EMAIL", StringComparison.OrdinalIgnoreCase))
                {
                    key = "PAVLOK_EMAIL";
                }
                else if (string.Equals(key, "PASSWORD", StringComparison.OrdinalIgnoreCase))
                {
                    key = "PAVLOK_PASSWORD";
                }

                Environment.SetEnvironmentVariable(key, val, EnvironmentVariableTarget.Process);
            }
        }
        catch
        {
            // best-effort for dev convenience
        }
    }
}