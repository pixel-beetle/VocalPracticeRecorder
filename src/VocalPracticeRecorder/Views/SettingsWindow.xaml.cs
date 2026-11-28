using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using VocalPracticeRecorder.Core;
using Path = System.IO.Path;

namespace VocalPracticeRecorder.Views;

public partial class SettingsWindow : Window
{
    private enum MoveChoice { Move, KeepOld, Cancel }

    private readonly AppSettings _settings;
    private bool _loading = true;
    private bool _syncingColors;
    private bool _busy;
    private TaskCompletionSource<MoveChoice>? _confirmTcs;

    /// <summary>True if the recordings location changed (library must be reloaded).</summary>
    public bool LibraryChanged { get; private set; }

    private static readonly int[] Heights = [480, 540, 720, 1080];
    private static readonly int[] FpsValues = [15, 20, 24, 30];
    private static readonly (int Crf, string Label)[] Crfs = [(30, "体积优先 (CRF 30)"), (28, "平衡 (CRF 28)"), (24, "清晰 (CRF 24)")];

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        HeightBox.ItemsSource = Heights.Select(h => $"{h}p").ToList();
        HeightBox.SelectedIndex = Math.Max(0, Array.IndexOf(Heights, settings.MaxHeight));
        FpsBox.ItemsSource = FpsValues.Select(f => $"{f} fps").ToList();
        FpsBox.SelectedIndex = Math.Max(0, Array.IndexOf(FpsValues, settings.Fps));
        CrfBox.ItemsSource = Crfs.Select(c => c.Label).ToList();
        CrfBox.SelectedIndex = Math.Max(0, Array.FindIndex(Crfs, c => c.Crf == settings.Export.VideoCrf));
        BuildColorPickers();
        _loading = false;
        Refresh();
        Closing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; return; }
            if (_confirmTcs != null)
            {
                // Esc while the inline confirmation is open dismisses the confirmation, not the whole dialog.
                ResolveConfirm(MoveChoice.Cancel);
                e.Cancel = true;
            }
        };
    }

    private void Refresh()
    {
        RootText.Text = _settings.Root;
        RootText.ToolTip = _settings.Root;
        ExportsText.Text = _settings.ExportsDir;
        ExportsText.ToolTip = _settings.ExportsDir;
        FollowButton.IsEnabled = _settings.ExportDirOverride != null;
        int count = Library.LoadAll(_settings.RecordingsDir).Count;
        long bytes = DataMover.DirectorySize(_settings.RecordingsDir);
        string free = "";
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.Root));
            if (root != null) free = $" · 剩余 {FormatSize(new DriveInfo(root).AvailableFreeSpace)}";
        }
        catch { }
        RootInfo.Text = $"{count} 遍 · {FormatSize(bytes)}{free}";
    }

    private static string FormatSize(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{b / 1024.0:0} KB";

    private string? PickFolder(string title, string initial)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (Directory.Exists(initial)) dlg.InitialDirectory = initial;
        bool? ok = dlg.ShowDialog(this);
        Log.Info($"settings pick '{title}': ok={ok} path='{dlg.FolderName}'");
        return ok == true && !string.IsNullOrWhiteSpace(dlg.FolderName) ? dlg.FolderName : null;
    }

    internal Task<bool> ShowConfirmPreviewForSnapshotAsync(string title, string detail)
    {
        ConfirmTitle.Text = title;
        ConfirmDetail.Text = detail;
        ConfirmOverlay.Visibility = Visibility.Visible;
        return Task.FromResult(true);
    }

    private Task<MoveChoice> AskMoveChoiceAsync(string title, string detail)
    {
        ConfirmTitle.Text = title;
        ConfirmDetail.Text = detail;
        ConfirmOverlay.Visibility = Visibility.Visible;
        _confirmTcs = new TaskCompletionSource<MoveChoice>();
        Activate();
        return _confirmTcs.Task;
    }

    private void ResolveConfirm(MoveChoice choice)
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        var tcs = _confirmTcs;
        _confirmTcs = null;
        tcs?.TrySetResult(choice);
    }

    private void OnConfirmMove(object sender, RoutedEventArgs e) => ResolveConfirm(MoveChoice.Move);
    private void OnConfirmKeep(object sender, RoutedEventArgs e) => ResolveConfirm(MoveChoice.KeepOld);
    private void OnConfirmCancel(object sender, RoutedEventArgs e) => ResolveConfirm(MoveChoice.Cancel);

    private async void OnChangeRoot(object sender, RoutedEventArgs e)
    {
        if (_busy || _confirmTcs != null) return;
        var picked = PickFolder("选择录音库位置", _settings.Root);
        if (picked == null) return;
        string newRoot = Path.GetFullPath(picked);
        string oldRec = _settings.RecordingsDir, newRec = Path.Combine(newRoot, "Recordings");
        if (DataMover.Overlaps(oldRec, newRec) && !string.Equals(Path.GetFullPath(oldRec), newRec, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "新位置不能放在旧录音库里面（反之亦然）。", "无法更改", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.Equals(Path.GetFullPath(oldRec), newRec, StringComparison.OrdinalIgnoreCase)) return;

        bool followExports = _settings.ExportDirOverride == null;
        string oldExp = _settings.ExportsDir, newExp = Path.Combine(newRoot, "Exports");
        int count = Library.LoadAll(oldRec).Count;
        bool move = false;
        if (count > 0)
        {
            long need = DataMover.DirectorySize(oldRec) + (followExports ? DataMover.DirectorySize(oldExp) : 0);
            var choice = await AskMoveChoiceAsync(
                $"把现有的 {count} 遍录音（{FormatSize(need)}）一起移动到新位置？",
                $"目标：{newRoot}\n「仅改位置」会把旧录音留在原处，不再显示在列表里。");
            Log.Info($"settings change-root choice={choice} -> {newRoot}");
            if (choice == MoveChoice.Cancel) return;
            move = choice == MoveChoice.Move;
            if (move && !HasSpace(oldRec, newRoot, need)) return;
        }

        if (move)
        {
            bool ok = await RunMoveAsync(async progress =>
            {
                var r1 = await Task.Run(() => DataMover.MoveChildren(oldRec, newRec, progress));
                var errors = r1.Errors.ToList();
                if (followExports && Directory.Exists(oldExp) && !DataMover.Overlaps(oldExp, newExp))
                {
                    var r2 = await Task.Run(() => DataMover.MoveChildren(oldExp, newExp, progress));
                    errors.AddRange(r2.Errors);
                    await Task.Run(() => DataMover.RewriteExportPaths(Library.LoadAll(newRec), oldExp, newExp));
                }
                return errors;
            });
            if (!ok) { Refresh(); return; }
        }
        _settings.DataRoot = string.Equals(newRoot, AppSettings.DefaultRoot, StringComparison.OrdinalIgnoreCase) ? null : newRoot;
        _settings.Save();
        Log.Info($"data root -> {_settings.Root} (moved={move})");
        LibraryChanged = true;
        Refresh();
    }

    private async void OnChangeExports(object sender, RoutedEventArgs e)
    {
        if (_busy || _confirmTcs != null) return;
        var picked = PickFolder("选择导出位置", _settings.ExportsDir);
        if (picked == null) return;
        await ChangeExportsAsync(Path.GetFullPath(picked));
    }

    private async void OnFollowRoot(object sender, RoutedEventArgs e)
    {
        if (_busy || _confirmTcs != null) return;
        await ChangeExportsAsync(Path.Combine(_settings.Root, "Exports"), follow: true);
    }

    private async Task ChangeExportsAsync(string newExp, bool follow = false)
    {
        string oldExp = _settings.ExportsDir;
        if (string.Equals(Path.GetFullPath(oldExp).TrimEnd('\\'), newExp.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            if (follow) { _settings.ExportDirOverride = null; _settings.Save(); Refresh(); }
            return;
        }
        if (DataMover.Overlaps(oldExp, newExp))
        {
            MessageBox.Show(this, "新位置不能放在旧导出文件夹里面（反之亦然）。", "无法更改", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        bool hasFiles = Directory.Exists(oldExp) && Directory.EnumerateFileSystemEntries(oldExp).Any();
        if (hasFiles)
        {
            var choice = await AskMoveChoiceAsync(
                "把已导出的文件一起移动到新位置？",
                $"目标：{newExp}");
            Log.Info($"settings change-exports choice={choice} -> {newExp}");
            if (choice == MoveChoice.Cancel) return;
            if (choice == MoveChoice.Move)
            {
                if (!HasSpace(oldExp, newExp, DataMover.DirectorySize(oldExp))) return;
                bool ok = await RunMoveAsync(async progress =>
                {
                    var r = await Task.Run(() => DataMover.MoveChildren(oldExp, newExp, progress));
                    await Task.Run(() => DataMover.RewriteExportPaths(Library.LoadAll(_settings.RecordingsDir), oldExp, newExp));
                    return r.Errors;
                });
                if (!ok) { Refresh(); return; }
            }
        }
        _settings.ExportDirOverride = follow ? null : newExp;
        _settings.Save();
        Log.Info($"exports dir -> {_settings.ExportsDir}");
        LibraryChanged = true;
        Refresh();
    }

    private bool HasSpace(string src, string dst, long need)
    {
        try
        {
            string? sr = Path.GetPathRoot(Path.GetFullPath(src)), dr = Path.GetPathRoot(Path.GetFullPath(dst));
            if (dr == null || string.Equals(sr, dr, StringComparison.OrdinalIgnoreCase)) return true;
            long free = new DriveInfo(dr).AvailableFreeSpace;
            if (free > need * 1.05) return true;
            MessageBox.Show(this, $"目标磁盘空间不够：需要 {FormatSize(need)}，剩余 {FormatSize(free)}。", "空间不足", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch { return true; }
    }

    private async Task<bool> RunMoveAsync(Func<IProgress<MoveProgress>, Task<List<string>>> work)
    {
        _busy = true;
        BusyOverlay.Visibility = Visibility.Visible;
        var progress = new Progress<MoveProgress>(p =>
        {
            BusyProgress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            BusyText.Text = p.Total == 0 ? "移动中…" : $"移动中 {p.Done}/{p.Total}  {p.Current}";
        });
        try
        {
            var errors = await work(progress);
            if (errors.Count > 0)
            {
                Log.Info("move errors: " + string.Join(" | ", errors));
                MessageBox.Show(this, $"有 {errors.Count} 项没能移动（已保留在原处）：\n\n" + string.Join("\n", errors.Take(8)),
                    "部分未移动", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("move", ex);
            MessageBox.Show(this, "移动失败：" + ex.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _busy = false;
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnOpenRoot(object sender, RoutedEventArgs e) => OpenFolder(_settings.Root);
    private void OnOpenExports(object sender, RoutedEventArgs e) => OpenFolder(_settings.ExportsDir);

    private static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", $"\"{dir}\"");
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (HeightBox.SelectedIndex >= 0) _settings.MaxHeight = Heights[HeightBox.SelectedIndex];
        if (FpsBox.SelectedIndex >= 0) _settings.Fps = FpsValues[FpsBox.SelectedIndex];
        if (CrfBox.SelectedIndex >= 0) _settings.Export.VideoCrf = Crfs[CrfBox.SelectedIndex].Crf;
        _settings.Save();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------------- Appearance Colors ----------------

    private void BuildColorPickers()
    {
        PopulateSwatches(AccentSwatches, ThemeManager.AccentPresets, hex => ApplyColor("accent", hex));
        PopulateSwatches(VocalSwatches, ThemeManager.VocalPresets, hex => ApplyColor("vocal", hex));
        PopulateSwatches(BackingSwatches, ThemeManager.BackingPresets, hex => ApplyColor("backing", hex));
        SyncColorUi();
    }

    private static void PopulateSwatches(Panel host, ColorPreset[] presets, Action<string> onPick)
    {
        host.Children.Clear();
        foreach (var p in presets)
        {
            if (!ThemeManager.TryParseHex(p.Hex, out var col)) continue;
            var outer = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                Padding = new Thickness(2), Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand, ToolTip = $"{p.Name} ({p.Hex})", Tag = p.Hex,
                Child = new Ellipse { Fill = new SolidColorBrush(col) },
            };
            outer.MouseLeftButtonDown += (_, _) => onPick(p.Hex);
            host.Children.Add(outer);
        }
    }

    private void ApplyColor(string which, string hex)
    {
        switch (which)
        {
            case "accent": _settings.AccentColorHex = hex; break;
            case "vocal": _settings.VocalColorHex = hex; break;
            case "backing": _settings.BackingColorHex = hex; break;
        }
        ThemeManager.Apply(_settings);
        _settings.Save();
        SyncColorUi();
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _syncingColors || sender is not TextBox tb || tb.Tag is not string which) return;
        if (!ThemeManager.TryParseHex(tb.Text, out var col)) return;
        string normalized = ThemeManager.ToHex(col);
        switch (which)
        {
            case "accent": _settings.AccentColorHex = normalized; break;
            case "vocal": _settings.VocalColorHex = normalized; break;
            case "backing": _settings.BackingColorHex = normalized; break;
        }
        ThemeManager.Apply(_settings);
        _settings.Save();
        SyncColorUi(skipBox: tb);
    }

    private void OnResetColors(object sender, RoutedEventArgs e)
    {
        _settings.AccentColorHex = ThemeManager.DefaultAccentHex;
        _settings.VocalColorHex = ThemeManager.DefaultVocalHex;
        _settings.BackingColorHex = ThemeManager.DefaultBackingHex;
        ThemeManager.Apply(_settings);
        _settings.Save();
        SyncColorUi();
    }

    private void SyncColorUi(TextBox? skipBox = null)
    {
        _syncingColors = true;
        try
        {
            string aHex = ThemeManager.ToHex(ThemeManager.CurrentAccent);
            string vHex = ThemeManager.ToHex(ThemeManager.CurrentVocal);
            string bHex = ThemeManager.ToHex(ThemeManager.CurrentBacking);

            if (skipBox != AccentHexBox) AccentHexBox.Text = aHex;
            if (skipBox != VocalHexBox) VocalHexBox.Text = vHex;
            if (skipBox != BackingHexBox) BackingHexBox.Text = bHex;

            AccentPreviewDot.Fill = new SolidColorBrush(ThemeManager.CurrentAccent);
            VocalPreviewDot.Fill = new SolidColorBrush(ThemeManager.CurrentVocal);
            BackingPreviewDot.Fill = new SolidColorBrush(ThemeManager.CurrentBacking);

            MarkActiveSwatch(AccentSwatches, aHex);
            MarkActiveSwatch(VocalSwatches, vHex);
            MarkActiveSwatch(BackingSwatches, bHex);
        }
        finally { _syncingColors = false; }
    }

    private static void MarkActiveSwatch(Panel host, string activeHex)
    {
        foreach (var child in host.Children)
        {
            if (child is Border b && b.Tag is string hex)
                b.BorderBrush = hex.Equals(activeHex, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent;
        }
    }
}
