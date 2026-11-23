using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VocalPracticeRecorder.Capture;
using VocalPracticeRecorder.Core;
using VocalPracticeRecorder.Views;

namespace VocalPracticeRecorder;

public sealed class TakeItem
{
    public required SessionInfo Session { get; init; }
    public required string Group { get; init; }
    public required int TakeNumber { get; init; }
    public string Header => $"Take {TakeNumber} · {Session.CreatedAt:MM/dd HH:mm}";
    public string StarMark => Session.Starred ? "  ★" : "";
    public string TagLine => string.Join(" · ", Library.Tags(Session));
    public BitmapImage? Thumbnail
    {
        get
        {
            var p = Session.PathOf("thumb.jpg");
            if (!File.Exists(p)) return null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad; // don't lock the file
                b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                b.DecodePixelWidth = 176;
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
    private readonly AppSettings _settings;
    private readonly RecordingController _controller = new();
    private readonly ObservableCollection<TakeItem> _takes = [];
    private readonly DispatcherTimer _meterTimer;
    private EditorView? _editor;
    private RecordingBar? _bar;
    private RegionFrameWindow? _frame;
    private float _micLevel, _backLevel;
    private bool _suppressSelection;
    private bool _devicesLoading;

    public MainWindow() : this(null) { }

    public MainWindow(AppSettings? settings)
    {
        _settings = settings ?? AppSettings.Load();
        InitializeComponent();
        var view = (CollectionViewSource)Resources["TakesView"];
        view.Source = _takes;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TakeItem.Group)));
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, OnMeterTick, Dispatcher);
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!Ffmpeg.IsAvailable)
        {
            MessageBox.Show(this, "找不到 ffmpeg.exe。\n请把 ffmpeg.exe 放到程序所在目录，或安装 FFmpeg 并加入 PATH。", "缺少 FFmpeg",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateRegionText();
        QualityText.Text = $"视频：最高 {_settings.MaxHeight}p · {_settings.Fps} fps（看清歌词足够，体积小）\n音频：录制时无损，导出为 AAC / MP3 320 kbps";
        LoadDevices();
        RefreshLibrary();
        _meterTimer.Start();
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
        DeviceError.Visibility = Visibility.Collapsed;
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
            MicFormatText.Text = f == null ? "" : $"{f.SampleRate / 1000.0:0.#} kHz · {f.Channels} 声道";
        }
        catch (Exception ex)
        {
            Log.Error("open devices", ex);
            ShowDeviceError("无法打开音频设备：" + ex.Message);
        }
    }

    private void ShowDeviceError(string text)
    {
        DeviceError.Text = text;
        DeviceError.Visibility = Visibility.Visible;
    }

    private void OnMeterTick(object? sender, EventArgs e)
    {
        if (RecordPanel.Visibility != Visibility.Visible || _controller.IsRecording) return;
        float mp = _controller.TakeMicPeak(), bp = _controller.TakeBackingPeak();
        _micLevel = mp > _micLevel ? mp : _micLevel * 0.85f;
        _backLevel = bp > _backLevel ? bp : _backLevel * 0.85f;
        MicMeter.Value = Meter.ToDisplay(_micLevel);
        BackMeter.Value = Meter.ToDisplay(_backLevel);
    }

    // ---------------- Region ----------------

    private void UpdateRegionText()
    {
        RegionText.Text = _settings.Region is { } r
            ? $"{r.Width} × {r.Height}（位置 {r.X}, {r.Y}）→ 输出 {RegionMath.OutputSize(r.Width, r.Height, _settings.MaxHeight).Width} × {RegionMath.OutputSize(r.Width, r.Height, _settings.MaxHeight).Height}"
            : "未选择";
    }

    private async Task<PixelRect?> SelectRegionAsync()
    {
        var prevState = WindowState;
        WindowState = WindowState.Minimized;
        await Task.Delay(250);
        var r = await RegionSelectorWindow.SelectAsync(_settings.Region);
        WindowState = prevState == WindowState.Minimized ? WindowState.Normal : prevState;
        Activate();
        if (r is { } region)
        {
            _settings.Region = region;
            _settings.Save();
            UpdateRegionText();
        }
        return r;
    }

    private async void OnSelectRegion(object sender, RoutedEventArgs e) => await SelectRegionAsync();

    // ---------------- Recording ----------------

    private void OnNewRecording(object sender, RoutedEventArgs e) => ShowRecordPanel();

    private void ShowRecordPanel()
    {
        CloseEditor();
        _suppressSelection = true;
        TakesList.SelectedItem = null;
        _suppressSelection = false;
        RecordPanel.Visibility = Visibility.Visible;
        if (!_controller.DevicesOpen) OpenDevices();
    }

    private async void OnStartRecording(object sender, RoutedEventArgs e)
    {
        if (!Ffmpeg.IsAvailable) { MessageBox.Show(this, "缺少 ffmpeg.exe，无法录制。"); return; }
        if (!_controller.DevicesOpen) OpenDevices();
        if (!_controller.DevicesOpen) return;

        var region = _settings.Region;
        if (region == null)
        {
            region = await SelectRegionAsync();
            if (region == null) return;
        }

        StartButton.IsEnabled = false;
        NewRecordingButton.IsEnabled = false;
        WindowState = WindowState.Minimized;
        await Task.Delay(350); // let the minimize animation finish before reading what's under the region
        var r = region.Value;
        string title = Native.TopLevelTitleAt(r.X + r.Width / 2, r.Y + r.Height / 2);
        try
        {
            var s = _controller.Start(r, title, _settings.RecordingsDir, _settings.Fps, _settings.MaxHeight);
            Log.Info($"recording {s.Id} region={r} title='{title}'");
        }
        catch (Exception ex)
        {
            Log.Error("start", ex);
            WindowState = WindowState.Normal;
            StartButton.IsEnabled = true;
            NewRecordingButton.IsEnabled = true;
            MessageBox.Show(this, "无法开始录制：" + ex.Message, "录制失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _frame = new RegionFrameWindow(r);
        _frame.Show();
        _bar = new RecordingBar(_controller, r);
        _bar.StopRequested += async () => await FinishRecordingAsync(discard: false);
        _bar.DiscardRequested += async () => await FinishRecordingAsync(discard: true);
        _bar.Show();
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
        _bar?.Close(); _bar = null;
        _frame?.Close(); _frame = null;
        WindowState = WindowState.Normal;
        Activate();
        StartButton.IsEnabled = true;
        NewRecordingButton.IsEnabled = true;

        if (error != null)
        {
            MessageBox.Show(this, "录制出错：" + error.Message, "录制失败", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLibrary();
            return;
        }
        if (s == null) return;

        SetBusy("正在保存音频…");
        try
        {
            await SessionPostProcessor.CompressRawAsync(s);
            s.Save();
            SetBusy("正在分析人声对齐…");
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

    // ---------------- Library ----------------

    private SessionAudio? _preloaded;

    private void RefreshLibrary(string? select = null, SessionAudio? preloaded = null)
    {
        string? keep = select ?? (TakesList.SelectedItem as TakeItem)?.Session.Id;
        var sessions = Library.LoadAll(_settings.RecordingsDir);
        // Reuse the live session object for the open editor so edits are not lost.
        if (_editor != null)
        {
            int i = sessions.FindIndex(x => x.Id == _editor.Session.Id);
            if (i >= 0) sessions[i] = _editor.Session;
        }
        var groups = Library.Group(sessions);
        _suppressSelection = true;
        _takes.Clear();
        TakeItem? toSelect = null;
        foreach (var g in groups)
        {
            for (int i = 0; i < g.Takes.Count; i++)
            {
                var item = new TakeItem { Session = g.Takes[i], Group = g.Title, TakeNumber = i + 1 };
                _takes.Add(item);
                if (item.Session.Id == keep) toSelect = item;
            }
        }
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

    private async void OnTakeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || TakesList.SelectedItem is not TakeItem item) return;
        if (_editor?.Session.Id == item.Session.Id && _preloaded == null) return;
        var s = item.Session;
        if (s.Incomplete)
        {
            MessageBox.Show(this, "这次录音没有正常结束（可能程序被意外关闭），无法编辑。可以右键删除。", "录音不完整");
            return;
        }
        CloseEditor();
        RecordPanel.Visibility = Visibility.Collapsed;
        var preloaded = _preloaded;
        _preloaded = null;
        SetBusy("正在加载…");
        try
        {
            var audio = preloaded ?? await SessionAudio.LoadAsync(s);
            if (TakesList.SelectedItem != item) return; // user moved on
            _editor = new EditorView(s, audio, _settings, item.TakeNumber);
            _editor.SessionChanged += OnEditorSessionChanged;
            EditorHost.Content = _editor;
            EditorHost.Visibility = Visibility.Visible;
            _editor.Focus();
        }
        catch (Exception ex)
        {
            Log.Error("open take", ex);
            MessageBox.Show(this, "无法打开录音：" + ex.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(null); }
    }

    private void OnEditorSessionChanged(SessionInfo s, bool regroup)
    {
        if (regroup) { RefreshLibrary(); return; }
        // Cheap refresh of the tag line for this item.
        var item = _takes.FirstOrDefault(t => t.Session.Id == s.Id);
        if (item == null) return;
        int idx = _takes.IndexOf(item);
        _suppressSelection = true;
        var replacement = new TakeItem { Session = s, Group = item.Group, TakeNumber = item.TakeNumber };
        _takes[idx] = replacement;
        TakesList.SelectedItem = replacement;
        _suppressSelection = false;
    }

    private void CloseEditor()
    {
        if (_editor == null) return;
        _editor.SessionChanged -= OnEditorSessionChanged;
        _editor.Shutdown();
        EditorHost.Content = null;
        EditorHost.Visibility = Visibility.Collapsed;
        _editor = null;
    }

    private TakeItem? ContextItem => TakesList.SelectedItem as TakeItem;

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (ContextItem is not { } item) return;
        var group = _takes.Where(t => t.Group == item.Group).Select(t => t.Session).ToList();
        var dlg = new RenameDialog(item.Session.SongTitle, group.Count) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        foreach (var s in dlg.ApplyToGroup ? group : [item.Session])
        {
            s.SongTitle = dlg.Value.Trim();
            s.Save();
        }
        CloseEditor();
        RefreshLibrary(select: item.Session.Id);
    }

    private void OnToggleStar(object sender, RoutedEventArgs e)
    {
        if (ContextItem is not { } item) return;
        item.Session.Starred = !item.Session.Starred;
        item.Session.Save();
        bool wasOpen = _editor?.Session.Id == item.Session.Id;
        if (wasOpen) CloseEditor();
        RefreshLibrary(select: wasOpen ? item.Session.Id : null);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (ContextItem is not { } item) return;
        Process.Start("explorer.exe", $"\"{item.Session.Folder}\"");
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ContextItem is not { } item) return;
        if (MessageBox.Show(this, $"删除「{item.Session.SongTitle}」的 Take {item.TakeNumber}？\n（移到回收站，已导出的文件不受影响）",
                "删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_editor?.Session.Id == item.Session.Id) CloseEditor();
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Session.Folder,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            Log.Error("delete", ex);
            MessageBox.Show(this, "删除失败：" + ex.Message);
        }
        RefreshLibrary();
        if (_editor == null) ShowRecordPanel();
    }

    // ---------------- Keyboard / lifecycle ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor == null || EditorHost.Visibility != Visibility.Visible) return;
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
            _controller.DiscardAsync().GetAwaiter().GetResult();
        }
        _bar?.Close();
        _frame?.Close();
        CloseEditor();
        _controller.Dispose();
        _settings.Save();
    }
}
