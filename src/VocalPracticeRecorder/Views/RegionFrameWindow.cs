using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Views;

/// <summary>Click-through red frame drawn just outside the capture region while recording.</summary>
public sealed class RegionFrameWindow : Window
{
    private const int Thickness = 3;
    private readonly PixelRect _bounds;

    public RegionFrameWindow(PixelRect region)
    {
        _bounds = new PixelRect(region.X - Thickness, region.Y - Thickness, region.Width + Thickness * 2, region.Height + Thickness * 2);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Left = _bounds.X; Top = _bounds.Y; Width = 100; Height = 100;
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 0xE5, 0x48, 0x4D)),
            BorderThickness = new Thickness(Thickness),
            Background = Brushes.Transparent,
        };
        SourceInitialized += (_, _) =>
        {
            Native.MakeClickThrough(this);
            Native.ExcludeFromCapture(this);
            Native.SetPhysicalBounds(this, _bounds);
        };
        // WPF may resize after DPI change; enforce the exact physical rect once shown.
        Loaded += (_, _) =>
        {
            Native.SetPhysicalBounds(this, _bounds);
            if (Content is Border b)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                b.BorderThickness = new System.Windows.Thickness(Thickness / dpi.DpiScaleX);
            }
        };
    }
}
