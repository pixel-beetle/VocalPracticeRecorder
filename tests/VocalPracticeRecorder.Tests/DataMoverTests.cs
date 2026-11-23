using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Tests;

public class DataMoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vpr-move-" + Guid.NewGuid().ToString("N"));
    public DataMoverTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private SessionInfo MakeRecording(string recDir, string id, string exportDir)
    {
        var s = new SessionInfo
        {
            Id = id, Folder = Path.Combine(recDir, id), SongTitle = "テスト",
            Video = new VideoInfo { Fps = 20, FrameCount = 200 },
        };
        Directory.CreateDirectory(s.Folder);
        File.WriteAllBytes(s.PathOf("mic.flac"), new byte[1234]);
        s.Exports.Add(new ExportRecord { Path = Path.Combine(exportDir, id + ".mp4") });
        s.Save();
        var b = s.SplitAt(5);
        b.Save();
        s.Save();
        return s;
    }

    [Fact]
    public void Moves_recordings_with_all_takes_and_rewrites_export_paths()
    {
        string oldRec = Path.Combine(_root, "old", "Recordings"), oldExp = Path.Combine(_root, "old", "Exports");
        string newRec = Path.Combine(_root, "new", "Recordings"), newExp = Path.Combine(_root, "new", "Exports");
        MakeRecording(oldRec, "20261124_010000", oldExp);
        MakeRecording(oldRec, "20261124_020000", oldExp);
        Directory.CreateDirectory(oldExp);
        File.WriteAllText(Path.Combine(oldExp, "20261124_010000.mp4"), "x");

        var r = DataMover.MoveChildren(oldRec, newRec);
        Assert.Equal(2, r.Moved);
        Assert.Empty(r.Errors);
        DataMover.MoveChildren(oldExp, newExp);

        var moved = Library.LoadAll(newRec);
        Assert.Equal(4, moved.Count); // 2 recordings × 2 takes
        Assert.All(moved, s => Assert.True(File.Exists(s.PathOf("mic.flac"))));
        Assert.Empty(Library.LoadAll(oldRec));

        DataMover.RewriteExportPaths(moved, oldExp, newExp);
        var reloaded = Library.LoadAll(newRec).Where(s => s.Exports.Count > 0).ToList();
        Assert.NotEmpty(reloaded);
        Assert.All(reloaded, s => Assert.StartsWith(newExp, s.Exports[0].Path));
        Assert.True(File.Exists(Path.Combine(newExp, "20261124_010000.mp4")));
    }

    [Fact]
    public void Refuses_nested_or_identical_targets_and_never_overwrites()
    {
        string rec = Path.Combine(_root, "Recordings");
        MakeRecording(rec, "a", Path.Combine(_root, "Exports"));
        Assert.Throws<InvalidOperationException>(() => DataMover.MoveChildren(rec, Path.Combine(rec, "sub")));
        Assert.Throws<InvalidOperationException>(() => DataMover.MoveChildren(rec, rec + "\\"));

        string dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(Path.Combine(dst, "a")); // name clash
        var r = DataMover.MoveChildren(rec, dst);
        Assert.Equal(1, r.Moved);
        Assert.True(Directory.Exists(Path.Combine(dst, "a_2")));
    }
}
