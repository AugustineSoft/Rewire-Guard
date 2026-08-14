using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace RewireGuard.Services;

/// <summary>Result of scanning one captured frame, coarse pass plus any tiles.</summary>
public readonly record struct ScanResult(
    double MaxProbability,
    int HotTileCount,
    int TilesScanned,
    int TilesReused,
    int TilesDeferred);

/// <summary>
/// Runs a cheap whole-frame classification first (catches large/full-bleed content), then --
/// if tiling is enabled -- classifies an overlapping grid of square crops over a horizontal
/// content band on each monitor, since squashing an entire desktop down to the model's 384x384
/// input washes out small feed thumbnails. Tiles are restricted to a center width band rather
/// than the full screen to avoid wasting inferences (and false-positive surface area) on browser
/// chrome, taskbar, sidebars, etc.
///
/// Two things keep the cost survivable. Tiles whose pixels have not moved since the previous poll
/// reuse their previous probability instead of paying for another ViT pass, so an idle desktop is
/// nearly free. And each poll spends at most MaxTilesPerPoll inferences, with a rotating cursor so
/// the tiles that get deferred are the ones scanned most recently -- coverage stays complete over
/// a few polls instead of every poll blowing through its interval.
///
/// Not thread-safe (it owns the classifier's reused buffers); the app scans one frame at a time.
/// </summary>
public class HierarchicalScanner
{
    private readonly NsfwClassifier _classifier;
    private readonly AppConfig _config;

    private const int SignatureGrid = 8;                                // 8x8 luma samples per tile
    private const int SignatureLength = SignatureGrid * SignatureGrid;

    private Rectangle[] _tiles = Array.Empty<Rectangle>();
    private byte[][] _signatures = Array.Empty<byte[]>();
    private double[] _probabilities = Array.Empty<double>();
    private bool[] _everScanned = Array.Empty<bool>();
    private byte[] _coarseSignature = Array.Empty<byte>();
    private double _coarseProbability;
    private bool _coarseScanned;

    private Size _lastFrameSize;
    private int _lastRegionHash;
    private int _cursor;

    public HierarchicalScanner(NsfwClassifier classifier, AppConfig config)
    {
        _classifier = classifier;
        _config = config;
    }

    /// <summary>Number of tiles in the current grid; useful for logging and diagnostics.</summary>
    public int TileCount => _tiles.Length;

    public ScanResult Scan(Bitmap fullFrame, IReadOnlyList<Rectangle>? monitorRegions = null)
    {
        var regions = (monitorRegions is { Count: > 0 })
            ? monitorRegions
            : new[] { new Rectangle(0, 0, fullFrame.Width, fullFrame.Height) };

        EnsureGrid(fullFrame, regions);

        // One LockBits for the whole frame covers every signature; classification needs the frame
        // unlocked (GDI+ DrawImage on a locked bitmap fails), so all sampling happens up front.
        var coarseSignature = new byte[SignatureLength];
        var tileSignatures = new byte[_tiles.Length][];
        SampleSignatures(fullFrame, coarseSignature, tileSignatures);

        int scanned = 0, reused = 0;

        // Coarse whole-frame pass. Always runs when the frame changed at all; it is one inference
        // and it is the only thing that catches full-bleed content larger than a tile.
        bool coarseChanged = !_coarseScanned || HasChanged(_coarseSignature, coarseSignature);
        if (coarseChanged)
        {
            _coarseProbability = _classifier.ClassifyNsfwProbability(fullFrame);
            _coarseSignature = coarseSignature;
            _coarseScanned = true;
            scanned++;
        }
        else
        {
            reused++;
        }

        double maxProb = _coarseProbability;
        int hotTiles = _coarseProbability >= _config.TileDetectionThreshold ? 1 : 0;

        if (!_config.TilingEnabled || _tiles.Length == 0)
            return new ScanResult(maxProb, hotTiles, scanned, reused, 0);

        // Changed tiles first, then never-scanned ones, then the rest in rotation. Anything past
        // the budget keeps its previous probability and gets priority next poll.
        var order = BuildScanOrder(tileSignatures);
        int budget = _config.MaxTilesPerPoll;
        int deferred = 0;

        foreach (int i in order)
        {
            bool changed = !_everScanned[i] || HasChanged(_signatures[i], tileSignatures[i]);

            if (changed && budget > 0)
            {
                _probabilities[i] = _classifier.ClassifyNsfwProbability(fullFrame, _tiles[i]);
                _signatures[i] = tileSignatures[i];
                _everScanned[i] = true;
                budget--;
                scanned++;
            }
            else if (changed)
            {
                deferred++;
            }
            else
            {
                reused++;
            }

            if (_everScanned[i])
            {
                if (_probabilities[i] > maxProb) maxProb = _probabilities[i];
                if (_probabilities[i] >= _config.TileDetectionThreshold) hotTiles++;
            }
        }

        _cursor = _tiles.Length == 0 ? 0 : (_cursor + Math.Max(1, _config.MaxTilesPerPoll)) % _tiles.Length;

        return new ScanResult(maxProb, hotTiles, scanned, reused, deferred);
    }

