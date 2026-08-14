using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RewireGuard.Services;

/// <summary>
/// Grabs a bitmap of the desktop using GDI BitBlt (System.Drawing).
/// Simple and reliable for periodic polling; not suitable for high frame-rate capture.
///
/// Deliberately avoids System.Windows.Forms.Screen (and therefore UseWindowsForms in the
/// csproj) because enabling WinForms alongside WPF makes the SDK add a global "using
/// System.Windows.Forms" that collides with WPF's "System.Windows" -- Application,
/// ContextMenu, and MenuItem all exist in both, which breaks the build with CS0104
/// ambiguous-reference errors. A couple of GetSystemMetrics calls avoid that whole class
/// of problem for the one thing we actually needed WinForms for.
///
/// The returned bitmap is owned by this service and reused across calls: a 2560x1440 frame is
/// ~11 MB, and allocating one per poll churned the large object heap for no reason. Callers must
/// not dispose it and must finish with it before the next Capture().
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private readonly bool _allMonitors;
    private Bitmap? _buffer;
    private Graphics? _graphics;
    private Rectangle _bufferBounds;
    private Rectangle[] _monitorRegions = Array.Empty<Rectangle>();
    private bool _disposed;

    /// <summary>Virtual-desktop bounds of the most recent capture, in physical pixels.</summary>
    public Rectangle LastBounds => _bufferBounds;

    /// <summary>
    /// Each monitor's rectangle in captured-frame coordinates (origin at the top-left of the
    /// buffer, not of the virtual desktop). The scanner needs these because
    /// ContentRegionWidthFraction means "the middle of a screen" -- applied to a 5120x1440
    /// dual-monitor frame it would centre the band on the seam between the two displays and
    /// scan the inner half of each, which is exactly the wrong region.
    /// </summary>
    public IReadOnlyList<Rectangle> MonitorRegions => _monitorRegions;

    public ScreenCaptureService(bool allMonitors = true)
    {
        _allMonitors = allMonitors;
    }

    /// <summary>
    /// Captures the desktop. Returns null when the screen cannot be read -- during a lock screen,
    /// UAC prompt, or session switch GDI fails, and that is expected rather than exceptional.
    /// </summary>
    public Bitmap? Capture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bounds = GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        // Resolution or monitor-arrangement change: rebuild the buffer.
        if (_buffer == null || _bufferBounds.Size != bounds.Size)
        {
            _graphics?.Dispose();
            _buffer?.Dispose();
            _buffer = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            _graphics = Graphics.FromImage(_buffer);
            Log.Info($"Capture buffer sized to {bounds.Width}x{bounds.Height} at ({bounds.X},{bounds.Y}).");
        }
        _bufferBounds = bounds;
        _monitorRegions = GetMonitorRegions(bounds);

        try
        {
            _graphics!.CopyFromScreen(
                bounds.X, bounds.Y, 0, 0,
                new Size(bounds.Width, bounds.Height),
                CopyPixelOperation.SourceCopy);
            return _buffer;
        }
        catch (Win32Exception ex)
        {
            // Secure desktop (lock screen / UAC) -- nothing to classify, try again next poll.
            Log.Info($"Screen capture unavailable this poll: {ex.Message}");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"Screen capture failed: {ex.Message}");
            return null;
        }
    }

    private Rectangle GetBounds()
    {
        if (_allMonitors)
        {
            var bounds = new Rectangle(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                GetSystemMetrics(SM_CXVIRTUALSCREEN),
                GetSystemMetrics(SM_CYVIRTUALSCREEN));

            if (bounds.Width > 0 && bounds.Height > 0) return bounds;
            Log.Warn("Virtual screen metrics were empty; falling back to the primary display.");
        }

        return new Rectangle(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }

    private Rectangle[] GetMonitorRegions(Rectangle captureBounds)
    {
        if (!_allMonitors)
            return new[] { new Rectangle(0, 0, captureBounds.Width, captureBounds.Height) };

        var found = new List<Rectangle>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref Rect rect, IntPtr _) =>
            {
                // Translate from virtual-desktop coordinates into buffer coordinates, and clip:
                // a monitor can extend past the buffer if the arrangement changed mid-enumeration.
                var monitor = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                monitor.Offset(-captureBounds.X, -captureBounds.Y);
                monitor = Rectangle.Intersect(monitor, new Rectangle(0, 0, captureBounds.Width, captureBounds.Height));

                if (monitor.Width > 0 && monitor.Height > 0) found.Add(monitor);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"Monitor enumeration failed: {ex.Message}");
        }

        return found.Count > 0
            ? found.ToArray()
            : new[] { new Rectangle(0, 0, captureBounds.Width, captureBounds.Height) };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _graphics?.Dispose();
        _buffer?.Dispose();
        _graphics = null;
        _buffer = null;
    }
}
