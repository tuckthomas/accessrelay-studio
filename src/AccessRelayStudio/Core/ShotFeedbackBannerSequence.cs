namespace EliteScriptStudio.Core;

// Follows the HUD even between presses. A still-visible banner can be claimed only once.
internal sealed class ShotFeedbackBannerSequence
{
    internal sealed record Appearance(long StartedAt, ShotFeedbackAnalysis.ParsedBanner Banner);
    private long _lastFrame, _candidateAt;
    private int _agreements, _emptyFrames;
    private bool _baselineKnown;
    private string _candidate = "";
    private ShotFeedbackAnalysis.ParsedBanner? _visible;
    internal void ConfirmClear(long ticks)
    {
        if (ticks <= _lastFrame) return;
        _lastFrame = ticks; _agreements = _emptyFrames = 0; _baselineKnown = true; _visible = null;
    }

    internal Appearance? Observe(long ticks, ShotFeedbackAnalysis.ParsedBanner? banner, bool heading, bool primary)
    {
        if (ticks <= _lastFrame) return null;
        _lastFrame = ticks;
        if (banner is null)
        {
            if (!primary) return null;
            _agreements = 0;
            _emptyFrames = heading ? 0 : _emptyFrames + 1;
            if (_emptyFrames >= 2) { _baselineKnown = true; _visible = null; }
            return null;
        }
        _emptyFrames = 0;
        string identity = ShotFeedbackAnalysis.Identity(banner);
        if (_agreements == 0 || identity != _candidate)
        { _agreements = 1; _candidate = identity; _candidateAt = ticks; return null; }
        if (++_agreements != 2) return null;
        // A transient missing distance or tempo line is common during the banner animation.
        // Only two complete, contradictory readings or an observed wipe establish a new result.
        bool changed = _visible is null || _visible.ShotType != banner.ShotType || _visible.Timing != banner.Timing
            || (_visible.Distance is { } oldDistance && banner.Distance is { } newDistance && oldDistance != newDistance);
        bool fresh = _baselineKnown && changed;
        if (changed || _visible?.Distance is null) _visible = banner;
        _baselineKnown = true;
        return fresh ? new(_candidateAt, banner) : null;
    }
}
