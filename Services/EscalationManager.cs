namespace RewireGuard.Services;

/// <summary>
/// Tracks consecutive positive detections and escalates a response level (0-3) immediately
/// as detection continues. Compliance (closing the content) is reflected the moment a clean
/// frame comes in -- the streak resets right away, so escalation can't continue climbing off
/// stale state. Level resets to 0 for the overlay after CleanMinutesToReset of sustained clean
/// frames, but that's purely a visual-lingering setting -- it does NOT gate whether new
/// stimuli can fire (see App.xaml.cs Tick(), which gates on that separately).
/// </summary>
public class EscalationManager
{
    private readonly AppConfig _config;
    private int _consecutiveHits;
    private DateTime _lastPositiveUtc = DateTime.MinValue;

    public int Level { get; private set; }

    /// <summary>Current positive-frame streak; surfaced for the tray status readout.</summary>
    public int ConsecutiveHits => _consecutiveHits;

    public event Action<int>? LevelChanged;

    public EscalationManager(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Drop back to level 0 and clear the streak.
    ///
    /// Pausing used to close the overlay while leaving Level where it was, so the first positive
    /// poll after resuming fired a level 3 stimulus with no ramp -- and because SetLevel only
    /// raises an event on change, the overlay did not even reappear to explain it. Anything that
    /// interrupts monitoring must come back through here.
    /// </summary>
    public void Reset()
    {
        _consecutiveHits = 0;
        _lastPositiveUtc = DateTime.MinValue;
        SetLevel(0);
    }

    public void ReportDetection(bool isNsfw, int hotTileCount = 0)
    {
        var now = DateTime.UtcNow;

        if (isNsfw)
        {
            _consecutiveHits++;
            _lastPositiveUtc = now;

            int consecutiveLevel = Math.Min(3, 1 + (_consecutiveHits - 1) / Math.Max(1, _config.ConsecutiveHitsToEscalate));

            // Tiles overlap by design -- several firing together on one frame is correlated,
            // not independent confirmation. This nudges the level up by at most one over what
            // consecutive hits alone would give; it never jumps straight to a high level off
            // a single frame.
            // Make tile-based bonus rarer to favor stepwise escalation 1->2->3.
            // Require both a higher consecutive level and sustained detection plus a larger number of hot tiles.
            int tileBonus = 0;
            if (consecutiveLevel >= 2 && hotTileCount >= 4 && _consecutiveHits >= _config.ConsecutiveHitsToEscalate * 2)
            {
                tileBonus = 1;
            }

            int computedLevel = Math.Min(3, consecutiveLevel + tileBonus);

            // Level only ever climbs while actively detecting -- never steps back down
            // mid-streak just because one tick had a lower tile count than the last. This is
            // what stops the level bouncing and re-firing on ordinary detection noise.
            SetLevel(Math.Max(Level, computedLevel));
        }
        else
        {
            // Compliance: reset the streak the instant a clean frame comes in. This alone
            // can't fire a NEW stimulus (Tick() gates on isNsfw for that), and it means the
            // ramp can't continue climbing off leftover state once the content is gone.
            _consecutiveHits = 0;

            if (Level > 0)
            {
                var cleanMinutes = (now - _lastPositiveUtc).TotalMinutes;
                if (cleanMinutes >= _config.CleanMinutesToReset)
                {
                    SetLevel(0);
                }
            }
        }
    }

    private void SetLevel(int level)
    {
        if (level == Level) return;
        Level = level;
        LevelChanged?.Invoke(level);
    }
}