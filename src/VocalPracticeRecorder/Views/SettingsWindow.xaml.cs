using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _loading = true;
    private bool _busy;

    /// <summary>True if the recordings location changed (library must be reloaded).</summary>
    public bool LibraryChanged { get; private set; }

    private static readonly int[] Heights = [480, 540, 720, 1080];
    private static readonly int[] FpsValues = [15, 20, 24, 30];
    private static readonly (int Crf, string Label)[] Crfs = [(30, "体积优先（CRF 30）"), (28, "平衡（CRF 28，默认）"), (24, "清晰（CRF 24）")];

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
        _loading = false;
        Refresh();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
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
            if (root != null) free = $" · 所在磁盘剩余 {FormatSize(new DriveInfo(root).AvailableFreeSpace)}";
        }
        catch { }
        RootInfo.Text = $"录音放在「{_settings.RecordingsDir}」：{count} 遍，占用 {FormatSize(bytes)}{free}";
    }

    private static string FormatSize(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{b / 1024.0:0} KB";

    private string? PickFolder(string title, string initial)
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(initial) ? initial : null };
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    private async void OnChangeRoot(object sender, RoutedEventArgs e)
    {
        var picked = PickFolder("选择录音库位置（会在里面建 Recordings / Exports 子文件夹）", _settings.Root);
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
            var answer = MessageBox.Show(this,
                $"把现有的 {count} 遍录音（{FormatSize(need)}）一起移动到新位置吗？\n\n" +
                "是：移动过去（推荐）\n否：只改位置，旧录音留在原处，不再显示在列表里\n取消：不更改",
                "移动录音", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            move = answer == MessageBoxResult.Yes;
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
        var picked = PickFolder("选择导出文件位置", _settings.ExportsDir);
        if (picked == null) return;
        await ChangeExportsAsync(Path.GetFullPath(picked));
    }

    private async void OnFollowRoot(object sender, RoutedEventArgs e) =>
        await ChangeExportsAsync(Path.Combine(_settings.Root, "Exports"), follow: true);

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
            var answer = MessageBox.Show(this, "把已经导出的文件也移动到新位置吗？\n\n是：移动\n否：只改位置\n取消：不更改",
                "移动导出文件", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
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
        LibraryChanged = true; // export paths inside sessions may have changed
        Refresh();
    }

    private bool HasSpace(string src, string dst, long need)
    {
        try
        {
            string? sr = Path.GetPathRoot(Path.GetFullPath(src)), dr = Path.GetPathRoot(Path.GetFullPath(dst));
            if (dr == null || string.Equals(sr, dr, StringComparison.OrdinalIgnoreCase)) return true; // same disk: rename only
            long free = new DriveInfo(dr).AvailableFreeSpace;
            if (free > need * 1.05) return true;
            MessageBox.Show(this, $"目标磁盘空间不够：需要 {FormatSize(need)}，只剩 {FormatSize(free)}。", "空间不足", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            BusyText.Text = p.Total == 0 ? "正在移动…" : $"正在移动 {p.Done}/{p.Total}  {p.Current}";
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
}
