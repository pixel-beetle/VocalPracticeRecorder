using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VocalPracticeRecorder.Capture;
using VocalPracticeRecorder.Core;
using VocalPracticeRecorder.Views;

namespace VocalPracticeRecorder;

/// <summary>
/// Renders the app's own UI to PNG files (in-process RenderTargetBitmap, no screen capture) for visual review:
/// <c>--snapshot &lt;outDir&gt; &lt;dataRoot&gt;</c>.
/// </summary>
internal static class Snapshot
{
    public static async Task<int> RunAsync(string[] args)
    {
        string outDir = args[0];
        Directory.CreateDirectory(outDir);
        var settings = new AppSettings
        {
            DataRoot = args.Length > 1 ? (args[1] == "demo" ? await DemoData.CreateAsync() : args[1]) : null,
            Region = new PixelRect(640, 360, 1280, 720),
        };
        var win = new MainWindow(settings)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000, Top = -30000, Width = 1280, Height = 820,
            ShowActivated = false, ShowInTaskbar = false,
        };
        win.Show();
        await Task.Delay(1200);
        Render(win, Path.Combine(outDir, "1-record-panel.png"), 1);

        var list = (ListBox)win.FindName("TakesList");
        if (list.Items.Count > 0)
        {
            list.SelectedIndex = list.Items.Count - 1;
            await Task.Delay(2500);
            Render(win, Path.Combine(outDir, "2-editor.png"), 1);
            // 2x close-ups of dense areas, cropped from a 2x render of the whole window.
            var rootEl = (FrameworkElement)win.Content;
            var full = RenderBitmap(rootEl, 2);
            Crop(full, rootEl, list, Path.Combine(outDir, "3-library-2x.png"));
            if (win.FindName("EditorHost") is ContentControl { Content: EditorView ed })
            {
                var grid = (Grid)ed.Content;
                Crop(full, rootEl, (FrameworkElement)grid.Children[0], Path.Combine(outDir, "4-header-2x.png"));
                Crop(full, rootEl, (FrameworkElement)ed.FindName("Timeline"), Path.Combine(outDir, "5-timeline-2x.png"));
                Crop(full, rootEl, (FrameworkElement)grid.Children[3], Path.Combine(outDir, "6-transport-2x.png"));
                Crop(full, rootEl, (FrameworkElement)grid.Children[4], Path.Combine(outDir, "7-mix-controls-2x.png"));
                Crop(full, rootEl, (FrameworkElement)grid.Children[5], Path.Combine(outDir, "8-export-bar-2x.png"));
            }
        }

        // Recording bar rendered detached (never shown on screen).
        using (var c = new RecordingController())
        {
            var bar = new RecordingBar(c, new PixelRect(0, 0, 100, 100));
            var root = (FrameworkElement)bar.Content;
            bar.Content = null;
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();
            RenderElement(root, Path.Combine(outDir, "9-recording-bar-2x.png"));
        }

        win.Close();
        return 0;
    }

    private static void Render(Window w, string path, double scale)
    {
        var el = (FrameworkElement)w.Content;
        RenderElement(el, path, scale);
    }

    private static void RenderElement(FrameworkElement el, string path, double scale = 2)
    {
        var rtb = RenderBitmap(el, scale);
        if (rtb == null) return;
        Save(rtb, path);
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
            var bg = Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush
                     ?? Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.White;
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
