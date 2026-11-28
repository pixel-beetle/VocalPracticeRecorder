using System.Windows;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Views;

public partial class CleanupWindow : Window
{
    private readonly string _recordingsDir;
    private readonly string? _activeSessionId;
    private CleanupPlan? _plan;

    public bool Cleaned { get; private set; }

    public CleanupWindow(string recordingsDir, string? activeSessionId)
    {
        InitializeComponent();
        _recordingsDir = recordingsDir;
        _activeSessionId = activeSessionId;
        TotalSizeText.Text = $"总占用 {LibraryCleanup.FormatBytes(LibraryCleanup.TotalLibraryBytes(recordingsDir))}";
        RefreshPlan();
    }

    private int SelectedDays =>
        Days30.IsChecked == true ? 30 :
        Days14.IsChecked == true ? 14 :
        Days7.IsChecked == true ? 7 : 0;

    private void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshPlan();
    }

    private void RefreshPlan()
    {
        _plan = LibraryCleanup.Analyze(_recordingsDir, SelectedDays, excludeSessionId: _activeSessionId);
        if (_plan.IsEmpty)
        {
            SummaryText.Text = "没有符合条件的未标星旧录音。";
            CleanButton.IsEnabled = false;
        }
        else
        {
            SummaryText.Text = $"将清理 {_plan.TakeCount} 遍未标星录音，释放约 {LibraryCleanup.FormatBytes(_plan.ReclaimableBytes)}。";
            CleanButton.IsEnabled = true;
        }
    }

    private void OnClean(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _plan.IsEmpty) return;
        CleanButton.IsEnabled = false;
        var recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
        var ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
        int errors = 0;
        foreach (var dir in _plan.WholeFoldersToRemove)
        {
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(dir, ui, recycle); }
            catch (Exception ex) { errors++; Log.Error("cleanup-dir", ex); }
        }
        foreach (var file in _plan.IndividualFilesToRemove)
        {
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file, ui, recycle); }
            catch (Exception ex) { errors++; Log.Error("cleanup-file", ex); }
        }
        Cleaned = true;
        Log.Info($"cleanup: takes={_plan.TakeCount} folders={_plan.WholeFoldersToRemove.Count} files={_plan.IndividualFilesToRemove.Count} bytes={_plan.ReclaimableBytes} errors={errors}");
        if (errors > 0)
            MessageBox.Show(this, $"清理完成，但有 {errors} 项被占用未能移动。", "清理结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
