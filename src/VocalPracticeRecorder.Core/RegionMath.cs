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

    /// <summary>
    /// Computes the transparent hole rectangle in window-local physical pixels (for <c>SetWindowRgn</c>).
    /// Keeps at least <paramref name="minBorder"/> pixels around all sides so the frame stays visible
    /// and edge resize handles remain hit-testable. Returns an empty rect if no valid hole fits.
    /// </summary>
    public static PixelRect WindowLocalHole(PixelRect windowBounds, PixelRect viewportScreen, int minBorder = 1)
    {
        if (windowBounds.IsEmpty || viewportScreen.IsEmpty) return default;
        int l = Math.Max(minBorder, viewportScreen.X - windowBounds.X);
        int t = Math.Max(minBorder, viewportScreen.Y - windowBounds.Y);
        int r = Math.Min(windowBounds.Width - minBorder, viewportScreen.Right - windowBounds.X);
        int b = Math.Min(windowBounds.Height - minBorder, viewportScreen.Bottom - windowBounds.Y);
        if (r - l < MinSize || b - t < MinSize) return default;
        return new PixelRect(l, t, r - l, b - t);
    }

    /// <summary>
    /// Ensures the window is big enough for the editor without jumping if it already fits,
    /// clamping the expanded bounds inside the monitor work area.
    /// </summary>
    public static PixelRect ExpandForEditor(PixelRect current, PixelRect workArea, int minWidth, int minHeight)
    {
        int w = Math.Min(workArea.Width, Math.Max(current.Width, minWidth));
        int h = Math.Min(workArea.Height, Math.Max(current.Height, minHeight));
        if (w == current.Width && h == current.Height) return current;
        int x = Math.Clamp(current.X - (w - current.Width) / 2, workArea.X, workArea.Right - w);
        int y = Math.Clamp(current.Y - (h - current.Height) / 2, workArea.Y, workArea.Bottom - h);
        return new PixelRect(x, y, w, h);
    }

    /// <summary>Clamps a persisted window rect into the nearest monitor's bounds so it never opens off-screen.</summary>
    public static PixelRect ClampWindowToMonitors(PixelRect saved, IReadOnlyList<PixelRect> monitors)
    {
        if (monitors.Count == 0) return saved;
        int i = PickMonitor(saved, monitors);
        var m = monitors[i >= 0 ? i : 0];
        int w = Math.Clamp(saved.Width, 400, m.Width);
        int h = Math.Clamp(saved.Height, 260, m.Height);
        int x = Math.Clamp(saved.X, m.X, m.Right - w);
        int y = Math.Clamp(saved.Y, m.Y, m.Bottom - h);
        return new PixelRect(x, y, w, h);
    }
}
