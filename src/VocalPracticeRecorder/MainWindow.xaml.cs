using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VocalPracticeRecorder.Capture;
using VocalPracticeRecorder.Core;
using VocalPracticeRecorder.Views;

namespace VocalPracticeRecorder;

public sealed record GroupHeader(string Key, string Title, string SpanBadge);

public sealed class TakeItem
{
    public required SessionInfo Session { get; init; }
    public required GroupHeader Group { get; init; }
    public required int TakeNumber { get; init; }
    public bool MultiYear { get; init; }
    public string Header => MultiYear
        ? $"Take {TakeNumber} · {Session.CreatedAt:yy/MM/dd HH:mm}"
        : $"Take {TakeNumber} · {Session.CreatedAt:MM/dd HH:mm}";
    public string StarMark => Session.Starred ? " ★" : "";
    public string TagLine => string.Join(" · ", Library.Tags(Session));
    public BitmapImage? Thumbnail
    {
        get
        {
            var p = Session.PathOf(Session.ThumbFile);
            if (!File.Exists(p)) return null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
                b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                b.DecodePixelWidth = 160;
                b.UriSource = new Uri(p);
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }
    }
}

public partial class MainWindow : Window
{
    private const double CaptureMinW = 660, CaptureMinH = 340;
    private const double EditorMinW = 900, EditorMinH = 600;
    private const double PitchMinH = 110, PitchMaxH = 300;

    private readonly AppSettings _settings;
    private readonly RecordingController _controller = new();
    private readonly ObservableCollection<TakeItem> _takes = [];
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _boundsSaveTimer;
    private EditorView? _editor;
    private float _micLevel, _backLevel;
    private int _recTick;
    private bool _loaded;
    private bool _suppressSelection;
    private bool _devicesLoading;
    private double _sidebarWidth = 276;
    private bool _sidebarOpen;
    private int _sidebarAddedLeftPx;
    private bool _sidebarClamped;
    private double _pitchPanelHeight = 156;
    private bool _pitchPanelOpen;
    private int _pitchAddedBottomPx;
    private bool _pitchClamped;
    private string? _lastDeviceError;
    private PixelRect? _savedCaptureBounds;
    private bool _expandedForEditor;

    internal RecordingController Controller => _controller;
    private bool IsEditorOpen => _editor != null;
    private bool IsBusy => BusyOverlay.Visibility == Visibility.Visible;

    public MainWindow() : this(null) { }

