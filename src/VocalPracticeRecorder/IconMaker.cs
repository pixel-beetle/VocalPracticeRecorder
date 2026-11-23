using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VocalPracticeRecorder;

/// <summary>
/// Draws the app icon (microphone on a coral→magenta rounded square, with sound waves at larger sizes)
/// and writes a multi-resolution .ico: <c>--make-icon &lt;app.ico&gt; [previewDir]</c>.
/// Each size is rendered separately so small sizes stay crisp (thicker strokes, no fine detail).
/// </summary>
internal static class IconMaker
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static int Run(string[] args)
    {
        string icoPath = args[0];
        string? previewDir = args.Length > 1 ? args[1] : null;
        var pngs = new List<(int Size, byte[] Data)>();
        foreach (int s in Sizes)
        {
            var bmp = Render(s);
            pngs.Add((s, Encode(bmp)));
            if (previewDir != null)
            {
                Directory.CreateDirectory(previewDir);
                File.WriteAllBytes(Path.Combine(previewDir, $"icon-{s}.png"), Encode(bmp));
                // 8x nearest-neighbour blow-up of tiny sizes for pixel-level review.
                if (s <= 32) File.WriteAllBytes(Path.Combine(previewDir, $"icon-{s}-x8.png"), Encode(Upscale(bmp, 8)));
            }
        }
        WriteIco(icoPath, pngs);
        return 0;
    }

    private static RenderTargetBitmap Render(int size)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetEdgeMode(dv, EdgeMode.Unspecified);
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            Draw(dc, size);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    /// <summary>All coordinates on a 256×256 canvas.</summary>
    private static void Draw(DrawingContext dc, int size)
    {
        bool tiny = size <= 24;
        bool small = size <= 48;

        // Background: rounded square, coral → magenta. Tiny icons use the full square for maximum area.
        double inset = tiny ? 0 : 8;
        double radius = tiny ? 48 : 56;
        var bg = new LinearGradientBrush(Color.FromRgb(0xFF, 0x6B, 0x5E), Color.FromRgb(0xC2, 0x18, 0x5B), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(bg, null, new Rect(inset, inset, 256 - 2 * inset, 256 - 2 * inset), radius, radius);
        // Soft top highlight for depth (skipped where it would just blur).
        if (!small)
        {
            var hl = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0, 1));
            dc.DrawRoundedRectangle(hl, null, new Rect(inset, inset, 256 - 2 * inset, 120), radius, radius);
        }

        var white = Brushes.White;
        double stroke = size <= 16 ? 34 : tiny ? 28 : small ? 20 : 15;
        var pen = new Pen(white, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        // Microphone capsule.
        double capW = tiny ? 84 : 70, capTop = tiny ? 34 : 44, capBottom = tiny ? 146 : 146;
        double cx = 128;
        dc.DrawRoundedRectangle(white, null, new Rect(cx - capW / 2, capTop, capW, capBottom - capTop), capW / 2, capW / 2);
        if (!small)
        {
            // Grille lines.
            var grille = new Pen(new SolidColorBrush(Color.FromArgb(90, 0xC2, 0x18, 0x5B)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i < 3; i++)
            {
                double y = 78 + i * 18;
                dc.DrawLine(grille, new Point(cx - 18, y), new Point(cx + 18, y));
            }
        }

        // Cradle (U) + stem + base.
        double uHalf = tiny ? 62 : 54, uTop = tiny ? 108 : 112, uBottom = tiny ? 176 : 172;
        var cradle = new StreamGeometry();
        using (var c = cradle.Open())
        {
            c.BeginFigure(new Point(cx - uHalf, uTop), false, false);
            c.BezierTo(new Point(cx - uHalf, uBottom + 8), new Point(cx + uHalf, uBottom + 8), new Point(cx + uHalf, uTop), true, true);
        }
        cradle.Freeze();
        dc.DrawGeometry(null, pen, cradle);
        double stemTop = uBottom + 4, stemBottom = tiny ? 222 : 208;
        dc.DrawLine(pen, new Point(cx, stemTop), new Point(cx, stemBottom));
        if (!tiny) dc.DrawLine(pen, new Point(cx - 34, stemBottom), new Point(cx + 34, stemBottom));

        // Sound waves on both sides (only where there is room for them).
        if (!small)
        {
            var wave = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 11) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            foreach (int side in new[] { -1, 1 })
            {
                for (int k = 0; k < 2; k++)
                {
                    double r = 92 + k * 22, a = 0.55;
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        var center = new Point(cx, 100);
                        var p0 = new Point(center.X + side * r * Math.Cos(a), center.Y - r * Math.Sin(a));
                        var p1 = new Point(center.X + side * r * Math.Cos(a), center.Y + r * Math.Sin(a));
                        c.BeginFigure(p0, false, false);
                        c.ArcTo(p1, new Size(r, r), 0, false, side > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
                    }
                    g.Freeze();
                    if (k == 1) wave = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 11) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawGeometry(null, wave, g);
                }
            }
        }
    }

    private static BitmapSource Upscale(BitmapSource src, int factor)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new int[w * h];
        src.CopyPixels(px, w * 4, 0);
        int W = w * factor, H = h * factor;
        var dst = new int[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                dst[y * W + x] = px[(y / factor) * w + x / factor];
        return BitmapSource.Create(W, H, 96, 96, PixelFormats.Pbgra32, null, dst, W * 4);
    }

    private static byte[] Encode(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>ICO container with PNG-compressed entries (supported since Windows Vista).</summary>
    private static void WriteIco(string path, List<(int Size, byte[] Data)> images)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
    }
}
