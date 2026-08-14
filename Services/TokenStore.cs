using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RewireGuard.Services;

/// <summary>
/// Persist a Pavlok token encrypted with DPAPI tied to the current Windows user.
/// File is stored in %LOCALAPPDATA%\RewireGuard\token.bin
/// </summary>
public static class TokenStore
{
    private static string TokenPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RewireGuard");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "token.bin");
    }

    public static void Save(string token)
    {
        var data = Encoding.UTF8.GetBytes(token);
        var protectedBytes = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(TokenPath(), protectedBytes);
    }

    public static string? Load()
    {
        var path = TokenPath();
        if (!File.Exists(path)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            var path = TokenPath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }
}