using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VocalPracticeRecorder.Core;
using VocalPracticeRecorder.Views;

namespace VocalPracticeRecorder;

/// <summary>
/// Renders the app's UI across all parallel states to PNG files (in-process RenderTargetBitmap, no screen capture)
/// and verifies Win32 region-hole invariants: <c>--snapshot &lt;outDir&gt; &lt;dataRoot|demo&gt;</c>.
/// Never modifies the user's real settings.json (Persist = false).
/// </summary>
internal static class Snapshot
{
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr hRgn, int x, int y);

    private static bool CheckHole(Window w, out bool holePunched, out bool borderSolid)
    {
        IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
        try
        {
            int res = GetWindowRgn(Native.Handle(w), rgn);
            if (res == 0) { holePunched = false; borderSolid = true; return false; } // ERROR = no custom region (solid)
            var b = Native.GetPhysicalBounds(w);
            var vp = (FrameworkElement)w.FindName("CaptureViewport");
            var tl = vp.PointToScreen(new Point(0, 0));
            var br = vp.PointToScreen(new Point(vp.ActualWidth, vp.ActualHeight));
            int cx = (int)((tl.X + br.X) / 2) - b.X;
            int cy = (int)((tl.Y + br.Y) / 2) - b.Y;
            holePunched = !PtInRegion(rgn, cx, cy); // hole centre must NOT be inside the window region
            borderSolid = PtInRegion(rgn, b.Width / 2, 10) && PtInRegion(rgn, b.Width - 2, b.Height - 2);
            return true;
        }
        finally { DeleteObject(rgn); }
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

        // State 1: Capture Mode, sidebar collapsed (default startup).
        bool hasRgn1 = CheckHole(win, out bool hole1, out bool border1);
        Log.Info($"snapshot capture-collapsed: hasRgn={hasRgn1} holePunched={hole1} borderSolid={border1}");
        if (!hasRgn1 || !hole1 || !border1) throw new InvalidOperationException($"Capture hole invariant failed: hasRgn={hasRgn1} hole={hole1} border={border1}");

        RenderCaptureWindow(win, Path.Combine(outDir, "1-capture-collapsed.png"), 1);
        var rootEl = (FrameworkElement)win.Content;
        var rootGrid = (Grid)win.FindName("RootGrid");
        var topBar = (FrameworkElement)rootGrid.Children[0];
        var fullCap = RenderCaptureBitmap(win, 2);
        Crop(fullCap, rootEl, topBar, Path.Combine(outDir, "4-topbar-capture-2x.png"));

        // State 1b: Capture Mode — Active Recording state (red border, live meters, timer, Stop/Discard).
        win.SetRecordingUi(true);
        ((TextBlock)win.FindName("ElapsedText")).Text = "1:24";
        ((ProgressBar)win.FindName("MicMeter")).Value = 0.65;
        ((ProgressBar)win.FindName("BackMeter")).Value = 0.48;
        await Task.Delay(200);
        RenderCaptureWindow(win, Path.Combine(outDir, "1b-capture-recording.png"), 1);
        Crop(RenderCaptureBitmap(win, 2), rootEl, topBar, Path.Combine(outDir, "4b-topbar-recording-2x.png"));
        win.SetRecordingUi(false);
        await Task.Delay(150);

        // State 2: Narrow Capture Mode (640x420) — checks top bar doesn't clip/overlap.
        win.Width = 640; win.Height = 420;
        await Task.Delay(400);
        bool hasRgnNarrow = CheckHole(win, out bool holeNarrow, out bool borderNarrow);
        if (!hasRgnNarrow || !holeNarrow || !borderNarrow) throw new InvalidOperationException("Narrow capture hole invariant failed");
        RenderCaptureWindow(win, Path.Combine(outDir, "2-capture-narrow-640.png"), 1);
        Crop(RenderCaptureBitmap(win, 2), rootEl, topBar, Path.Combine(outDir, "5-topbar-narrow-2x.png"));

        // State 3: Capture Mode with sidebar expanded.
        win.Width = 1060; win.Height = 680;
        win.SetSidebar(true, save: false);
        await Task.Delay(400);
        bool hasRgnSide = CheckHole(win, out bool holeSide, out bool borderSide);
        if (!hasRgnSide || !holeSide || !borderSide) throw new InvalidOperationException("Sidebar-open capture hole invariant failed");
        RenderCaptureWindow(win, Path.Combine(outDir, "3-capture-sidebar-open.png"), 1);

        // State 4: Editor Mode (solid window, no hole!).
        var list = (ListBox)win.FindName("TakesList");
        if (list.Items.Count > 0)
        {
            var withCuts = list.Items.Cast<TakeItem>().FirstOrDefault(t => t.Session.Edit.Cuts.Count > 0);
            list.SelectedItem = withCuts ?? list.Items[^1];
            await Task.Delay(2200);
            bool hasRgnEd = CheckHole(win, out _, out _);
            Log.Info($"snapshot editor: hasRgn={hasRgnEd}");
            if (hasRgnEd) throw new InvalidOperationException("Editor mode must clear the window region (solid window)");

            if (win.FindName("EditorHost") is ContentControl { Content: EditorView ed } && ed.FindName("Timeline") is Editing.TimelineControl tl)
            {
                tl.Selection = new TimeRange(30, 34.5);
                await Task.Delay(200);
                // Layout invariant: sliders must stay comfortably draggable even with sidebar + suggestion button open.
                var vg = (FrameworkElement)ed.FindName("VocalGain");
                var bg = (FrameworkElement)ed.FindName("BackingGain");
                if (vg.ActualWidth < 80 || bg.ActualWidth < 80)
                    throw new InvalidOperationException($"Slider squeezed too narrow: vocal={vg.ActualWidth:0}px backing={bg.ActualWidth:0}px");
                RenderElement(rootEl, Path.Combine(outDir, "6-editor.png"), 1);
                var full = RenderBitmap(rootEl, 2);
                Crop(full, rootEl, topBar, Path.Combine(outDir, "7-topbar-editor-2x.png"));
                Crop(full, rootEl, list, Path.Combine(outDir, "8-library-2x.png"));
                var grid = (Grid)ed.Content;
                Crop(full, rootEl, (FrameworkElement)ed.FindName("Timeline"), Path.Combine(outDir, "9-timeline-2x.png"));
                Crop(full, rootEl, (FrameworkElement)grid.Children[2], Path.Combine(outDir, "10-transport-2x.png"));
                Crop(full, rootEl, (FrameworkElement)grid.Children[3], Path.Combine(outDir, "11-mix-controls-2x.png"));
                Crop(full, rootEl, (FrameworkElement)grid.Children[4], Path.Combine(outDir, "12-export-bar-2x.png"));
            }
        }

        // State 5: Settings dialog.
        var settingsWin = new SettingsWindow(settings)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000, ShowActivated = false,
        };
        settingsWin.Show();
        await Task.Delay(400);
        RenderElement((FrameworkElement)settingsWin.Content, Path.Combine(outDir, "13-settings-2x.png"));
        settingsWin.Close();

        win.Close();
        return 0;
    }

    private static void RenderCaptureWindow(Window w, string path, double scale)
    {
        var bmp = RenderCaptureBitmap(w, scale);
        if (bmp != null) Save(bmp, path);
    }

    /// <summary>Renders the capture-mode window with a subtle checkerboard behind the transparent hole for visual review.</summary>
    private static RenderTargetBitmap? RenderCaptureBitmap(Window w, double scale)
    {
        var rootEl = (FrameworkElement)w.Content;
        rootEl.UpdateLayout();
        int pw = (int)Math.Ceiling(rootEl.ActualWidth * scale), ph = (int)Math.Ceiling(rootEl.ActualHeight * scale);
        if (pw <= 0 || ph <= 0) return null;
        var vp = (FrameworkElement)w.FindName("CaptureViewport");
        var hole = vp.IsVisible ? vp.TransformToAncestor(rootEl).TransformBounds(new Rect(0, 0, vp.ActualWidth, vp.ActualHeight)) : Rect.Empty;

        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.Black;
            dc.DrawRectangle(bg, null, new Rect(0, 0, rootEl.ActualWidth, rootEl.ActualHeight));
            var vb = new VisualBrush(rootEl)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, rootEl.ActualWidth, rootEl.ActualHeight),
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, rootEl.ActualWidth, rootEl.ActualHeight));
            if (!hole.IsEmpty) dc.DrawRectangle(MakeCheckerBrush(), null, hole);
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

    private static void RenderElement(FrameworkElement el, string path, double scale = 2)
    {
        var rtb = RenderBitmap(el, scale);
        if (rtb != null) Save(rtb, path);
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void Crop(RenderTargetBitmap? full, FrameworkElement root, FrameworkElement el, string path)
    {
        if (full == null) return;
        var r = el.TransformToAncestor(root).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        double s = full.PixelWidth / root.ActualWidth;
        var px = new Int32Rect((int)(r.X * s), (int)(r.Y * s),
            (int)Math.Min(r.Width * s, full.PixelWidth - r.X * s), (int)Math.Min(r.Height * s, full.PixelHeight - r.Y * s));
        if (px.Width <= 0 || px.Height <= 0) return;
        Save(new CroppedBitmap(full, px), path);
    }

    private static RenderTargetBitmap? RenderBitmap(FrameworkElement el, double scale)
    {
        el.UpdateLayout();
        int pw = (int)Math.Ceiling(el.ActualWidth * scale), ph = (int)Math.Ceiling(el.ActualHeight * scale);
        if (pw <= 0 || ph <= 0) return null;
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
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
}
