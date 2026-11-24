using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VocalPracticeRecorder.Core;
using VocalPracticeRecorder.Views;

namespace VocalPracticeRecorder;

/// <summary>
/// Renders the app's UI across all parallel states to PNG files and verifies real alpha transparency,
/// backdrop-disabled, WDA_EXCLUDEFROMCAPTURE, and MediaElement invariants:
/// <c>--snapshot &lt;outDir&gt; &lt;dataRoot|demo&gt;</c>. Never modifies settings.json.
/// </summary>
internal static class Snapshot
{
    private static (byte A, byte R, byte G, byte B) SamplePixel(RenderTargetBitmap bmp, int x, int y)
    {
        x = Math.Clamp(x, 0, bmp.PixelWidth - 1);
        y = Math.Clamp(y, 0, bmp.PixelHeight - 1);
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[3], px[2], px[1], px[0]); // Pbgra32
    }

    /// <summary>Renders the true WPF window alpha buffer (transparent pixels stay alpha == 0).</summary>
    private static RenderTargetBitmap RenderTrueAlpha(FrameworkElement el, double scale = 1)
    {
        el.UpdateLayout();
        int pw = (int)Math.Ceiling(el.ActualWidth * scale), ph = (int)Math.Ceiling(el.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var vb = new VisualBrush(el)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, el.ActualWidth, el.ActualHeight),
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        rtb.Render(dv);
        return rtb;
    }

    private static void AssertCaptureTransparency(Window w, string label)
    {
        var rootEl = (FrameworkElement)w.Content;
        var vp = (FrameworkElement)w.FindName("CaptureViewport");
        var trueBmp = RenderTrueAlpha(rootEl, 1);
        var hole = vp.TransformToAncestor(rootEl).TransformBounds(new Rect(0, 0, vp.ActualWidth, vp.ActualHeight));
        var center = SamplePixel(trueBmp, (int)(hole.X + hole.Width / 2), (int)(hole.Y + hole.Height / 2));
        var corner = SamplePixel(trueBmp, (int)(hole.X + 2), (int)(hole.Y + 2));
        var topBarPx = SamplePixel(trueBmp, trueBmp.PixelWidth / 2, 20);
        var framePx = SamplePixel(trueBmp, trueBmp.PixelWidth - 3, trueBmp.PixelHeight / 2);
        int backdrop = Native.GetBackdropType(w);
        Log.Info($"snapshot {label}: holeCenterA={center.A} holeCornerA={corner.A} topBarA={topBarPx.A} frameA={framePx.A} backdrop={backdrop}");
        if (center.A != 0 || corner.A != 0)
            throw new InvalidOperationException($"{label}: capture hole is not transparent (centerA={center.A}, cornerA={corner.A})");
        if (topBarPx.A < 250 || framePx.A < 250)
            throw new InvalidOperationException($"{label}: chrome/frame is not opaque (topBarA={topBarPx.A}, frameA={framePx.A})");
        if (backdrop == 2 || backdrop == 3 || backdrop == 4)
            throw new InvalidOperationException($"{label}: Windows 11 DWM backdrop is active (type={backdrop}), which blocks transparency!");
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string outDir = args[0];
        Directory.CreateDirectory(outDir);
        var settings = new AppSettings
        {
            Persist = false, // NEVER touch %APPDATA%\VocalPracticeRecorder\settings.json
            DataRoot = args.Length > 1 ? (args[1] == "demo" ? await DemoData.CreateAsync() : args[1]) : null,
            SidebarOpen = false,
            TopmostInCapture = false,
        };
        var win = new MainWindow(settings)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000, Top = -30000, Width = 1060, Height = 680,
            ShowActivated = false, ShowInTaskbar = false,
        };
        win.Show();
        await Task.Delay(1000);

        // Assert WDA_EXCLUDEFROMCAPTURE toggles cleanly on AllowsTransparency window.
        bool exclOn = Native.SetExcludeFromCapture(win, true);
        bool exclOff = Native.SetExcludeFromCapture(win, false);
        Log.Info($"snapshot wda: on={exclOn} off={exclOff}");
        if (!exclOn || !exclOff) throw new InvalidOperationException($"SetExcludeFromCapture failed: on={exclOn} off={exclOff}");

        // State 1: Capture Mode, sidebar collapsed.
        AssertCaptureTransparency(win, "capture-collapsed");
        // Save TRUE transparent PNG (alpha=0 hole!) + preview-over-checker PNG.
        var rootEl = (FrameworkElement)win.Content;
        Save(RenderTrueAlpha(rootEl, 1), Path.Combine(outDir, "1-capture-true-alpha.png"));
        Save(RenderOverChecker(rootEl, 1), Path.Combine(outDir, "1-capture-collapsed.png"));

        var rootGrid = (Grid)win.FindName("RootGrid");
        var topBar = (FrameworkElement)rootGrid.Children[0];
        Crop(RenderOverChecker(rootEl, 2), rootEl, topBar, Path.Combine(outDir, "4-topbar-capture-2x.png"));

        // State 1b: Capture Recording state.
        win.SetRecordingUi(true);
        ((TextBlock)win.FindName("ElapsedText")).Text = "1:24";
        ((ProgressBar)win.FindName("MicMeter")).Value = 0.65;
        ((ProgressBar)win.FindName("BackMeter")).Value = 0.48;
        await Task.Delay(200);
        AssertCaptureTransparency(win, "capture-recording");
        Save(RenderOverChecker(rootEl, 1), Path.Combine(outDir, "1b-capture-recording.png"));
        Crop(RenderOverChecker(rootEl, 2), rootEl, topBar, Path.Combine(outDir, "4b-topbar-recording-2x.png"));
        win.SetRecordingUi(false);
        await Task.Delay(150);

        // State 2: Narrow Capture Mode (640x420).
        win.Width = 640; win.Height = 420;
        await Task.Delay(350);
        AssertCaptureTransparency(win, "capture-narrow");
        Save(RenderOverChecker(rootEl, 1), Path.Combine(outDir, "2-capture-narrow-640.png"));

        // State 3: Capture Mode with sidebar expanded.
        win.Width = 1060; win.Height = 680;
        win.SetSidebar(true, save: false);
        await Task.Delay(350);
        AssertCaptureTransparency(win, "capture-sidebar-open");
        Save(RenderOverChecker(rootEl, 1), Path.Combine(outDir, "3-capture-sidebar-open.png"));

        // State 4: Editor Mode (must be 100% solid AND MediaElement must render actual video pixels!).
        var list = (ListBox)win.FindName("TakesList");
        if (list.Items.Count > 0)
        {
            var withCuts = list.Items.Cast<TakeItem>().FirstOrDefault(t => t.Session.Edit.Cuts.Count > 0);
            list.SelectedItem = withCuts ?? list.Items[^1];
            await Task.Delay(2500);
            if (win.FindName("EditorHost") is ContentControl { Content: EditorView ed } && ed.FindName("Timeline") is Editing.TimelineControl tl)
            {
                tl.Selection = new TimeRange(30, 34.5);
                await Task.Delay(250);
                var edTrue = RenderTrueAlpha(rootEl, 1);
                var edCenter = SamplePixel(edTrue, edTrue.PixelWidth * 2 / 3, edTrue.PixelHeight / 2);
                if (edCenter.A < 250) throw new InvalidOperationException($"Editor is not opaque: A={edCenter.A}");

                // Verify MediaElement renders real non-black video inside AllowsTransparency=True!
                var video = (FrameworkElement)ed.FindName("Video");
                var vr = video.TransformToAncestor(rootEl).TransformBounds(new Rect(0, 0, video.ActualWidth, video.ActualHeight));
                int bright = 0;
                for (int dy = 1; dy <= 4; dy++)
                    for (int dx = 1; dx <= 6; dx++)
                    {
                        var p = SamplePixel(edTrue, (int)(vr.X + vr.Width * dx / 7), (int)(vr.Y + vr.Height * dy / 5));
                        if (p.R > 30 || p.G > 30 || p.B > 30) bright++;
                    }
                Log.Info($"snapshot editor: alpha={edCenter.A} videoBrightSamples={bright}/24");
                if (bright < 4) throw new InvalidOperationException($"MediaElement appears black in AllowsTransparency window (bright={bright}/24)");

                var vg = (FrameworkElement)ed.FindName("VocalGain");
                var bg = (FrameworkElement)ed.FindName("BackingGain");
                if (vg.ActualWidth < 80 || bg.ActualWidth < 80)
                    throw new InvalidOperationException($"Slider squeezed too narrow: vocal={vg.ActualWidth:0}px backing={bg.ActualWidth:0}px");

                Save(edTrue, Path.Combine(outDir, "6-editor.png"));
                var full = RenderTrueAlpha(rootEl, 2);
                Crop(full, rootEl, topBar, Path.Combine(outDir, "7-topbar-editor-2x.png"));
                var grid = (Grid)ed.Content;
                Crop(full, rootEl, (FrameworkElement)grid.Children[3], Path.Combine(outDir, "11-mix-controls-2x.png"));
            }
        }

        win.Close();
        return 0;
    }

    private static RenderTargetBitmap RenderOverChecker(FrameworkElement el, double scale)
    {
        el.UpdateLayout();
        int pw = (int)Math.Ceiling(el.ActualWidth * scale), ph = (int)Math.Ceiling(el.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(MakeCheckerBrush(), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            var vb = new VisualBrush(el)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, el.ActualWidth, el.ActualHeight),
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        rtb.Render(dv);
        return rtb;
    }

    private static Brush MakeCheckerBrush()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x34)), null, new Rect(0, 0, 16, 16));
            var light = new SolidColorBrush(Color.FromRgb(0x32, 0x36, 0x3E));
            dc.DrawRectangle(light, null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(light, null, new Rect(8, 8, 8, 8));
        }
        return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void Crop(RenderTargetBitmap full, FrameworkElement root, FrameworkElement el, string path)
    {
        var r = el.TransformToAncestor(root).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        double s = full.PixelWidth / root.ActualWidth;
        var px = new Int32Rect((int)(r.X * s), (int)(r.Y * s),
            (int)Math.Min(r.Width * s, full.PixelWidth - r.X * s), (int)Math.Min(r.Height * s, full.PixelHeight - r.Y * s));
        if (px.Width <= 0 || px.Height <= 0) return;
        Save(new CroppedBitmap(full, px), path);
    }
}
