using RewireGuard;
using Xunit;

namespace RewireGuard.Tests;

public class AppConfigTests
{
    [Fact]
    public void ClampsOutOfRangeNumbersIntoUsableRanges()
    {
        var config = new AppConfig
        {
            PollIntervalSeconds = 0,          // would spin the timer
            TileSize = -10,                   // would yield no tiles at all
            DetectionThreshold = 5.0,         // never fires
            TileOverlapFraction = 4.0,        // stride collapses to 1px -> millions of tiles
            ContentRegionWidthFraction = 0,
            ConsecutiveHitsToEscalate = 0,    // divide-by-zero guard in the escalation maths
            PavlokMinSecondsBetweenStimuli = -1
        }.Validated();

        Assert.True(config.PollIntervalSeconds >= 1);
        Assert.True(config.TileSize >= 64);
        Assert.InRange(config.DetectionThreshold, 0.0, 1.0);
        Assert.InRange(config.TileOverlapFraction, 0.0, 0.9);
        Assert.True(config.ContentRegionWidthFraction > 0);
        Assert.True(config.ConsecutiveHitsToEscalate >= 1);
        Assert.True(config.PavlokMinSecondsBetweenStimuli > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(9999)]
    public void ClampsStimulusIntensityToTheDeviceRange(int value)
    {
        var config = new AppConfig
        {
            PavlokLevel1Value = value,
            PavlokLevel2Value = value,
            PavlokLevel3Value = value
        }.Validated();

        Assert.InRange(config.PavlokLevel1Value, 1, 100);
        Assert.InRange(config.PavlokLevel2Value, 1, 100);
        Assert.InRange(config.PavlokLevel3Value, 1, 100);
    }

    [Fact]
    public void RejectsUnknownStimulusTypes()
    {
        // The API 422s on anything outside vibe/beep/zap, and the failure only showed up as a
        // logged HTTP error at the moment a stimulus was meant to fire.
        var config = new AppConfig
        {
            PavlokLevel1Type = "shock",
            PavlokLevel2Type = "",
            PavlokLevel3Type = "ZAP"
        }.Validated();

        Assert.Equal("vibe", config.PavlokLevel1Type);
        Assert.Equal("zap", config.PavlokLevel2Type);
        Assert.Equal("zap", config.PavlokLevel3Type);
    }

    [Fact]
    public void KeepsValidValuesUntouched()
    {
        var config = new AppConfig
        {
            PollIntervalSeconds = 5,
            DetectionThreshold = 0.42,
            TileSize = 384,
            PavlokLevel2Type = "beep",
            PavlokLevel2Value = 55
        }.Validated();

        Assert.Equal(5, config.PollIntervalSeconds);
        Assert.Equal(0.42, config.DetectionThreshold);
        Assert.Equal(384, config.TileSize);
        Assert.Equal("beep", config.PavlokLevel2Type);
        Assert.Equal(55, config.PavlokLevel2Value);
    }

    [Fact]
    public void EmptyModelPathFallsBackToTheDefault()
    {
        var config = new AppConfig { ModelPath = "  " }.Validated();
        Assert.Equal("Models/model.onnx", config.ModelPath);
    }

    [Fact]
    public void NaNThresholdDoesNotSurviveValidation()
    {
        var config = new AppConfig { DetectionThreshold = double.NaN }.Validated();
        Assert.False(double.IsNaN(config.DetectionThreshold));
    }

    [Fact]
    public void MissingConfigFileYieldsDefaultsWithoutError()
    {
        var config = AppConfig.Load("does-not-exist.json", out var error);

        Assert.Null(error);
        Assert.Equal("Models/model.onnx", config.ModelPath);
    }
}
