namespace VocalPracticeRecorder.Core;

/// <summary>A span on the timeline, in seconds.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Length => End - Start;
    public bool Contains(double t) => t >= Start && t < End;
}

/// <summary>
/// Pure timeline math for trims and deleted ranges ("cuts"). Every boundary is snapped to the video
/// frame grid so that video (frame-granular) and audio (sample-granular) are cut at exactly the same instant.
/// </summary>
public static class EditTimeline
{
    public static double Snap(double t, int fps) => fps > 0 ? Math.Round(t * fps, MidpointRounding.AwayFromZero) / fps : t;

    /// <summary>Adds a cut, merging overlapping/adjacent cuts. Returns a new sorted list.</summary>
    public static List<TimeRange> AddCut(IEnumerable<TimeRange> cuts, TimeRange cut, int fps)
    {
        var r = new TimeRange(Snap(Math.Min(cut.Start, cut.End), fps), Snap(Math.Max(cut.Start, cut.End), fps));
        var all = cuts.Append(r).Where(c => c.Length > 1e-9).OrderBy(c => c.Start).ToList();
        var merged = new List<TimeRange>();
        foreach (var c in all)
        {
            if (merged.Count > 0 && c.Start <= merged[^1].End + 1e-9)
                merged[^1] = new TimeRange(merged[^1].Start, Math.Max(merged[^1].End, c.End));
            else merged.Add(c);
        }
        return merged;
    }

    /// <summary>Removes the cut containing <paramref name="t"/> (restores that section).</summary>
    public static List<TimeRange> RemoveCutAt(IEnumerable<TimeRange> cuts, double t) =>
        cuts.Where(c => !(t >= c.Start - 1e-9 && t <= c.End + 1e-9)).ToList();

    public static TimeRange? CutAt(IEnumerable<TimeRange> cuts, double t)
    {
        foreach (var c in cuts) if (t >= c.Start && t < c.End) return c;
        return null;
    }

    /// <summary>Kept pieces of the timeline: [trimStart, trimEnd] minus cuts, frame-snapped, in order.</summary>
    public static List<TimeRange> KeptSegments(SessionInfo s)
    {
        int fps = s.Video.Fps;
        double a = Snap(Math.Max(0, s.Edit.TrimStart), fps);
        double b = Math.Min(Snap(s.EffectiveTrimEnd, fps), s.Duration);
        var result = new List<TimeRange>();
        double cur = a;
        foreach (var c in s.Edit.Cuts.OrderBy(c => c.Start))
        {
            double cs = Snap(c.Start, fps), ce = Snap(c.End, fps);
            if (ce <= cur || cs >= b) continue;
            if (cs > cur) result.Add(new TimeRange(cur, Math.Min(cs, b)));
            cur = Math.Max(cur, ce);
            if (cur >= b) break;
        }
        if (cur < b) result.Add(new TimeRange(cur, b));
        double minLen = fps > 0 ? 1.0 / fps - 1e-9 : 0.01;
        return result.Where(r => r.Length >= minLen).ToList();
    }

    public static double OutputDuration(SessionInfo s) => KeptSegments(s).Sum(r => r.Length);

    /// <summary>Where timeline time <paramref name="t"/> ends up in the exported file, or null if it is not kept.</summary>
    public static double? TimelineToOutput(SessionInfo s, double t)
    {
        double acc = 0;
        foreach (var seg in KeptSegments(s))
        {
            if (t >= seg.Start && t < seg.End) return acc + (t - seg.Start);
            acc += seg.Length;
        }
        return null;
    }
}
