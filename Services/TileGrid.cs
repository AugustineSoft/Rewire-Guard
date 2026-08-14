using System;
using System.Collections.Generic;
using System.Drawing;

namespace RewireGuard.Services;

/// <summary>
/// Pure geometry for the scanning grid, kept separate from <see cref="HierarchicalScanner"/> so it
/// can be tested without a 344 MB model or a live desktop.
/// </summary>
public static class TileGrid
{
    /// <summary>
    /// Square tiles covering a centered content band of <paramref name="region"/>, overlapping by
    /// the configured fraction. Full region height is always covered.
    /// </summary>
    public static IEnumerable<Rectangle> Generate(Rectangle region, AppConfig config)
    {
        if (region.Width <= 0 || region.Height <= 0) yield break;

        int contentWidth = Math.Max(1, (int)(region.Width * config.ContentRegionWidthFraction));
        int contentLeft = region.X + (region.Width - contentWidth) / 2;

        int tileSize = Math.Min(config.TileSize, Math.Min(region.Height, contentWidth));
        if (tileSize <= 0) yield break;

        double overlap = Math.Clamp(config.TileOverlapFraction, 0.0, 0.9);
        int stride = Math.Max(1, (int)(tileSize * (1 - overlap)));

        // Always emit full square tiles. The previous version clipped the last row/column, then
        // stretched a partial rect to a square input -- so edge tiles reached the model distorted.
        foreach (int y in AxisPositions(region.Y, region.Height, tileSize, stride))
        foreach (int x in AxisPositions(contentLeft, contentWidth, tileSize, stride))
            yield return new Rectangle(x, y, tileSize, tileSize);
    }

    /// <summary>
    /// Tile origins along one axis, stepping by stride and shifting the final tile back so it ends
    /// flush with the region edge instead of hanging over it.
    /// </summary>
    public static IEnumerable<int> AxisPositions(int start, int extent, int tileSize, int stride)
    {
        int last = start + extent - tileSize;
        if (last <= start)
        {
            yield return start;
            yield break;
        }

        int position = start;
        int lastEmitted = start;
        for (; position < last; position += stride)
        {
            lastEmitted = position;
            yield return position;
        }

        // Close the gap at the far edge. The threshold only exists to avoid emitting a tile that
        // is a pixel or two off the previous one; it is deliberately tiny, because an uncovered
        // strip along the edge of the screen is a real blind spot while a slightly redundant tile
        // costs one inference that change detection will skip on the next poll anyway.
        const int MinimumUsefulShift = 8;
        if (last - lastEmitted >= MinimumUsefulShift)
            yield return last;
    }
}
