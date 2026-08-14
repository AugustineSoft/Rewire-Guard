using System.Drawing;
using RewireGuard;
using RewireGuard.Services;
using Xunit;

namespace RewireGuard.Tests;

public class TileGridTests
{
    private static AppConfig Config(int tileSize = 500, double overlap = 0.3, double widthFraction = 1.0) =>
        new AppConfig
        {
            TileSize = tileSize,
            TileOverlapFraction = overlap,
            ContentRegionWidthFraction = widthFraction
        }.Validated();

    [Fact]
    public void EveryTileIsSquareAndFullSize()
    {
        var tiles = TileGrid.Generate(new Rectangle(0, 0, 2560, 1440), Config()).ToList();

        Assert.NotEmpty(tiles);
        // The old generator clipped edge tiles, then stretched the partial rect to a square model
        // input -- so tiles at the right and bottom edges were distorted before classification.
        Assert.All(tiles, t => Assert.Equal(t.Width, t.Height));
        Assert.All(tiles, t => Assert.Equal(500, t.Width));
    }

    [Fact]
    public void TilesStayInsideTheRegion()
    {
        var region = new Rectangle(0, 0, 1920, 1080);
        var tiles = TileGrid.Generate(region, Config()).ToList();

        Assert.All(tiles, t => Assert.True(region.Contains(t), $"{t} escaped {region}"));
    }

    [Fact]
    public void TilesAreOffsetForANonZeroOriginRegion()
    {
        // Second monitor sitting to the right of the primary in the captured virtual desktop.
        var region = new Rectangle(1920, 0, 1920, 1080);
        var tiles = TileGrid.Generate(region, Config()).ToList();

        Assert.NotEmpty(tiles);
        Assert.All(tiles, t => Assert.True(t.X >= 1920, $"{t} was not offset onto the second monitor"));
        Assert.All(tiles, t => Assert.True(region.Contains(t)));
    }

    [Fact]
    public void CoverageReachesBothEdgesOfTheRegion()
    {
        var region = new Rectangle(0, 0, 1920, 1080);
        var tiles = TileGrid.Generate(region, Config()).ToList();

        Assert.Equal(0, tiles.Min(t => t.Left));
        Assert.Equal(0, tiles.Min(t => t.Top));
        Assert.Equal(1920, tiles.Max(t => t.Right));
        Assert.Equal(1080, tiles.Max(t => t.Bottom));
    }

    [Fact]
    public void ContentWidthFractionNarrowsTheBandAroundTheRegionCentre()
    {
        var region = new Rectangle(0, 0, 2000, 1000);
        var tiles = TileGrid.Generate(region, Config(tileSize: 400, widthFraction: 0.5)).ToList();

        // Band is the middle 1000px: x from 500 to 1500.
        Assert.All(tiles, t => Assert.True(t.Left >= 500, $"{t} started left of the band"));
        Assert.All(tiles, t => Assert.True(t.Right <= 1500, $"{t} ran past the band"));
    }

    [Fact]
    public void TileSizeIsClampedToTheRegionWhenTheRegionIsSmall()
    {
        var region = new Rectangle(0, 0, 300, 200);
        var tiles = TileGrid.Generate(region, Config(tileSize: 500)).ToList();

        Assert.NotEmpty(tiles);
        Assert.All(tiles, t => Assert.True(t.Width <= 200 && t.Height <= 200));
        Assert.All(tiles, t => Assert.True(region.Contains(t)));
    }

    [Fact]
    public void AdjacentTilesActuallyOverlap()
    {
        var region = new Rectangle(0, 0, 2000, 500);
        var tiles = TileGrid.Generate(region, Config(tileSize: 500, overlap: 0.3)).ToList();

        var xs = tiles.Select(t => t.X).Distinct().OrderBy(x => x).ToList();
        Assert.True(xs.Count > 1);

        // Overlap exists so a thumbnail can't fall entirely into a seam between two tiles.
        for (int i = 1; i < xs.Count; i++)
            Assert.True(xs[i] - xs[i - 1] < 500, $"gap between {xs[i - 1]} and {xs[i]} left a seam");
    }

    [Fact]
    public void EmptyRegionProducesNoTiles()
    {
        Assert.Empty(TileGrid.Generate(new Rectangle(0, 0, 0, 0), Config()));
        Assert.Empty(TileGrid.Generate(new Rectangle(0, 0, -10, 100), Config()));
    }

    [Fact]
    public void AxisPositionsEndFlushWithTheFarEdge()
    {
        var positions = TileGrid.AxisPositions(start: 0, extent: 1000, tileSize: 400, stride: 280).ToList();

        Assert.Equal(0, positions.First());
        Assert.Equal(600, positions.Last());          // 1000 - 400, so the last tile ends at 1000
        Assert.Equal(positions.Count, positions.Distinct().Count());
    }

    [Fact]
    public void AxisPositionsYieldsASinglePositionWhenTheTileFillsTheExtent()
    {
        Assert.Equal(new[] { 7 }, TileGrid.AxisPositions(start: 7, extent: 400, tileSize: 400, stride: 280));
        Assert.Equal(new[] { 7 }, TileGrid.AxisPositions(start: 7, extent: 300, tileSize: 400, stride: 280));
    }

    [Fact]
    public void AxisPositionsDoesNotEmitANearDuplicateFinalPosition()
    {
        // Stride divides the extent evenly, so the loop already lands on the flush position.
        var positions = TileGrid.AxisPositions(start: 0, extent: 900, tileSize: 500, stride: 200).ToList();

        Assert.Equal(positions.Count, positions.Distinct().Count());
        for (int i = 1; i < positions.Count; i++)
            Assert.True(positions[i] - positions[i - 1] >= 50, "emitted two effectively identical tiles");
    }
}
