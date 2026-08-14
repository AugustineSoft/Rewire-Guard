using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace RewireGuard.Services;

/// <summary>
/// Minimal rolling file logger. Everything used to go to Debug.WriteLine, which is invisible
/// in a Release build -- so any field failure (Pavlok 401, model load error, capture fault)
/// was undiagnosable. Writes to %LOCALAPPDATA%\RewireGuard\logs\rewireguard-yyyyMMdd.log.
///
/// Writes happen on a background thread so a slow/locked disk can never stall the poll loop
/// or the UI thread.
/// </summary>
public static class Log
{
    private const long MaxBytesPerFile = 2 * 1024 * 1024;
    private const int MaxFilesToKeep = 5;

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 4096);
    private static readonly Lazy<Thread> Writer = new(StartWriter, LazyThreadSafetyMode.ExecutionAndPublication);
    private static volatile bool _shuttingDown;

    /// <summary>
    /// Log destination. REWIREGUARD_LOG_DIR overrides the default, which keeps a test run (config
    /// validation logs a warning for every clamped value) from writing into the real app's log
    /// directory, and makes a portable/USB install possible.
    /// </summary>
    public static string Directory { get; } = ResolveDirectory();

    private static string ResolveDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("REWIREGUARD_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RewireGuard",
            "logs");
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine($"[RewireGuard] {line}");

        if (_shuttingDown) return;
        try
        {
            _ = Writer.Value;           // ensure the writer thread exists
            Queue.TryAdd(line);         // drop on overflow rather than block the caller
        }
        catch (ObjectDisposedException) { /* shutting down */ }
        catch (InvalidOperationException) { /* queue completed */ }
    }

    /// <summary>Flush pending lines and stop the writer thread. Safe to call more than once.</summary>
    public static void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try
        {
            Queue.CompleteAdding();
            if (Writer.IsValueCreated) Writer.Value.Join(TimeSpan.FromSeconds(2));
        }
        catch { /* best-effort */ }
    }

    private static Thread StartWriter()
    {
        var thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "RewireGuard.Log",
            Priority = ThreadPriority.BelowNormal
        };
        thread.Start();
        return thread;
    }

    private static void WriterLoop()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, $"rewireguard-{DateTime.Now:yyyyMMdd}.log");
                Roll(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the app down. Debug.WriteLine above already fired.
            }
        }
    }

    private static void Roll(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytesPerFile) return;

        var rolled = Path.Combine(
            Directory,
            $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:HHmmss}.log");
        File.Move(path, rolled, overwrite: true);

        var files = new DirectoryInfo(Directory).GetFiles("rewireguard-*.log");
        if (files.Length <= MaxFilesToKeep) return;

        Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        for (int i = 0; i < files.Length - MaxFilesToKeep; i++)
        {
            try { files[i].Delete(); } catch { /* best-effort */ }
        }
    }
}
