namespace VocalPracticeRecorder.Core;

public sealed record MoveProgress(int Done, int Total, string Current);

public sealed record MoveResult(int Moved, int Skipped, List<string> Errors);

/// <summary>
/// Moves the app's data (recording folders, optionally exports) to a new root. Same-volume moves are
/// instant renames; cross-volume moves copy then delete, verifying sizes before removing the source.
/// </summary>
public static class DataMover
{
    public static long DirectorySize(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    /// <summary>True if <paramref name="a"/> and <paramref name="b"/> are the same folder or one contains the other.</summary>
    public static bool Overlaps(string a, string b)
    {
        string na = Normalize(a), nb = Normalize(b);
        return na.StartsWith(nb, StringComparison.OrdinalIgnoreCase) || nb.StartsWith(na, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string p) => Path.GetFullPath(p).TrimEnd('\\', '/') + "\\";

    /// <summary>Moves every child folder of <paramref name="srcDir"/> into <paramref name="dstDir"/>.</summary>
    public static MoveResult MoveChildren(string srcDir, string dstDir, IProgress<MoveProgress>? progress = null)
    {
        var errors = new List<string>();
        if (!Directory.Exists(srcDir)) return new MoveResult(0, 0, errors);
        if (Overlaps(srcDir, dstDir)) throw new InvalidOperationException("新位置不能和旧位置相同，也不能互相包含。");
        Directory.CreateDirectory(dstDir);
        var children = Directory.GetDirectories(srcDir);
        var files = Directory.GetFiles(srcDir);
        int total = children.Length + files.Length, done = 0, moved = 0, skipped = 0;
        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            progress?.Report(new MoveProgress(done, total, name));
            var target = UniqueTarget(Path.Combine(dstDir, name));
            try
            {
                MoveDirectory(child, target);
                moved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
                skipped++;
            }
            done++;
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            progress?.Report(new MoveProgress(done, total, name));
            try
            {
                var target = UniqueTarget(Path.Combine(dstDir, name));
                File.Copy(file, target);
                File.Delete(file);
                moved++;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
                skipped++;
            }
            done++;
        }
        progress?.Report(new MoveProgress(total, total, ""));
        try { if (!Directory.EnumerateFileSystemEntries(srcDir).Any()) Directory.Delete(srcDir); } catch { }
        return new MoveResult(moved, skipped, errors);
    }

    private static string UniqueTarget(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!Directory.Exists(p) && !File.Exists(p)) return p;
        }
    }

    public static void MoveDirectory(string src, string dst)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dst)), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(src, dst);
            return;
        }
        // Cross-volume: copy everything, verify, then delete the source.
        CopyDirectory(src, dst);
        if (DirectorySize(src) != DirectorySize(dst))
        {
            try { Directory.Delete(dst, true); } catch { }
            throw new IOException("复制后大小不一致，已保留原文件。");
        }
        Directory.Delete(src, true);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>Rewrites export paths stored in sessions after the export folder moved.</summary>
    public static int RewriteExportPaths(IEnumerable<SessionInfo> sessions, string oldExportDir, string newExportDir)
    {
        string oldN = Normalize(oldExportDir);
        int changed = 0;
        foreach (var s in sessions)
        {
            bool dirty = false;
            foreach (var e in s.Exports)
            {
                if (e.Path.StartsWith(oldN, StringComparison.OrdinalIgnoreCase))
                {
                    e.Path = Path.Combine(newExportDir, e.Path[oldN.Length..]);
                    dirty = true;
                }
            }
            if (dirty) { s.Save(); changed++; }
        }
        return changed;
    }
}
