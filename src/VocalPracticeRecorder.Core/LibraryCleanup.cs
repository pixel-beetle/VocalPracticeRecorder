namespace VocalPracticeRecorder.Core;

public sealed record CleanupPlan(
    List<SessionInfo> TakesToRemove,
    List<string> WholeFoldersToRemove,
    List<string> IndividualFilesToRemove,
    long ReclaimableBytes)
{
    public int TakeCount => TakesToRemove.Count;
    public bool IsEmpty => TakesToRemove.Count == 0;
}

/// <summary>
/// Safely plans cleanup of unstarred old takes without ever deleting starred takes or shared media
/// belonging to split takes kept in the same recording folder.
/// </summary>
public static class LibraryCleanup
{
    /// <summary>
    /// Computes which unstarred takes older than <paramref name="olderThanDays"/> days (0 = all unstarred)
    /// can be moved to the Recycle Bin, separating whole-folder removals from shared-folder take removals.
    /// </summary>
    public static CleanupPlan Analyze(
        string recordingsRoot,
        int olderThanDays,
        DateTime? nowUtcOrLocal = null,
        string? excludeSessionId = null)
    {
        var all = Library.LoadAll(recordingsRoot);
        var now = nowUtcOrLocal ?? DateTime.Now;
        var cutoff = olderThanDays <= 0 ? DateTime.MaxValue : now.AddDays(-olderThanDays);

        var takesToRemove = new List<SessionInfo>();
        var wholeFolders = new List<string>();
        var singleFiles = new List<string>();
        long bytes = 0;

        foreach (var byFolder in all.GroupBy(s => s.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var folderTakes = byFolder.ToList();
            var eligible = folderTakes
                .Where(s => !s.Starred
                            && s.CreatedAt <= cutoff
                            && (excludeSessionId == null || !s.Id.Equals(excludeSessionId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (eligible.Count == 0) continue;

            takesToRemove.AddRange(eligible);
            if (eligible.Count == folderTakes.Count)
            {
                // Every take in this folder is being removed -> whole folder can safely go to Recycle Bin.
                wholeFolders.Add(byFolder.Key);
                bytes += FolderSize(byFolder.Key);
            }
            else
            {
                // Another take in this folder (e.g. a starred split take) is kept -> remove ONLY this take's JSON & thumb!
                foreach (var s in eligible)
                {
                    AddIfExists(s.PathOf(s.JsonFile), singleFiles, ref bytes);
                    AddIfExists(s.PathOf(s.ThumbFile), singleFiles, ref bytes);
                }
            }
        }

        return new CleanupPlan(takesToRemove, wholeFolders, singleFiles, bytes);
    }

    public static long TotalLibraryBytes(string recordingsRoot)
    {
        if (!Directory.Exists(recordingsRoot)) return 0;
        return FolderSize(recordingsRoot);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB";
    }

    private static void AddIfExists(string path, List<string> list, ref long bytes)
    {
        if (!File.Exists(path)) return;
        list.Add(path);
        try { bytes += new FileInfo(path).Length; } catch { }
    }

    private static long FolderSize(string dir)
    {
        long sum = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { sum += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return sum;
    }
}