    public MainWindow(AppSettings? settings)
    {
        _settings = settings ?? AppSettings.Load();
        InitializeComponent();
        var view = (CollectionViewSource)Resources["TakesView"];
        view.Source = _takes;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TakeItem.Group)));
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnMeterTick, Dispatcher);
        _boundsSaveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, OnSaveCaptureBounds, Dispatcher);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        SizeChanged += OnWindowGeometryChanged;
        LocationChanged += OnWindowGeometryChanged;
        StateChanged += OnWindowStateChanged;
        CaptureViewport.SizeChanged += (_, _) => UpdateCaptureTooltip();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Native.DisableBackdrop(this);
        Native.InstallBorderlessHooks(this, () => !_controller.IsRecording, resizeBorderDip: 6);
        if (_settings.CaptureWindowBounds is { } saved)
        {
            var mons = ScreenCapturer.EnumerateMonitors().Select(m => m.Bounds).ToList();
            Native.SetPhysicalBounds(this, RegionMath.ClampWindowToMonitors(saved, mons));
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        Native.DisableBackdrop(this);
        if (!Ffmpeg.IsAvailable)
        {
            MessageBox.Show(this, "找不到 ffmpeg.exe。\n请把 ffmpeg.exe 放到程序所在目录，或安装 FFmpeg 并加入 PATH。", "缺少 FFmpeg",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        SetSidebar(_settings.SidebarOpen, save: false, resizeOutward: false);
        _pitchPanelHeight = Math.Clamp(_settings.PitchPanelHeight, PitchMinH, PitchMaxH);
        SetPitchPanel(_settings.PitchPanelOpen, save: false, resizeOutward: false);
        PinToggle.IsChecked = _settings.TopmostInCapture;
        Topmost = _settings.TopmostInCapture;
        UpdateWindowFrameBrush();
        UpdateMaximizeGlyph();
        LoadDevices();
        RefreshLibrary();
        _meterTimer.Start();
        UpdateCaptureTooltip();
    }

    // ---------------- Capture Viewport Geometry ----------------

    private void OnWindowGeometryChanged(object? sender, EventArgs e)
    {
        UpdatePitchMaxHeight();
        UpdateCaptureTooltip();
        if (_loaded && !IsEditorOpen && !IsBusy && !_controller.IsRecording && WindowState == WindowState.Normal)
        {
            _boundsSaveTimer.Stop();
            _boundsSaveTimer.Start();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeGlyph();
        UpdateCaptureTooltip();
    }

    private void OnSaveCaptureBounds(object? sender, EventArgs e)
    {
        _boundsSaveTimer.Stop();
        if (IsEditorOpen || IsBusy || _controller.IsRecording || WindowState != WindowState.Normal) return;
        _settings.CaptureWindowBounds = Native.GetPhysicalBounds(this);
        _settings.Save();
    }

    private PixelRect ViewportPhysicalRect()
    {
        if (!CaptureViewport.IsVisible || CaptureViewport.ActualWidth < 2 || CaptureViewport.ActualHeight < 2) return default;
        var tl = CaptureViewport.PointToScreen(new Point(0, 0));
        var br = CaptureViewport.PointToScreen(new Point(CaptureViewport.ActualWidth, CaptureViewport.ActualHeight));
        return new PixelRect((int)Math.Round(tl.X), (int)Math.Round(tl.Y),
            (int)Math.Round(br.X - tl.X), (int)Math.Round(br.Y - tl.Y));
    }

    private void UpdateCaptureTooltip()
    {
        if (!_loaded || IsEditorOpen || IsBusy) return;
        var vp = ViewportPhysicalRect();
        if (!vp.IsEmpty)
        {
            var (ow, oh) = RegionMath.OutputSize(vp.Width, vp.Height, _settings.MaxHeight);
            StartButton.ToolTip = $"录制 {vp.Width}×{vp.Height}（输出 {ow}×{oh}）";
        }
    }

    // ---------------- Window Chrome & Sidebar ----------------

    private void OnTopBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { OnToggleMaximize(sender, e); return; }
        try { DragMove(); } catch { }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        bool max = WindowState == WindowState.Maximized;
        MaxGlyph.Data = Geometry.Parse(max
            ? "M2,4.5 H9.5 V12 H2 Z M4.5,4.5 V2 H12 V9.5 H9.5"
            : "M2.5,2.5 H11.5 V11.5 H2.5 Z");
        MaxButton.ToolTip = max ? "还原" : "最大化";
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        bool pin = PinToggle.IsChecked == true;
        _settings.TopmostInCapture = pin;
        _settings.Save();
        if (!IsEditorOpen && !_controller.IsRecording) Topmost = pin;
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e) =>
        SetSidebar(SidebarToggle.IsChecked == true, save: true, resizeOutward: true);

    public void SetSidebar(bool open, bool save = true, bool resizeOutward = true)
    {
        bool changed = open != _sidebarOpen;
        if (!open && SidebarCol.ActualWidth >= 200) _sidebarWidth = SidebarCol.ActualWidth;

        bool canOutward = changed && resizeOutward && _loaded && !IsEditorOpen && WindowState == WindowState.Normal;
        var dpi = VisualTreeHelper.GetDpi(this);
        int panelPx = (int)Math.Round((_sidebarWidth + 1.0) * dpi.DpiScaleX);

        if (open)
        {
            if (canOutward)
            {
                var cur = Native.GetPhysicalBounds(this);
                var work = Native.GetMonitorWorkArea(this);
                if (cur.Right > work.X && cur.X < work.Right)
                {
                    var expanded = RegionMath.ExpandOutwardLeft(cur, work, panelPx, out _sidebarAddedLeftPx);
                    _sidebarClamped = _sidebarAddedLeftPx < panelPx;
                    if (expanded != cur) Native.SetPhysicalBounds(this, expanded);
                }
            }
            else if (changed)
            {
                // Startup restore already includes sidebar width in saved bounds.
                _sidebarAddedLeftPx = panelPx;
                _sidebarClamped = false;
            }
        }

        _sidebarOpen = open;
        SidebarToggle.IsChecked = open;
        SidebarPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarCol.MinWidth = open ? 220 : 0;
        SidebarCol.MaxWidth = open ? 360 : 0;
        SidebarCol.Width = open ? new GridLength(_sidebarWidth) : new GridLength(0);

        if (!open && canOutward)
        {
            var cur = Native.GetPhysicalBounds(this);
            var work = Native.GetMonitorWorkArea(this);
            if (cur.Right > work.X && cur.X < work.Right)
            {
                int shrinkPx = _sidebarClamped ? _sidebarAddedLeftPx : panelPx;
                int minW = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
                var collapsed = RegionMath.CollapseInwardLeft(cur, shrinkPx, minW);
                if (collapsed != cur) Native.SetPhysicalBounds(this, collapsed);
            }
            _sidebarAddedLeftPx = 0;
            _sidebarClamped = false;
        }

        if (save)
        {
            _settings.SidebarOpen = open;
            if (!IsEditorOpen && WindowState == WindowState.Normal && _loaded)
                _settings.CaptureWindowBounds = Native.GetPhysicalBounds(this);
            _settings.Save();
        }
        Dispatcher.BeginInvoke(UpdateCaptureTooltip, DispatcherPriority.Loaded);
    }

    private void OnTogglePitch(object sender, RoutedEventArgs e)
    {
        if (_controller.IsRecording)
        {
            // Keep capture hole geometry fixed while recording; live pitch readout stays bright and active.
            PitchToggle.IsChecked = _pitchPanelOpen;
            return;
        }
        SetPitchPanel(PitchToggle.IsChecked == true, save: true, resizeOutward: true);
    }

    public void SetPitchPanel(bool open, bool save = true, bool resizeOutward = true)
    {
        bool changed = open != _pitchPanelOpen;
        if (!open && PitchRow.ActualHeight >= PitchMinH)
            _pitchPanelHeight = Math.Clamp(PitchRow.ActualHeight, PitchMinH, PitchMaxH);

        bool canOutward = changed && resizeOutward && _loaded && !IsEditorOpen && WindowState == WindowState.Normal;
        var dpi = VisualTreeHelper.GetDpi(this);
        // Collapsed: ViewportFrame.Bottom=6; Expanded: ViewportFrame.Bottom=3 + PitchSplitter=3 + PitchRow=H -> exact delta = H.
        int panelPx = (int)Math.Round(_pitchPanelHeight * dpi.DpiScaleY);
        double targetWindowHeightDip = Height;

        if (open)
        {
            if (canOutward)
            {
                var cur = Native.GetPhysicalBounds(this);
                var work = Native.GetMonitorWorkArea(this);
                if (cur.Bottom > work.Y && cur.Y < work.Bottom)
                {
                    var expanded = RegionMath.ExpandOutwardBottom(cur, work, panelPx, out _pitchAddedBottomPx);
                    _pitchClamped = _pitchAddedBottomPx < panelPx;
                    if (expanded != cur) Native.SetPhysicalBounds(this, expanded);
                    targetWindowHeightDip = expanded.Height / dpi.DpiScaleY;
                }
            }
            else if (changed)
            {
                _pitchAddedBottomPx = panelPx;
                _pitchClamped = false;
                if (_loaded) targetWindowHeightDip = Native.GetPhysicalBounds(this).Height / dpi.DpiScaleY;
            }
        }

        _pitchPanelOpen = open;
        PitchToggle.IsChecked = open;
        PitchChevron.Data = Geometry.Parse(open ? "M1,4.5 L4,1.5 L7,4.5" : "M1,1.5 L4,4.5 L7,1.5");
        PitchPanelBorder.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PitchSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ViewportFrame.BorderThickness = open ? new Thickness(6, 3, 6, 3) : new Thickness(6, 3, 6, 6);

        UpdatePitchMaxHeight(targetWindowHeightDip);
        PitchRow.MinHeight = open ? PitchMinH : 0;
        PitchRow.Height = open ? new GridLength(Math.Min(_pitchPanelHeight, PitchRow.MaxHeight)) : new GridLength(0);

        if (!open && canOutward)
        {
            var cur = Native.GetPhysicalBounds(this);
            var work = Native.GetMonitorWorkArea(this);
            if (cur.Bottom > work.Y && cur.Y < work.Bottom)
            {
                int shrinkPx = _pitchClamped ? _pitchAddedBottomPx : panelPx;
                int minH = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
                var collapsed = RegionMath.CollapseInwardBottom(cur, shrinkPx, minH);
                if (collapsed != cur) Native.SetPhysicalBounds(this, collapsed);
            }
            _pitchAddedBottomPx = 0;
            _pitchClamped = false;
        }

        if (save)
        {
            _settings.PitchPanelOpen = open;
            _settings.PitchPanelHeight = Math.Round(_pitchPanelHeight);
            if (!IsEditorOpen && WindowState == WindowState.Normal && _loaded)
                _settings.CaptureWindowBounds = Native.GetPhysicalBounds(this);
            _settings.Save();
        }
        if (open) PitchMonitor.UpdateFrom(_controller.Pitch);
        Dispatcher.BeginInvoke(UpdateCaptureTooltip, DispatcherPriority.Loaded);
    }

    private void UpdatePitchMaxHeight(double? windowHeightDip = null)
    {
        if (!_pitchPanelOpen)
        {
            PitchRow.MaxHeight = 0;
            return;
        }
        double totalH = windowHeightDip ?? (RootGrid.ActualHeight > 100 ? RootGrid.ActualHeight : Height);
        double bodyH = totalH - 48;
        PitchRow.MaxHeight = Math.Clamp(bodyH - 130, PitchMinH, PitchMaxH);
    }

    private void UpdateWindowFrameBrush()
    {
        if (IsEditorOpen)
        {
            OuterFrame.BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush");
            return;
        }
        var frameBrush = (Brush)FindResource(_controller.IsRecording ? "RecordBrush" : "FrameIdleBrush");
        ViewportFrame.BorderBrush = frameBrush;
        OuterFrame.BorderBrush = frameBrush;
    }

    private void OnPitchSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!_pitchPanelOpen) return;
        _pitchPanelHeight = Math.Clamp(PitchRow.ActualHeight, PitchMinH, PitchMaxH);
        _settings.PitchPanelHeight = Math.Round(_pitchPanelHeight);
        _settings.Save();
        UpdateCaptureTooltip();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_controller.IsRecording) return;
        string? reopen = _editor?.Session.Id;
        if (IsEditorOpen) CloseEditor(restoreCapture: false);
        // Drop Topmost while Settings is open so OpenFolderDialog, prompts, and Explorer never hide behind MainWindow.
        bool prevTopmost = Topmost;
        Topmost = false;
        SettingsWindow dlg;
        try
        {
            dlg = new SettingsWindow(_settings) { Owner = this };
            dlg.ShowDialog();
        }
        finally
        {
            if (!IsEditorOpen) Topmost = _settings.TopmostInCapture;
            else Topmost = prevTopmost;
        }
        string? target = dlg.LibraryChanged ? null : reopen;
        RefreshLibrary(select: target);
        if (target == null || TakesList.SelectedItem == null) ShowCaptureMode();
    }

    // ---------------- Devices ----------------

    private void LoadDevices()
    {
        _devicesLoading = true;
        try
        {
            var mics = AudioDevices.List(DataFlow.Capture);
            var outs = AudioDevices.List(DataFlow.Render);
            MicBox.ItemsSource = mics;
            OutputBox.ItemsSource = outs;
            MicBox.SelectedItem = mics.FirstOrDefault(d => d.Id == _settings.MicDeviceId)
                                  ?? mics.FirstOrDefault(d => d.Id == AudioDevices.DefaultId(DataFlow.Capture)) ?? mics.FirstOrDefault();
            OutputBox.SelectedItem = outs.FirstOrDefault(d => d.Id == _settings.OutputDeviceId)
                                     ?? outs.FirstOrDefault(d => d.Id == AudioDevices.DefaultId(DataFlow.Render)) ?? outs.FirstOrDefault();
        }
        finally { _devicesLoading = false; }
        OpenDevices();
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_devicesLoading) return;
        OpenDevices();
    }

    private void OpenDevices()
    {
        if (_controller.IsRecording) return;
        _lastDeviceError = null;
        DeviceErrorButton.Visibility = Visibility.Collapsed;
        if (MicBox.SelectedItem is not AudioDeviceItem mic || OutputBox.SelectedItem is not AudioDeviceItem output)
        {
            ShowDeviceError("没有找到可用的音频设备。");
            return;
        }
        try
        {
            _controller.OpenDevices(mic.Id, output.Id);
            _settings.MicDeviceId = mic.Id;
            _settings.OutputDeviceId = output.Id;
            _settings.Save();
            var f = _controller.MicFormat;
            MicBox.ToolTip = f == null ? mic.Name : $"{mic.Name}（{f.SampleRate / 1000.0:0.#} kHz · {f.Channels} 声道）";
            OutputBox.ToolTip = output.Name;
        }
        catch (Exception ex)
        {
            Log.Error("open devices", ex);
            ShowDeviceError("无法打开音频设备：" + ex.Message);
        }
    }

    private void ShowDeviceError(string text)
    {
        _lastDeviceError = text;
        DeviceErrorButton.ToolTip = text;
        DeviceErrorButton.Visibility = Visibility.Visible;
    }

    private void OnDeviceErrorClick(object sender, RoutedEventArgs e)
    {
        if (_lastDeviceError != null) MessageBox.Show(this, _lastDeviceError, "音频设备错误", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnMeterTick(object? sender, EventArgs e)
    {
        if (IsEditorOpen && !_controller.IsRecording) return;
        float mp = _controller.TakeMicPeak(), bp = _controller.TakeBackingPeak();
        _micLevel = mp > _micLevel ? mp : _micLevel * 0.88f;
        _backLevel = bp > _backLevel ? bp : _backLevel * 0.88f;
        MicMeter.Value = Library.PeakToMeter(_micLevel);
        BackMeter.Value = Library.PeakToMeter(_backLevel);
        UpdatePitchUi(snapAutoFollow: false);
        if (_controller.IsRecording)
        {
            ElapsedText.Text = Library.FormatDuration(_controller.Elapsed);
            if (++_recTick % 15 == 0) RecDot.Opacity = RecDot.Opacity > 0.5 ? 0.3 : 1.0;
        }
    }

    internal void RefreshPitchUiForSnapshot() => UpdatePitchUi(snapAutoFollow: true);

    private void UpdatePitchUi(bool snapAutoFollow)
    {
        var reading = _controller.Pitch.GetLiveReading();
        var zoneBrush = PitchMonitorControl.BrushForZone(reading.Zone);
        if (reading.IsVoiced)
        {
            PitchNoteText.Text = reading.NoteName;
            PitchNoteText.Foreground = zoneBrush;
            PitchCentsText.Text = reading.CentsText;
            PitchCentsText.Foreground = zoneBrush;
            PitchBarMarker.Background = zoneBrush;
            PitchBarMarker.Visibility = Visibility.Visible;
            double w = PitchBarCanvas.ActualWidth > 12 ? PitchBarCanvas.ActualWidth : 83;
            double frac = Math.Clamp(reading.Cents / 50.0, -1.0, 1.0);
            double x = w * 0.5 + frac * (w * 0.5 - PitchBarMarker.Width * 0.5) - PitchBarMarker.Width * 0.5;
            Canvas.SetLeft(PitchBarMarker, x);
        }
        else
        {
            PitchNoteText.Text = "--";
            PitchNoteText.Foreground = (Brush)FindResource("SubtleTextBrush");
            PitchCentsText.Text = "";
            PitchBarMarker.Visibility = Visibility.Collapsed;
        }
        if (_pitchPanelOpen) PitchMonitor.UpdateFrom(_controller.Pitch, snapAutoFollow);
    }

    // ---------------- Recording (in-place viewfinder) ----------------

    private void OnBackToCapture(object sender, RoutedEventArgs e) => ShowCaptureMode();

    private void ShowCaptureMode()
    {
        CloseEditor(restoreCapture: true);
        _suppressSelection = true;
        TakesList.SelectedItem = null;
        _suppressSelection = false;
        EditorTopBar.Visibility = Visibility.Collapsed;
        EditorHostBorder.Visibility = Visibility.Collapsed;
        CaptureTopBar.Visibility = Visibility.Visible;
        PinToggle.Visibility = Visibility.Visible;
        CaptureAreaGrid.Visibility = Visibility.Visible;
        Topmost = _settings.TopmostInCapture;
        UpdateWindowFrameBrush();
        if (!_controller.DevicesOpen) OpenDevices();
        UpdateCaptureTooltip();
    }

    private void OnStartRecording(object sender, RoutedEventArgs e)
    {
        if (!Ffmpeg.IsAvailable) { MessageBox.Show(this, "缺少 ffmpeg.exe，无法录制。"); return; }
        if (!_controller.DevicesOpen) OpenDevices();
        if (!_controller.DevicesOpen) return;

        var vp = ViewportPhysicalRect();
        var monitors = ScreenCapturer.EnumerateMonitors();
        int mi = RegionMath.PickMonitor(vp, monitors.Select(m => m.Bounds).ToList());
        var region = mi >= 0 ? RegionMath.ClampToMonitor(vp, monitors[mi].Bounds) : default;
        if (region.IsEmpty)
        {
            MessageBox.Show(this, "取景框太小或超出屏幕范围，请把窗口拉大后再录制。", "无法录制", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string title = Native.TopLevelTitleAt(region.X + region.Width / 2, region.Y + region.Height / 2, Native.Handle(this));
        Native.SetExcludeFromCapture(this, true);
        try
        {
            var s = _controller.Start(region, title, _settings.RecordingsDir, _settings.Fps, _settings.MaxHeight);
            Log.Info($"recording {s.Id} region={region} title='{title}'");
        }
        catch (Exception ex)
        {
            Native.SetExcludeFromCapture(this, false);
            Log.Error("start", ex);
            MessageBox.Show(this, "无法开始录制：" + ex.Message, "录制失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _recTick = 0;
        ElapsedText.Text = "0:00";
        RecDot.Opacity = 1.0;
        Topmost = true; // stay above the browser while recording so Stop is always clickable
        SetRecordingUi(true);
    }

    private async void OnStopRecording(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        DiscardButton.IsEnabled = false;
        StopButton.Content = "保存中…";
        await FinishRecordingAsync(discard: false);
    }

    private async void OnDiscardRecording(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "放弃这次录制？", "放弃", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StopButton.IsEnabled = false;
        DiscardButton.IsEnabled = false;
        await FinishRecordingAsync(discard: true);
    }

    internal void SetRecordingUi(bool recording)
    {
        StartButton.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        RecordingControls.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = true;
        DiscardButton.IsEnabled = true;
        StopButton.Content = "■ 停止";

        // While recording, hide device dropdowns and enlarge live level meters.
        MicBox.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        OutputBox.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        MicMeter.Height = recording ? 6 : 3;
        BackMeter.Height = recording ? 6 : 3;
        MicCol.MaxWidth = recording ? 110 : 240;
        OutCol.MaxWidth = recording ? 110 : 240;

        var frameBrush = (Brush)FindResource(recording ? "RecordBrush" : "FrameIdleBrush");
        ViewportFrame.BorderBrush = frameBrush;
        OuterFrame.BorderBrush = frameBrush;

        SidebarToggle.IsEnabled = !recording;
        SettingsButton.IsEnabled = !recording;
        PinToggle.IsEnabled = !recording;
        TakesList.IsEnabled = !recording;
        CleanupButton.IsEnabled = !recording;
        PitchSplitter.IsEnabled = !recording;
    }

    private async Task FinishRecordingAsync(bool discard)
    {
        SessionInfo? s = null;
        Exception? error = null;
        try
        {
            if (discard) await _controller.DiscardAsync();
            else s = await _controller.StopAsync();
        }
        catch (Exception ex)
        {
            error = ex;
            Log.Error("stop", ex);
        }
        finally
        {
            Native.SetExcludeFromCapture(this, false);
            SetRecordingUi(false);
        }

        if (error != null)
        {
            Topmost = _settings.TopmostInCapture;
            MessageBox.Show(this, "录制出错：" + error.Message, "录制失败", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLibrary();
            return;
        }
        if (s == null)
        {
            Topmost = _settings.TopmostInCapture;
            return;
        }

        SetBusy("保存音频…");
        try
        {
            await SessionPostProcessor.CompressRawAsync(s);
            s.Save();
            SetBusy("对齐分析…");
            var audio = await SessionAudio.LoadAsync(s);
            await Task.Run(() =>
            {
                s.Edit.MicChannel = ChannelSelector.Detect(audio.MicInterleaved, audio.MicChannels);
                var cal = _settings.CalibrationFor(s.DeviceComboKey);
                var est = SessionPostProcessor.AutoAlign(s, audio, cal);
                if (est.IsReliable) _settings.RecordLatency(s.DeviceComboKey, est.DelaySeconds * 1000);
                Log.Info($"post {s.Id}: channel={s.Edit.MicChannel} delay={est.DelaySeconds * 1000:0.0}ms strength={est.Confidence:0} " +
                         $"bands={est.BandsAgreeing}/3 windows={est.Windows} reliable={est.IsReliable} mode={s.Edit.AlignMode} " +
                         $"offset={s.Edit.VocalOffsetMs:0}ms prior={cal.MeasuredLatencyMs?.ToString("0.0") ?? "-"} " +
                         $"offsets v={s.Video.StartOffset:0.000} m={s.Mic.StartOffset:0.000} b={s.Backing.StartOffset:0.000} frames={s.Video.FrameCount}");
            });
            await SessionPostProcessor.MakeThumbnailAsync(s);
            s.Incomplete = false;
            s.Save();
            RefreshLibrary(select: s.Id, preloaded: audio);
        }
        catch (Exception ex)
        {
            Log.Error("post-process", ex);
            MessageBox.Show(this, "处理录音时出错：" + ex.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLibrary();
        }
        finally { SetBusy(null); }
    }

    private void SetBusy(string? text)
    {
        BusyOverlay.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (text != null) BusyText.Text = text;
    }

    // ---------------- Library & Editor ----------------

    private SessionAudio? _preloaded;
    private string _libraryQuery = "";
    private bool _starFilterOnly;

    private void OnLibraryFilterChanged(object sender, TextChangedEventArgs e)
    {
        _libraryQuery = LibrarySearchBox.Text.Trim();
        bool hasText = _libraryQuery.Length > 0;
        LibrarySearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        RefreshLibrary();
    }

    private void OnClearLibrarySearch(object sender, RoutedEventArgs e)
    {
        LibrarySearchBox.Text = "";
        LibrarySearchBox.Focus();
    }

    private void OnStarFilterClick(object sender, RoutedEventArgs e)
    {
        _starFilterOnly = StarFilterToggle.IsChecked == true;
        StarFilterToggle.Foreground = _starFilterOnly
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))
            : (Brush)FindResource("TextFillColorPrimaryBrush");
        RefreshLibrary();
    }

    private void OnCleanupLibrary(object sender, RoutedEventArgs e)
    {
        if (_controller.IsRecording) return;
        bool prevTopmost = Topmost;
        Topmost = false;
        CleanupWindow dlg;
        try
        {
            dlg = new CleanupWindow(_settings.RecordingsDir, _editor?.Session.Id) { Owner = this };
            dlg.ShowDialog();
        }
        finally
        {
            Topmost = IsEditorOpen ? prevTopmost : _settings.TopmostInCapture;
        }
        if (dlg.Cleaned) RefreshLibrary();
    }

    private void RefreshLibrary(string? select = null, SessionAudio? preloaded = null)
    {
        string? keep = select ?? (TakesList.SelectedItem as TakeItem)?.Session.Id;
        var sessions = Library.LoadAll(_settings.RecordingsDir);
        if (_editor != null)
        {
            int i = sessions.FindIndex(x => x.Id == _editor.Session.Id);
            if (i >= 0) sessions[i] = _editor.Session;
        }
        var groups = Library.Group(sessions);
        bool hasFilter = _libraryQuery.Length > 0 || _starFilterOnly;

        _suppressSelection = true;
        _takes.Clear();
        TakeItem? toSelect = null;
        foreach (var g in groups)
        {
            if (_libraryQuery.Length > 0 && !g.Title.Contains(_libraryQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            bool multiYear = g.Takes.Count > 1 && g.EarliestAt.Year != g.LatestAt.Year;
            var header = new GroupHeader(g.Key, g.Title, g.SpanBadge);
            for (int i = 0; i < g.Takes.Count; i++)
            {
                var take = g.Takes[i];
                if (_starFilterOnly && !take.Starred) continue;
                var item = new TakeItem { Session = take, Group = header, TakeNumber = i + 1, MultiYear = multiYear };
                _takes.Add(item);
                if (item.Session.Id == keep) toSelect = item;
            }
        }
        EmptyHint.Text = hasFilter && sessions.Count > 0 ? "没有匹配的录音" : "暂无录音";
        EmptyHint.Visibility = _takes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _suppressSelection = false;

        if (toSelect != null)
        {
            bool reopen = select != null || _editor == null || _editor.Session.Id != toSelect.Session.Id;
            _preloaded = preloaded;
            _suppressSelection = !reopen;
            TakesList.SelectedItem = toSelect;
            _suppressSelection = false;
            TakesList.ScrollIntoView(toSelect);
        }
    }

    private void EnterEditorChrome()
    {
        if (!IsEditorOpen && CaptureAreaGrid.Visibility == Visibility.Visible)
        {
            if (WindowState == WindowState.Normal)
            {
                var cur = Native.GetPhysicalBounds(this);
                _savedCaptureBounds = cur;
                var dpi = VisualTreeHelper.GetDpi(this);
                int minW = (int)Math.Ceiling(980 * dpi.DpiScaleX), minH = (int)Math.Ceiling(680 * dpi.DpiScaleY);
                var expanded = RegionMath.ExpandForEditor(cur, Native.GetMonitorWorkArea(this), minW, minH);
                _expandedForEditor = expanded != cur;
                if (_expandedForEditor) Native.SetPhysicalBounds(this, expanded);
            }
            MinWidth = EditorMinW;
            MinHeight = EditorMinH;
        }
        Topmost = false;
        CaptureTopBar.Visibility = Visibility.Collapsed;
        PinToggle.Visibility = Visibility.Collapsed;
        CaptureAreaGrid.Visibility = Visibility.Collapsed;
        EditorTopBar.Visibility = Visibility.Visible;
        EditorHostBorder.Visibility = Visibility.Visible;
        OuterFrame.BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush");
    }

    private async void OnTakeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || TakesList.SelectedItem is not TakeItem item) return;
        if (_controller.IsRecording) return;
        if (_editor?.Session.Id == item.Session.Id && _preloaded == null) return;
        var s = item.Session;
        if (s.Incomplete)
        {
            MessageBox.Show(this, "这次录音未正常结束，无法编辑，可右键删除。", "录音不完整");
            return;
        }
        CloseEditor(restoreCapture: false);
        EnterEditorChrome();
        var preloaded = _preloaded;
        _preloaded = null;
        if (preloaded == null) SetBusy("加载中…");
        try
        {
            var audio = preloaded ?? await SessionAudio.LoadAsync(s);
            if (TakesList.SelectedItem != item) return;
            _editor = new EditorView(s, audio, _settings, item.TakeNumber);
            _editor.SessionChanged += OnEditorSessionChanged;
            EditorHost.Content = _editor;
            SyncEditorHeader(s, item.TakeNumber);
            _editor.Focus();
        }
        catch (Exception ex)
        {
            Log.Error("open take", ex);
            MessageBox.Show(this, "无法打开录音：" + ex.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowCaptureMode();
        }
        finally { if (preloaded == null) SetBusy(null); }
    }

    private void SyncEditorHeader(SessionInfo s, int takeNumber)
    {
        EditorTitleBox.Text = s.SongTitle;
        EditorTakeMeta.Text = $"Take {takeNumber} · {s.CreatedAt:MM/dd HH:mm}";
        EditorStarButton.IsChecked = s.Starred;
        EditorStarButton.Content = s.Starred ? "★" : "☆";
        EditorStarButton.Foreground = s.Starred
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))
            : (Brush)FindResource("TextFillColorPrimaryBrush");
    }

    private void OnEditorTitleKey(object sender, KeyEventArgs e)
    {
        if (_editor == null) return;
        if (e.Key == Key.Enter) { CommitEditorTitle(); _editor.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EditorTitleBox.Text = _editor.Session.SongTitle; _editor.Focus(); e.Handled = true; }
    }

    private void OnEditorTitleCommitted(object sender, RoutedEventArgs e) => CommitEditorTitle();

    private void CommitEditorTitle()
    {
        if (_editor == null) return;
        var s = _editor.Session;
        var t = EditorTitleBox.Text.Trim();
        if (t.Length == 0 || t == s.SongTitle) { EditorTitleBox.Text = s.SongTitle; return; }
        s.SongTitle = t;
        s.Save();
        RefreshLibrary();
    }

    private void OnEditorStar(object sender, RoutedEventArgs e)
    {
        if (_editor == null) return;
        var s = _editor.Session;
        s.Starred = !s.Starred;
        s.Save();
        var cur = TakesList.SelectedItem as TakeItem;
        SyncEditorHeader(s, cur?.TakeNumber ?? 1);
        OnEditorSessionChanged(s, false);
    }

    private void OnEditorSessionChanged(SessionInfo s, bool regroup)
    {
        if (regroup) { RefreshLibrary(); return; }
        var item = _takes.FirstOrDefault(t => t.Session.Id == s.Id);
        if (item == null) return;
        int idx = _takes.IndexOf(item);
        _suppressSelection = true;
        var replacement = new TakeItem { Session = s, Group = item.Group, TakeNumber = item.TakeNumber, MultiYear = item.MultiYear };
        _takes[idx] = replacement;
        TakesList.SelectedItem = replacement;
        _suppressSelection = false;
    }

    private void CloseEditor(bool restoreCapture)
    {
        if (_editor != null)
        {
            _editor.SessionChanged -= OnEditorSessionChanged;
            _editor.Shutdown();
            EditorHost.Content = null;
            _editor = null;
        }
        if (restoreCapture)
        {
            MinWidth = CaptureMinW;
            MinHeight = CaptureMinH;
            if (_expandedForEditor && _savedCaptureBounds is { } cb && WindowState == WindowState.Normal)
                Native.SetPhysicalBounds(this, cb);
            _expandedForEditor = false;
        }
    }

    private TakeItem? ContextItem => TakesList.SelectedItem as TakeItem;

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (ContextItem is not { } item) return;
        var group = _takes.Where(t => t.Group.Key == item.Group.Key).Select(t => t.Session).ToList();
        var dlg = new RenameDialog(item.Session.SongTitle, group.Count) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        foreach (var s in dlg.ApplyToGroup ? group : [item.Session])
        {
            s.SongTitle = dlg.Value.Trim();
            s.Save();
        }
        RefreshLibrary();
        if (_editor != null && TakesList.SelectedItem is TakeItem cur) SyncEditorHeader(_editor.Session, cur.TakeNumber);
    }

    private void OnToggleStar(object sender, RoutedEventArgs e)
    {
        if (ContextItem is not { } item) return;
        item.Session.Starred = !item.Session.Starred;
        item.Session.Save();
        bool wasOpen = _editor?.Session.Id == item.Session.Id;
        if (wasOpen) SyncEditorHeader(item.Session, item.TakeNumber);
        OnEditorSessionChanged(item.Session, false);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (ContextItem is not { } item) return;
        Process.Start("explorer.exe", $"\"{item.Session.Folder}\"");
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ContextItem is not { } item) return;
        if (MessageBox.Show(this, $"删除「{item.Session.SongTitle}」Take {item.TakeNumber}？（移到回收站）",
                "删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool wasOpen = _editor?.Session.Id == item.Session.Id;
        if (wasOpen) CloseEditor(restoreCapture: false);
        try
        {
            var s = item.Session;
            bool otherTakes = Directory.EnumerateFiles(s.Folder, "*.json")
                .Any(f => SessionInfo.IsTakeFile(Path.GetFileName(f)) && !Path.GetFileName(f).Equals(s.JsonFile, StringComparison.OrdinalIgnoreCase));
            var recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
            var ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
            if (otherTakes)
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(s.PathOf(s.JsonFile), ui, recycle);
                if (File.Exists(s.PathOf(s.ThumbFile))) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(s.PathOf(s.ThumbFile), ui, recycle);
            }
            else Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(s.Folder, ui, recycle);
        }
        catch (Exception ex)
        {
            Log.Error("delete", ex);
            MessageBox.Show(this, "删除失败：" + ex.Message);
        }
        RefreshLibrary();
        if (wasOpen) ShowCaptureMode();
    }

    // ---------------- Keyboard / lifecycle ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor == null || EditorHostBorder.Visibility != Visibility.Visible) return;
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox || Keyboard.FocusedElement is ComboBoxItem) return;
        if (_editor.HandleKey(e)) e.Handled = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_controller.IsRecording)
        {
            if (MessageBox.Show(this, "正在录制，确定退出？本次录音会被丢弃。", "退出", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            Native.SetExcludeFromCapture(this, false);
            _controller.DiscardAsync().GetAwaiter().GetResult();
        }
        if (!IsEditorOpen && WindowState == WindowState.Normal)
            _settings.CaptureWindowBounds = Native.GetPhysicalBounds(this);
        else if (_savedCaptureBounds is { } cb)
            _settings.CaptureWindowBounds = cb;
        CloseEditor(restoreCapture: false);
        _controller.Dispose();
        _settings.Save();
    }
}
