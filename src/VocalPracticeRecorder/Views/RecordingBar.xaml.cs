using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VocalPracticeRecorder.Capture;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Views;

/// <summary>Floating control bar shown while recording. Hidden from screen capture.</summary>
public partial class RecordingBar : Window
{
    private readonly RecordingController _controller;
    private readonly PixelRect _region;
    private readonly DispatcherTimer _timer;
    private float _micLevel, _backLevel;
    private int _tick;

    public event Action? StopRequested;
    public event Action? DiscardRequested;

    public RecordingBar(RecordingController controller, PixelRect region)
    {
        InitializeComponent();
        _controller = controller;
        _region = region;
        Left = region.X; Top = region.Y + region.Height;
        SourceInitialized += (_, _) => Native.ExcludeFromCapture(this);
        Loaded += (_, _) => PlaceOutsideRegion();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, OnTick, Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Below the region if it fits, else above, else inside the top-right corner.</summary>
    private void PlaceOutsideRegion()
    {
        var me = Native.GetPhysicalBounds(this);
        var monitors = ScreenCapturer.EnumerateMonitors();
        int mi = RegionMath.PickMonitor(_region, monitors.Select(m => m.Bounds).ToList());
        var mon = mi >= 0 ? monitors[mi].Bounds : new PixelRect(0, 0, 1920, 1080);
        const int gap = 10;
        int x = Math.Clamp(_region.X + _region.Width - me.Width, mon.X, mon.Right - me.Width);
        int y;
        if (_region.Bottom + gap + me.Height <= mon.Bottom) y = _region.Bottom + gap;
        else if (_region.Y - gap - me.Height >= mon.Y) y = _region.Y - gap - me.Height;
        else y = _region.Y + gap;
        Native.SetPhysicalBounds(this, new PixelRect(x, y, me.Width, me.Height));
    }

    private static float Smooth(float current, float peak) => peak > current ? peak : current * 0.85f;

    private void OnTick(object? sender, EventArgs e)
    {
        _micLevel = Smooth(_micLevel, _controller.TakeMicPeak());
        _backLevel = Smooth(_backLevel, _controller.TakeBackingPeak());
        MicMeter.Value = Meter.ToDisplay(_micLevel);
        BackMeter.Value = Meter.ToDisplay(_backLevel);
        Elapsed.Text = Library.FormatDuration(_controller.Elapsed);
        if (++_tick % 10 == 0) Dot.Opacity = Dot.Opacity > 0.5 ? 0.3 : 1.0;
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        DiscardButton.IsEnabled = false;
        StopButton.Content = "保存中…";
        StopRequested?.Invoke();
    }

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "放弃这次录制？文件会被删除。", "放弃录制", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StopButton.IsEnabled = false;
        DiscardButton.IsEnabled = false;
        DiscardRequested?.Invoke();
    }
}

public static class Meter
{
    /// <summary>Maps linear peak to a 0..1 meter over a -60..0 dBFS range.</summary>
    public static double ToDisplay(float peak)
    {
        if (peak <= 1e-6f) return 0;
        double db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }
}
