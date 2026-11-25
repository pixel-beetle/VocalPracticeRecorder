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

    private static void LoadDemoPitchTrace(MainWindow win)
    {
        var samples = new List<PitchSample>();
        const double now = 6.0;
        // Build a realistic 5.8s vocal phrase with vibrato, breath gap, slightly flat/sharp notes, ending on G#4 +5¢.
        for (double t = 0.2; t <= now; t += 0.02)
        {
            double? midi = null;
            if (t < 1.35)
                midi = 62.0 + 0.06 * Math.Sin(2 * Math.PI * 5.2 * t); // D4 in tune
            else if (t < 2.20)
                midi = 64.0 + (t < 1.55 ? (t - 1.35) / 0.20 * 0.0 : 0) + 0.18 * Math.Sin(2 * Math.PI * 5.6 * t); // E4 vibrato
            else if (t < 3.10)
                midi = 67.0 + 0.08 * Math.Sin(2 * Math.PI * 5.4 * t); // G4 in tune
            else if (t < 3.55)
                midi = null; // breath rest gap
            else if (t < 4.35)
                midi = 69.0 - 0.18 + 0.06 * Math.Sin(2 * Math.PI * 5.0 * t); // A4 slightly flat (-18¢, amber)
            else if (t < 5.05)
                midi = 71.0 + 0.34; // B4 sharp (+34¢, coral)
            else
                midi = 68.0 + 0.05 + 0.06 * Math.Sin(2 * Math.PI * 5.5 * (t - 5.05)); // G#4 in tune (+5¢, green)

            var r = midi is { } m
                ? PitchMath.FromFrequency(PitchMath.MidiToHz(m), 0.96, 0.18f)
                : PitchReading.Silent;
            samples.Add(new PitchSample(t, r));
        }
        // Ensure final live point is exact G#4 +5¢ for crisp inspection.
        samples[^1] = new PitchSample(now, PitchMath.FromFrequency(PitchMath.MidiToHz(68.05), 0.97, 0.20f));
        win.Controller.Pitch.LoadSnapshotSamples(samples, now);
        win.RefreshPitchUiForSnapshot();
    }

    private static void AssertPitchPanelInvariants(Window w, string label)
    {
        var rootEl = (FrameworkElement)w.Content;
        var vp = (FrameworkElement)w.FindName("CaptureViewport");
        var pp = (FrameworkElement)w.FindName("PitchPanelBorder");
        var trueBmp = RenderTrueAlpha(rootEl, 1);
        var hole = vp.TransformToAncestor(rootEl).TransformBounds(new Rect(0, 0, vp.ActualWidth, vp.ActualHeight));
        var panel = pp.TransformToAncestor(rootEl).TransformBounds(new Rect(0, 0, pp.ActualWidth, pp.ActualHeight));

        if (hole.Height < 110)
            throw new InvalidOperationException($"{label}: capture viewport squeezed too short ({hole.Height:0}px)");
        if (hole.Bottom > panel.Top + 0.5)
            throw new InvalidOperationException($"{label}: capture viewport overlaps pitch panel (holeBottom={hole.Bottom:0.0}, panelTop={panel.Top:0.0})");
        var panelCenter = SamplePixel(trueBmp, (int)(panel.X + panel.Width / 2), (int)(panel.Y + panel.Height / 2));
        if (panelCenter.A < 250)
            throw new InvalidOperationException($"{label}: pitch panel is not opaque (A={panelCenter.A})");
        Log.Info($"snapshot {label}: holeH={hole.Height:0} panelH={panel.Height:0} panelA={panelCenter.A}");
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
            PitchPanelOpen = false,
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

        // Load realistic vocal pitch trace so both TopBar pill and bottom pitch curve render live data.
        LoadDemoPitchTrace(win);
        await Task.Delay(100);

        // State 1: Capture Mode, sidebar & pitch drawer collapsed (live pitch pill active in TopBar).
        AssertCaptureTransparency(win, "capture-collapsed");
        var rootEl = (FrameworkElement)win.Content;
        Save(RenderTrueAlpha(rootEl, 1), Path.Combine(outDir, "1-capture-true-alpha.png"));
        Save(RenderOverChecker(rootEl, 1), Path.Combine(outDir, "1-capture-collapsed.png"));

        var rootGrid = (Grid)win.FindName("RootGrid");
        var topBar = (FrameworkElement)rootGrid.Children[0];
        Crop(RenderOverChecker(rootEl, 2), rootEl, topBar, Path.Combine(outDir, "4-topbar-capture-2x.png"));

        // State 1c: Capture Mode with docked bottom Pitch Monitor expanded (+ 2x close-up).
        win.SetPitchPanel(true, save: false);
        win.RefreshPitchUiForSnapshot();
        await Task.Delay(200);
        AssertCaptureTransparency(win, "capture-pitch-open");
        AssertPitchPanelInvariants(win, "capture-pitch-open");
        Save(RenderOverChecker(rootEl, 1), Path.Combine(outDir, "1c-capture-pitch-open.png"));
        var pitchPanel = (FrameworkElement)win.FindName("PitchPanelBorder");
        Crop(RenderOverChecker(rootEl, 2), rootEl, pitchPanel, Path.Combine(outDir, "14-pitch-monitor-2x.png"));

        // State 1b: Capture Recording state (with pitch drawer open so red frame + live pitch pill + curve are all verified).
        win.SetRecordingUi(true);
        ((TextBlock)win.FindName("ElapsedText")).Text = "1:24";
        ((ProgressBar)win.FindName("MicMeter")).Value = 0.65;
        ((ProgressBar)win.FindName("BackMeter")).Value = 0.48;
        win.RefreshPitchUiForSnapshot();
        await Task.Delay(200);
        AssertCaptureTransparency(win, "capture-recording");
        AssertPitchPanelInvariants(win, "capture-recording");
        Save(RenderOverChecker(rootEl, 1), Path.Combine(outDir, "1b-capture-recording.png"));
        Crop(RenderOverChecker(rootEl, 2), rootEl, topBar, Path.Combine(outDir, "4b-topbar-recording-2x.png"));
        win.SetRecordingUi(false);
        await Task.Delay(150);

        // State 2: Narrow Capture Mode (660x420) with pitch panel open (worst-case height & topbar width stress test).
        win.Width = 660; win.Height = 420;
        win.RefreshPitchUiForSnapshot();
        await Task.Delay(350);
        AssertCaptureTransparency(win, "capture-narrow-pitch");
        AssertPitchPanelInvariants(win, "capture-narrow-pitch");
        Save(RenderOverChecker(rootEl, 1), Path.Combine(outDir, "2-capture-narrow-660.png"));
        Crop(RenderOverChecker(rootEl, 2), rootEl, topBar, Path.Combine(outDir, "4c-topbar-narrow-2x.png"));

        // State 3: Capture Mode with sidebar AND pitch drawer both expanded.
        win.Width = 1060; win.Height = 680;
        win.SetSidebar(true, save: false);
        win.RefreshPitchUiForSnapshot();
        await Task.Delay(350);
        AssertCaptureTransparency(win, "capture-sidebar-pitch-open");
        AssertPitchPanelInvariants(win, "capture-sidebar-pitch-open");
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

        // State 5: Settings dialog + inline move confirmation overlay.
        var sw = new SettingsWindow(settings)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false,
        };
        sw.Show();
        await Task.Delay(300);
        await sw.ShowConfirmPreviewForSnapshotAsync("把现有的 3 遍录音（68 MB）一起移动到新位置？",
            "目标：D:\\VocalPractice\n「仅改位置」会把旧录音留在原处，不再显示在列表里。");
        await Task.Delay(150);
        Save(RenderOverSolid((FrameworkElement)sw.Content, 2), Path.Combine(outDir, "13-settings-confirm-2x.png"));
        sw.Close();

        win.Close();
        return 0;
    }

    private static RenderTargetBitmap RenderOverSolid(FrameworkElement el, double scale)
    {
        el.UpdateLayout();
        int pw = (int)Math.Ceiling(el.ActualWidth * scale), ph = (int)Math.Ceiling(el.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.Black;
            dc.DrawRectangle(bg, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
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
