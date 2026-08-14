using RewireGuard;
using RewireGuard.Services;
using Xunit;

namespace RewireGuard.Tests;

public class EscalationManagerTests
{
    private static AppConfig Config(int hitsToEscalate = 2, double cleanMinutes = 2) => new AppConfig
    {
        ConsecutiveHitsToEscalate = hitsToEscalate,
        CleanMinutesToReset = cleanMinutes
    }.Validated();

    [Fact]
    public void StartsAtLevelZero()
    {
        var manager = new EscalationManager(Config());
        Assert.Equal(0, manager.Level);
    }

    [Fact]
    public void FirstDetectionRaisesLevelOne()
    {
        var manager = new EscalationManager(Config());
        manager.ReportDetection(true);
        Assert.Equal(1, manager.Level);
    }

    [Fact]
    public void EscalatesOneStepPerConfiguredRunOfHits()
    {
        var manager = new EscalationManager(Config(hitsToEscalate: 2));

        manager.ReportDetection(true);   // hit 1 -> level 1
        manager.ReportDetection(true);   // hit 2 -> level 1
        Assert.Equal(1, manager.Level);

        manager.ReportDetection(true);   // hit 3 -> level 2
        Assert.Equal(2, manager.Level);

        manager.ReportDetection(true);   // hit 4 -> level 2
        manager.ReportDetection(true);   // hit 5 -> level 3
        Assert.Equal(3, manager.Level);
    }

    [Fact]
    public void LevelIsCappedAtThree()
    {
        var manager = new EscalationManager(Config(hitsToEscalate: 1));
        for (int i = 0; i < 50; i++) manager.ReportDetection(true, hotTileCount: 10);
        Assert.Equal(3, manager.Level);
    }

    [Fact]
    public void CleanFrameResetsTheStreakButNotTheLevelImmediately()
    {
        var manager = new EscalationManager(Config(hitsToEscalate: 2, cleanMinutes: 60));

        manager.ReportDetection(true);
        manager.ReportDetection(true);
        manager.ReportDetection(true);
        Assert.Equal(2, manager.Level);

        // Going clean must not drop the overlay instantly; that is what CleanMinutesToReset is for.
        manager.ReportDetection(false);
        Assert.Equal(2, manager.Level);

        // But the streak is gone, so the ramp restarts rather than continuing from where it was.
        manager.ReportDetection(true);
        Assert.Equal(2, manager.Level);
        manager.ReportDetection(true);
        Assert.Equal(2, manager.Level);
    }

    [Fact]
    public void LevelNeverStepsDownMidStreak()
    {
        var manager = new EscalationManager(Config(hitsToEscalate: 1));

        manager.ReportDetection(true, hotTileCount: 8);
        manager.ReportDetection(true, hotTileCount: 8);
        var peak = manager.Level;

        // A tick with fewer hot tiles must not bounce the level back down and re-fire on the way up.
        manager.ReportDetection(true, hotTileCount: 0);
        Assert.True(manager.Level >= peak);
    }

    [Fact]
    public void ZeroCleanMinutesResetsOnTheFirstCleanFrame()
    {
        var manager = new EscalationManager(Config(hitsToEscalate: 1, cleanMinutes: 0));

        manager.ReportDetection(true);
        Assert.Equal(1, manager.Level);

        manager.ReportDetection(false);
        Assert.Equal(0, manager.Level);
    }

    [Fact]
    public void ResetClearsLevelAndStreak()
    {
        var manager = new EscalationManager(Config(hitsToEscalate: 2));

        manager.ReportDetection(true);
        manager.ReportDetection(true);
        manager.ReportDetection(true);
        Assert.Equal(2, manager.Level);

        manager.Reset();
        Assert.Equal(0, manager.Level);
        Assert.Equal(0, manager.ConsecutiveHits);

        // This is the pause/resume case: the next detection must start the ramp over at level 1,
        // not resume at the level the user was on when they paused.
        manager.ReportDetection(true);
        Assert.Equal(1, manager.Level);
    }

    [Fact]
    public void ResetRaisesLevelChangedSoTheOverlayCanBeDismissed()
    {
        var manager = new EscalationManager(Config(hitsToEscalate: 1));
        var seen = new List<int>();
        manager.LevelChanged += seen.Add;

        manager.ReportDetection(true);
        manager.Reset();

        Assert.Equal(new[] { 1, 0 }, seen);
    }

    [Fact]
    public void ResetOnAlreadyZeroLevelRaisesNoEvent()
    {
        var manager = new EscalationManager(Config());
        var seen = new List<int>();
        manager.LevelChanged += seen.Add;

        manager.Reset();

        Assert.Empty(seen);
    }

    [Fact]
    public void TileBonusNeverJumpsStraightToHighLevelFromOneFrame()
    {
        var manager = new EscalationManager(Config(hitsToEscalate: 2));

        // A single frame lighting up many overlapping tiles is correlated evidence, not
        // independent confirmation, so it must not skip the ramp.
        manager.ReportDetection(true, hotTileCount: 25);
        Assert.Equal(1, manager.Level);
    }
}
