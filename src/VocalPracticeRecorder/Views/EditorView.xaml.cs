using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using VocalPracticeRecorder.Core;
using VocalPracticeRecorder.Editing;

namespace VocalPracticeRecorder.Views;

public partial class EditorView : UserControl
{
    private readonly SessionInfo _s;
    private readonly SessionAudio _audio;
    private readonly AppSettings _settings;
    private readonly int _take;
    private readonly PlaybackEngine _engine;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly Stack<EditState> _undo = new();
    private bool _loading = true;
    private bool _videoPlaying;
    private int _syncCounter;
    private string? _lastExport;
    private CancellationTokenSource? _exportCts;

    /// <summary>Raised after title/star/edit changes are saved, so the library can refresh.</summary>
    public event Action<SessionInfo, bool>? SessionChanged;

    public SessionInfo Session => _s;

    public EditorView(SessionInfo s, SessionAudio audio, AppSettings settings, int takeNumber)
    {
        InitializeComponent();
        _s = s;
        _audio = audio;
        _settings = settings;
        _take = takeNumber;

        TitleBox.Text = s.SongTitle;
        TakeInfo.Text = $"Take {takeNumber} · {s.CreatedAt:yyyy/MM/dd HH:mm} · {Library.FormatDuration(s.Duration)}";
        UpdateStar();
        ChannelBox.SelectedIndex = (int)s.Edit.MicChannel;
        if (s.Mic.Channels < 2) ChannelBox.IsEnabled = false;
        VocalGain.Value = s.Edit.VocalGainDb;
        BackingGain.Value = s.Edit.BackingGainDb;
        BackingMute.IsChecked = s.Edit.BackingMuted;
        (settings.LastExportKind switch
        {
            ExportKind.MixAudio => KindMix,
            ExportKind.DryVocal => KindDry,
            _ => KindVideo,
        }).IsChecked = true;
        var last = s.Exports.LastOrDefault();
        if (last != null && File.Exists(last.Path)) ShowExported(last.Path);

        Timeline.Load(s, new Peaks(audio.MicMono(s.Edit.MicChannel)), new Peaks(audio.BackingMono));
        Timeline.SeekRequested += Seek;
        Timeline.EditStarting += BeginEdit;
        Timeline.TrimChanged += MarkDirty;

        _engine = new PlaybackEngine(s, audio);
        _engine.Ended += () => Dispatcher.BeginInvoke(OnEnded);
        if (_engine.OutputError != null) ExportText.Text = "⚠ 无法打开播放设备：" + _engine.OutputError;

        Video.MediaOpened += (_, _) => { Video.Pause(); Video.Position = TimeSpan.Zero; };
        Video.Source = new Uri(s.PathOf(s.Video.File));
        Video.Pause();

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnUiTick, Dispatcher);
        _uiTimer.Start();
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher);
        _saveTimer.Stop();

        Seek(s.Edit.TrimStart);
        UpdateTexts();
        _loading = false;
        Loaded += (_, _) => Focus();
    }

    // ---------- Playback ----------

    private void Seek(double t)
    {
        t = Math.Clamp(t, 0, _s.Duration);
        if (_engine.IsPlaying)
        {
            _engine.Play(t, _s.EffectiveTrimEnd);
        }
        else _engine.Seek(t);
        Video.Position = TimeSpan.FromSeconds(t);
        Timeline.Playhead = t;
        TimeText.Text = Library.FormatTimecode(t);
    }

    private void TogglePlay()
    {
        if (_engine.IsPlaying)
        {
            _engine.Pause();
            Video.Pause();
            _videoPlaying = false;
            Video.Position = TimeSpan.FromSeconds(_engine.Position);
            PlayButton.Content = "▶ 播放";
            return;
        }
        double from = Timeline.Playhead;
        if (from < _s.Edit.TrimStart || from >= _s.EffectiveTrimEnd - 0.05) from = _s.Edit.TrimStart;
        _engine.Play(from, _s.EffectiveTrimEnd);
        Video.Position = TimeSpan.FromSeconds(from);
        Video.Play();
        _videoPlaying = true;
        PlayButton.Content = "⏸ 暂停";
    }

    private void OnEnded()
    {
        Video.Pause();
        _videoPlaying = false;
        PlayButton.Content = "▶ 播放";
        Timeline.Playhead = _engine.Position;
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        if (!_engine.IsPlaying) return;
        double pos = _engine.Position;
        Timeline.Playhead = pos;
        Timeline.EnsureVisible(pos);
        TimeText.Text = Library.FormatTimecode(pos);
        if (_videoPlaying && ++_syncCounter % 15 == 0)
        {
            double drift = Video.Position.TotalSeconds - pos;
            if (Math.Abs(drift) > 0.12) Video.Position = TimeSpan.FromSeconds(pos);
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e) { TogglePlay(); Focus(); }

    // ---------- Editing ----------

    private void BeginEdit()
    {
        _undo.Push(_s.Edit.Clone());
        if (_undo.Count > 200)
        {
            var keep = _undo.Take(100).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _s.Edit = _undo.Pop();
        _loading = true;
        ChannelBox.SelectedIndex = (int)_s.Edit.MicChannel;
        VocalGain.Value = _s.Edit.VocalGainDb;
        BackingGain.Value = _s.Edit.BackingGainDb;
        BackingMute.IsChecked = _s.Edit.BackingMuted;
        _loading = false;
        Timeline.SetVocalPeaks(new Peaks(_audio.MicMono(_s.Edit.MicChannel)));
        MarkDirty();
    }

    private void MarkDirty()
    {
        UpdateTexts();
        Timeline.Refresh();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        try { _s.Save(); } catch (Exception ex) { Log.Error("save", ex); }
        SessionChanged?.Invoke(_s, false);
    }

    private void Nudge(double ms)
    {
        BeginEdit();
        _s.Edit.VocalOffsetMs = Math.Round(_s.Edit.VocalOffsetMs + ms);
        _s.Edit.AlignMode = AlignMode.Manual;
        // Headphone case: remember the hand-tuned value for this device pair.
        if (!_s.Edit.AutoReliable)
        {
            _settings.RememberedOffsets[_s.DeviceComboKey] = _s.Edit.VocalOffsetMs;
            _settings.Save();
        }
        MarkDirty();
    }

    private void OnNudge(object sender, RoutedEventArgs e)
    {
        Nudge(double.Parse((string)((Button)sender).Tag));
        Focus();
    }

    private void OnAutoAlign(object sender, RoutedEventArgs e)
    {
        BeginEdit();
        _settings.RememberedOffsets.TryGetValue(_s.DeviceComboKey, out var remembered);
        var est = SessionPostProcessor.AutoAlign(_s, _audio,
            _settings.RememberedOffsets.ContainsKey(_s.DeviceComboKey) ? remembered : null);
        Log.Info($"auto-align {_s.Id}: delay={est.DelaySeconds * 1000:0.0}ms z={est.Confidence:0.0}");
        MarkDirty();
        Focus();
    }

    private void OnTrySuggestion(object sender, RoutedEventArgs e)
    {
        if (_s.Edit.AutoDelayMs is not { } d) return;
        BeginEdit();
        _s.Edit.VocalOffsetMs = Math.Round(-d);
        _s.Edit.AlignMode = AlignMode.Manual; // deliberately not remembered: it is a speaker-mode guess
        MarkDirty();
        Focus();
    }

    private void SetIn()
    {
        BeginEdit();
        _s.Edit.TrimStart = Math.Min(Timeline.Playhead, _s.EffectiveTrimEnd - 0.5);
        if (_s.Edit.TrimStart < 0) _s.Edit.TrimStart = 0;
        MarkDirty();
    }

    private void SetOut()
    {
        BeginEdit();
        double end = Math.Max(Timeline.Playhead, _s.Edit.TrimStart + 0.5);
        _s.Edit.TrimEnd = end >= _s.Duration - 0.01 ? 0 : end;
        MarkDirty();
    }

    private void OnSetIn(object sender, RoutedEventArgs e) { SetIn(); Focus(); }
    private void OnSetOut(object sender, RoutedEventArgs e) { SetOut(); Focus(); }

    private void OnResetTrim(object sender, RoutedEventArgs e)
    {
        BeginEdit();
        _s.Edit.TrimStart = 0;
        _s.Edit.TrimEnd = 0;
        Timeline.ZoomToFit();
        MarkDirty();
        Focus();
    }

    private void OnSliderDragStarted(object sender, DragStartedEventArgs e) => BeginEdit();

    private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _s.Edit.VocalGainDb = VocalGain.Value;
        _s.Edit.BackingGainDb = BackingGain.Value;
        MarkDirty();
    }

    private void OnMuteChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        BeginEdit();
        _s.Edit.BackingMuted = BackingMute.IsChecked == true;
        MarkDirty();
    }

    private void OnChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ChannelBox.SelectedIndex < 0) return;
        BeginEdit();
        _s.Edit.MicChannel = (MicChannel)ChannelBox.SelectedIndex;
        Timeline.SetVocalPeaks(new Peaks(_audio.MicMono(_s.Edit.MicChannel)));
        MarkDirty();
    }

    private void UpdateTexts()
    {
        var e = _s.Edit;
        OffsetText.Text = $"{e.VocalOffsetMs:+0;−0;0} ms";
        VocalGainText.Text = $"{e.VocalGainDb:+0;−0;0} dB";
        BackingGainText.Text = $"{e.BackingGainDb:+0;−0;0} dB";
        BackingGain.IsEnabled = !e.BackingMuted;
        double len = _s.EffectiveTrimEnd - e.TrimStart;
        TrimText.Text = $"{Library.FormatTimecode(e.TrimStart)} → {Library.FormatTimecode(_s.EffectiveTrimEnd)}（{Library.FormatDuration(len)}）";
        string dir = e.VocalOffsetMs < 0 ? $"人声提前 {-e.VocalOffsetMs:0} ms" : e.VocalOffsetMs > 0 ? $"人声推后 {e.VocalOffsetMs:0} ms" : "未偏移";
        AlignText.Text = e.AlignMode switch
        {
            AlignMode.Auto => $"✓ 外放模式：在麦克风里检测到伴奏，已自动对齐（{dir}，{e.AutoConfidence:0}% 分析段一致）。",
            AlignMode.Remembered => $"未检测到伴奏回声（可能戴了耳机），已沿用这组设备上次手调的值（{dir}）。边听边用 ← → 微调。",
            AlignMode.Manual => $"手动调整：{dir}。" + (e.AutoReliable
                ? $"（自动检测值 {-(e.AutoDelayMs ?? 0):+0;−0;0} ms）" : "调好的值会记住，下次用同样设备时自动套用。"),
            _ => "未检测到伴奏回声（可能戴了耳机）。边听边用 ← → 微调，调好的值会记住给下次用。",
        };
        bool suggest = !e.AutoReliable && e.AutoDelayMs is { } ad && Math.Abs(e.VocalOffsetMs + Math.Round(ad)) > 1
                       && Math.Abs(ad) >= 5;
        SuggestButton.Visibility = suggest ? Visibility.Visible : Visibility.Collapsed;
        if (suggest) SuggestButton.Content = $"试用检测值 {-e.AutoDelayMs!.Value:+0;−0;0} ms";
        if (suggest && e.AlignMode is AlignMode.None or AlignMode.Remembered)
            AlignText.Text = "找到一个可能的延迟，但把握不大（伴奏声小或戴了耳机）。可以点「试用检测值」试听，不对就 Ctrl+Z；也可以用 ← → 微调。";
    }

    // ---------- Header ----------

    private void OnTitleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTitle(); Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { TitleBox.Text = _s.SongTitle; Focus(); e.Handled = true; }
    }

    private void OnTitleCommitted(object sender, RoutedEventArgs e) => CommitTitle();

    private void CommitTitle()
    {
        var t = TitleBox.Text.Trim();
        if (t.Length == 0 || t == _s.SongTitle) { TitleBox.Text = _s.SongTitle; return; }
        _s.SongTitle = t;
        _s.Save();
        SessionChanged?.Invoke(_s, true);
    }

    private void UpdateStar()
    {
        StarButton.IsChecked = _s.Starred;
        StarButton.Content = _s.Starred ? "★ 最佳" : "☆ 标为最佳";
    }

    private void OnStar(object sender, RoutedEventArgs e)
    {
        _s.Starred = !_s.Starred;
        UpdateStar();
        _s.Save();
        SessionChanged?.Invoke(_s, true);
    }

    // ---------- Export ----------

    private ExportKind SelectedKind => KindMix.IsChecked == true ? ExportKind.MixAudio
        : KindDry.IsChecked == true ? ExportKind.DryVocal : ExportKind.MixVideo;

    private async void OnExport(object sender, RoutedEventArgs e) => await ExportAsync();

    private async Task ExportAsync()
    {
        if (_exportCts != null) return;
        if (_engine.IsPlaying) TogglePlay();
        SaveNow();
        var kind = SelectedKind;
        _settings.LastExportKind = kind;
        _settings.Save();
        Directory.CreateDirectory(_settings.ExportsDir);
        var path = Path.Combine(_settings.ExportsDir, ExportBuilder.DefaultFileName(_s, _take, kind));
        double length = _s.EffectiveTrimEnd - _s.Edit.TrimStart;

        ExportButton.IsEnabled = false;
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ExportText.Text = $"正在导出 {ExportBuilder.KindLabel(kind)}…";
        _exportCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        try
        {
            var args = ExportBuilder.BuildArgs(_s, kind, path, _settings.Export);
            Log.Info("export: " + string.Join(" ", args));
            await Task.Run(() => Ffmpeg.RunAsync(args,
                sec => Dispatcher.BeginInvoke(() => ExportProgress.Value = Math.Clamp(sec / length, 0, 1)), _exportCts.Token));
            _s.Exports.Add(new ExportRecord { Path = path, Kind = kind, At = DateTime.Now });
            _s.Save();
            ShowExported(path, sw.Elapsed.TotalSeconds);
            SessionChanged?.Invoke(_s, false);
        }
        catch (Exception ex)
        {
            Log.Error("export", ex);
            ExportText.Text = "导出失败：" + ex.Message.Split('\n')[0];
            MessageBox.Show(Window.GetWindow(this), ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExportButton.IsEnabled = true;
            ExportProgress.Visibility = Visibility.Collapsed;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    private void ShowExported(string path, double? seconds = null)
    {
        _lastExport = path;
        double mb = new FileInfo(path).Length / 1024.0 / 1024.0;
        ExportText.Text = $"✓ {Path.GetFileName(path)}（{mb:0.0} MB）" + (seconds.HasValue ? $" · 用时 {seconds:0.0}s" : "");
        ExportText.ToolTip = path;
        RevealButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
    }

    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (_lastExport == null) return;
        Process.Start("explorer.exe", $"/select,\"{_lastExport}\"");
    }

    private void OnCopyFile(object sender, RoutedEventArgs e)
    {
        if (_lastExport == null) return;
        Clipboard.SetFileDropList(new StringCollection { _lastExport });
        ExportText.Text = "已复制，可以到微信里粘贴发送（Ctrl+V）";
    }

    // ---------- Keyboard ----------

    /// <summary>Editor shortcuts; returns true if handled.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); return true;
            case Key.Left: Nudge(shift ? -50 : -10); return true;
            case Key.Right: Nudge(shift ? 50 : 10); return true;
            case Key.I: SetIn(); return true;
            case Key.O: SetOut(); return true;
            case Key.Home: Seek(_s.Edit.TrimStart); return true;
            case Key.End: Seek(Math.Max(_s.Edit.TrimStart, _s.EffectiveTrimEnd - 3)); return true;
            case Key.Z when ctrl: Undo(); return true;
            case Key.E when ctrl: _ = ExportAsync(); return true;
        }
        return false;
    }

    public void Shutdown()
    {
        _uiTimer.Stop();
        if (_saveTimer.IsEnabled) SaveNow();
        _exportCts?.Cancel();
        _engine.Dispose();
        Video.Stop();
        Video.Close();
        Video.Source = null;
    }
}