    /// <summary>Forget all cached probabilities and signatures, e.g. after resuming from pause.</summary>
    public void Reset()
    {
        Array.Fill(_everScanned, false);
        Array.Fill(_probabilities, 0.0);
        _coarseScanned = false;
        _coarseProbability = 0.0;
        _coarseSignature = Array.Empty<byte>();
        _cursor = 0;
    }

    private IEnumerable<int> BuildScanOrder(byte[][] tileSignatures)
    {
        var changed = new List<int>();
        var unchanged = new List<int>();

        for (int n = 0; n < _tiles.Length; n++)
        {
            int i = (_cursor + n) % _tiles.Length;
            if (!_everScanned[i] || HasChanged(_signatures[i], tileSignatures[i]))
                changed.Add(i);
            else
                unchanged.Add(i);
        }

        return changed.Concat(unchanged);
    }

    private bool HasChanged(byte[] previous, byte[] current)
    {
        if (!_config.ChangeDetectionEnabled) return true;
        if (previous.Length != current.Length) return true;

        int total = 0;
        for (int i = 0; i < current.Length; i++)
            total += Math.Abs(current[i] - previous[i]);

        return (double)total / current.Length > _config.ChangeDetectionThreshold;
    }

    /// <summary>
    /// Reads an 8x8 grid of luma samples for the whole frame and for each tile in a single pass
    /// over the locked bitmap. 64 samples per tile is enough to notice a scroll or a new image and
    /// cheap enough to be free next to inference.
    /// </summary>
    private void SampleSignatures(Bitmap frame, byte[] coarseSignature, byte[][] tileSignatures)
    {
        var rect = new Rectangle(0, 0, frame.Width, frame.Height);
        BitmapData? data = null;
        try
        {
            data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                int stride = data.Stride;

                Sample(basePtr, stride, rect, coarseSignature);
                for (int i = 0; i < _tiles.Length; i++)
                {
                    tileSignatures[i] = new byte[SignatureLength];
                    Sample(basePtr, stride, _tiles[i], tileSignatures[i]);
                }
            }
        }
        catch (Exception ex)
        {
            // Fall back to "everything changed" rather than skipping the scan entirely.
            Log.Warn($"Signature sampling failed, treating frame as fully changed: {ex.Message}");
            for (int i = 0; i < tileSignatures.Length; i++) tileSignatures[i] ??= new byte[SignatureLength];
        }
        finally
        {
            if (data != null) frame.UnlockBits(data);
        }
    }

    private static unsafe void Sample(byte* basePtr, int stride, Rectangle area, byte[] destination)
    {
        for (int gy = 0; gy < SignatureGrid; gy++)
        {
            // Sample cell centres so a 1px border never dominates the signature.
            int y = area.Y + (int)((gy + 0.5) * area.Height / SignatureGrid);
            byte* row = basePtr + y * stride;

            for (int gx = 0; gx < SignatureGrid; gx++)
            {
                int x = area.X + (int)((gx + 0.5) * area.Width / SignatureGrid);
                byte* px = row + x * 3;

                // Integer luma (BT.601) -- exact value does not matter, only that it is stable.
                destination[gy * SignatureGrid + gx] = (byte)((px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8);
            }
        }
    }

    private void EnsureGrid(Bitmap frame, IReadOnlyList<Rectangle> regions)
    {
        int regionHash = 17;
        foreach (var r in regions) regionHash = regionHash * 31 + r.GetHashCode();

        var size = new Size(frame.Width, frame.Height);
        if (size == _lastFrameSize && regionHash == _lastRegionHash && _tiles.Length > 0) return;

        _lastFrameSize = size;
        _lastRegionHash = regionHash;

        _tiles = regions.SelectMany(r => TileGrid.Generate(r, _config)).ToArray();
        _signatures = new byte[_tiles.Length][];
        _probabilities = new double[_tiles.Length];
        _everScanned = new bool[_tiles.Length];
        for (int i = 0; i < _tiles.Length; i++) _signatures[i] = Array.Empty<byte>();

        _coarseScanned = false;
        _cursor = 0;

        Log.Info($"Tile grid rebuilt: {_tiles.Length} tiles over {regions.Count} monitor(s) " +
                 $"for a {size.Width}x{size.Height} frame (budget {_config.MaxTilesPerPoll}/poll).");
    }

}
