namespace VocalPracticeRecorder.Core;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;
    public override string ToString() => $"{Width}×{Height} @ ({X},{Y})";
}

public static class RegionMath
{
    public const int MinSize = 64;

    /// <summary>Normalises a drag (any direction) into a rectangle.</summary>
    public static PixelRect FromDrag(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>
    /// Clamps a capture region into a monitor and forces even dimensions (required by yuv420p).
    /// Returns an empty rect when the result would be smaller than <see cref="MinSize"/>.
    /// </summary>
    public static PixelRect ClampToMonitor(PixelRect r, PixelRect monitor)
    {
        int x = Math.Max(r.X, monitor.X);
        int y = Math.Max(r.Y, monitor.Y);
        int right = Math.Min(r.Right, monitor.Right);
        int bottom = Math.Min(r.Bottom, monitor.Bottom);
        int w = (right - x) & ~1;
        int h = (bottom - y) & ~1;
        if (w < MinSize || h < MinSize) return default;
        return new PixelRect(x, y, w, h);
    }

    /// <summary>Picks the monitor containing the region centre (falls back to largest overlap).</summary>
    public static int PickMonitor(PixelRect r, IReadOnlyList<PixelRect> monitors)
    {
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        for (int i = 0; i < monitors.Count; i++)
            if (monitors[i].Contains(cx, cy)) return i;
        int best = -1;
        long bestArea = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            long w = Math.Min(r.Right, m.Right) - Math.Max(r.X, m.X);
            long h = Math.Min(r.Bottom, m.Bottom) - Math.Max(r.Y, m.Y);
            if (w > 0 && h > 0 && w * h > bestArea) { bestArea = w * h; best = i; }
        }
        return best;
    }

    /// <summary>Encoded output size: keep aspect, cap height, even dimensions.</summary>
    public static (int Width, int Height) OutputSize(int srcW, int srcH, int maxHeight)
    {
        if (srcH <= maxHeight) return (srcW & ~1, srcH & ~1);
        int h = maxHeight & ~1;
        int w = (int)Math.Round(srcW * (double)h / srcH / 2.0) * 2;
        return (Math.Max(2, w), h);
    }
}
