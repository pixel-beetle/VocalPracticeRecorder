using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VocalPracticeRecorder.Capture;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Views;

/// <summary>
/// Full-screen dimmed overlay (one per monitor) for dragging out the capture region.
/// All results are in physical desktop pixels.
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly PixelRect? _previous;
    private readonly Action<PixelRect?> _done;
    private readonly Canvas _canvas = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)) };
    private readonly Rectangle _sel = new() { Stroke = Brushes.White, StrokeThickness = 1.5, Visibility = Visibility.Collapsed };
    private readonly Rectangle _prev = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)), StrokeThickness = 1.5,
        StrokeDashArray = [4, 3], Visibility = Visibility.Collapsed,
    };
    private readonly Border _label = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 2, 6, 2), Visibility = Visibility.Collapsed,
        Child = new TextBlock { Foreground = Brushes.White, FontSize = 12 },
    };
    private Point? _dragStart;

    private RegionSelectorWindow(MonitorInfo monitor, PixelRect? previous, Action<PixelRect?> done)
    {
        _monitor = monitor;
        _previous = previous;
        _done = done;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = true;
        Cursor = Cursors.Cross;
        // Rough placement so WPF picks the right monitor DPI; exact physical bounds set below.
        Left = monitor.Bounds.X; Top = monitor.Bounds.Y; Width = 200; Height = 200;

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 30, 30, 30)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0), IsHitTestVisible = false,
            Child = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 15, TextAlignment = TextAlignment.Center,
                Text = previous.HasValue
                    ? "拖拽框选要录制的区域（建议框住视频画面）\nEnter 使用上次区域（黄色虚线） · Esc 取消"
                    : "拖拽框选要录制的区域（建议框住视频画面）\nEsc 取消",
            },
        };
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_prev);
        _canvas.Children.Add(_sel);
        _canvas.Children.Add(_label);
        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(hint);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            Native.SetPhysicalBounds(this, _monitor.Bounds);
            Native.ExcludeFromCapture(this);
        };
        Loaded += (_, _) => { UpdateDim(null); ShowPrevious(); Activate(); Focus(); };
        SizeChanged += (_, _) => { UpdateDim(CurrentRect()); ShowPrevious(); };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => _done(null);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _done(null);
            else if (e.Key == Key.Enter && _previous.HasValue) _done(_previous);
        };
    }

    /// <summary>Shows overlays on all monitors and returns the selected region, or null if cancelled.</summary>
    public static Task<PixelRect?> SelectAsync(PixelRect? previous)
    {
        var tcs = new TaskCompletionSource<PixelRect?>();
        var windows = new List<RegionSelectorWindow>();
        bool finished = false;
        void Done(PixelRect? r)
        {
            if (finished) return;
            finished = true;
            foreach (var w in windows) w.Close();
            tcs.TrySetResult(r);
        }
        var monitors = ScreenCapturer.EnumerateMonitors();
        foreach (var m in monitors)
        {
            var w = new RegionSelectorWindow(m, previous, Done);
            windows.Add(w);
            w.Show();
        }
        if (windows.Count == 0) tcs.TrySetResult(null);
        return tcs.Task;
    }

    private void ShowPrevious()
    {
        if (_previous is not { } p || !_monitor.Bounds.Contains(p.X + p.Width / 2, p.Y + p.Height / 2)) return;
        var tl = PointFromScreen(new Point(p.X, p.Y));
        var br = PointFromScreen(new Point(p.Right, p.Bottom));
        Canvas.SetLeft(_prev, tl.X); Canvas.SetTop(_prev, tl.Y);
        _prev.Width = Math.Max(0, br.X - tl.X); _prev.Height = Math.Max(0, br.Y - tl.Y);
        _prev.Visibility = Visibility.Visible;
    }

    private Rect? CurrentRect() =>
        _dragStart is { } s && _sel.Visibility == Visibility.Visible
            ? new Rect(Canvas.GetLeft(_sel), Canvas.GetTop(_sel), _sel.Width, _sel.Height)
            : null;

    private void UpdateDim(Rect? hole)
    {
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        _dim.Data = hole is { } h ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(h)) : full;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_canvas);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } s) return;
        var p = e.GetPosition(_canvas);
        var r = new Rect(s, p);
        Canvas.SetLeft(_sel, r.X); Canvas.SetTop(_sel, r.Y);
        _sel.Width = r.Width; _sel.Height = r.Height;
        _sel.Visibility = Visibility.Visible;
        UpdateDim(r);
        var phys = ToPhysical(s, p);
        ((TextBlock)_label.Child).Text = $"{phys.Width} × {phys.Height}";
        Canvas.SetLeft(_label, r.X);
        Canvas.SetTop(_label, r.Y > 26 ? r.Y - 24 : r.Bottom + 4);
        _label.Visibility = Visibility.Visible;
    }

    private PixelRect ToPhysical(Point a, Point b)
    {
        var pa = PointToScreen(a);
        var pb = PointToScreen(b);
        var raw = RegionMath.FromDrag((int)Math.Round(pa.X), (int)Math.Round(pa.Y), (int)Math.Round(pb.X), (int)Math.Round(pb.Y));
        return RegionMath.ClampToMonitor(raw, _monitor.Bounds);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } s) return;
        ReleaseMouseCapture();
        var phys = ToPhysical(s, e.GetPosition(_canvas));
        _dragStart = null;
        if (phys.IsEmpty)
        {
            // Too small (probably a click): reset and let the user try again.
            _sel.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Collapsed;
            UpdateDim(null);
            return;
        }
        _done(phys);
    }
}
