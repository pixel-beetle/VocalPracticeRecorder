using System.Diagnostics;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Tests;

public class EditTimelineTests
{
    private static SessionInfo Session(double duration = 10, int fps = 20) => new()
    {
        Video = new VideoInfo { Fps = fps, FrameCount = (long)(duration * fps) },
    };

    [Fact]
    public void Cuts_merge_sort_and_snap_to_frames()
    {
        var cuts = EditTimeline.AddCut([], new TimeRange(5.03, 4.01), 20);  // reversed + off-grid
        Assert.Equal([new TimeRange(4.0, 5.05)], cuts);
        cuts = EditTimeline.AddCut(cuts, new TimeRange(1, 2), 20);
        cuts = EditTimeline.AddCut(cuts, new TimeRange(4.5, 6), 20);        // overlaps → merged
        Assert.Equal([new TimeRange(1, 2), new TimeRange(4.0, 6)], cuts);
        cuts = EditTimeline.AddCut(cuts, new TimeRange(2, 4), 20);          // adjacent → one cut
        Assert.Equal([new TimeRange(1, 6)], cuts);
        Assert.Empty(EditTimeline.RemoveCutAt(cuts, 3));
    }

    [Fact]
    public void Kept_segments_respect_trim_and_cuts()
    {
        var s = Session();
        s.Edit.TrimStart = 1;
        s.Edit.TrimEnd = 8;
        s.Edit.Cuts = [new(0.2, 1.5), new(3, 4), new(7.5, 9.5)];
        var segs = EditTimeline.KeptSegments(s);
        Assert.Equal([new TimeRange(1.5, 3), new TimeRange(4, 7.5)], segs);
        Assert.Equal(5.0, EditTimeline.OutputDuration(s), 6);
        Assert.Equal(1.5 + 0.5, EditTimeline.TimelineToOutput(s, 4.5)!.Value, 6);
        Assert.Null(EditTimeline.TimelineToOutput(s, 3.5));
    }

    /// <summary>Invariant over random edits: segments are ordered, disjoint, frame-aligned, inside the trim, outside every cut.</summary>
    [Fact]
    public void Random_edits_keep_segment_invariants()
    {
        var rnd = new Random(3);
        for (int k = 0; k < 500; k++)
        {
            var s = Session(30);
            s.Edit.TrimStart = rnd.NextDouble() * 5;
            s.Edit.TrimEnd = rnd.Next(2) == 0 ? 0 : 20 + rnd.NextDouble() * 10;
            var cuts = new List<TimeRange>();
            for (int c = 0; c < rnd.Next(0, 6); c++)
            {
                double a = rnd.NextDouble() * 30;
                cuts = EditTimeline.AddCut(cuts, new TimeRange(a, a + rnd.NextDouble() * 4), 20);
            }
            s.Edit.Cuts = cuts;
            var segs = EditTimeline.KeptSegments(s);
            double prevEnd = -1;
            foreach (var seg in segs)
            {
                Assert.True(seg.Start >= prevEnd - 1e-9 && seg.Length > 0);
                Assert.Equal(seg.Start * 20, Math.Round(seg.Start * 20), 6);
                Assert.Equal(seg.End * 20, Math.Round(seg.End * 20), 6);
                Assert.True(seg.Start >= EditTimeline.Snap(s.Edit.TrimStart, 20) - 1e-9 && seg.End <= s.EffectiveTrimEnd + 0.05);
                Assert.DoesNotContain(cuts, c => seg.Start < c.End - 1e-9 && seg.End > c.Start + 1e-9);
                prevEnd = seg.End;
            }
        }
    }

    [Fact]
    public void Split_creates_second_take_sharing_media()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpr-split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var s = Session(60);
            s.Folder = dir;
            s.Id = Path.GetFileName(dir);
            s.CreatedAt = new DateTime(2026, 11, 24, 1, 0, 0);
            s.Edit.TrimStart = 2;
            s.Edit.Cuts = [new(10, 12), new(40, 41)];
            s.Starred = true;
            var b = s.SplitAt(30.02);
            Assert.Equal(30.0, s.Edit.TrimEnd);
            Assert.Equal(30.0, b.Edit.TrimStart);
            Assert.Equal(0, b.Edit.TrimEnd);
            Assert.False(b.Starred);
            Assert.Equal("take-2.json", b.JsonFile);
            Assert.Equal(dir, b.Folder);
            Assert.True(b.CreatedAt > s.CreatedAt);
            // Each half exports only its own part; together they cover the original.
            Assert.Equal(28 - 2, EditTimeline.OutputDuration(s), 6);
            Assert.Equal(30 - 1, EditTimeline.OutputDuration(b), 6);
            s.Save(); b.Save();
            var loaded = Library.LoadAll(Path.GetDirectoryName(dir)!).Where(x => x.Folder == dir).ToList();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(2, Library.Group(loaded)[0].Takes.Count);
            Assert.Throws<InvalidOperationException>(() => s.SplitAt(29.8)); // too close to the end
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>
/// End-to-end: exported picture and sound must come from the same source instants across every join.
/// The test video encodes its frame number in its brightness, so each exported frame can be traced back.
/// </summary>
public class CutExportIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpr-cut-" + Guid.NewGuid().ToString("N"));
    public CutExportIntegrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const int Fps = 20;
    private const double LumaStep = 2.5; // brightness = (frame mod 90) * 2.5

    private SessionInfo MakeSession()
    {
        var video = Path.Combine(_dir, "video.mp4");
        Process.Start(Ffmpeg.StartInfo(["-hide_banner", "-y", "-f", "lavfi", "-i",
            $"nullsrc=s=64x64:r={Fps}:d=10,geq=lum='16+mod(N\\,90)*{LumaStep}':cb=128:cr=128",
            "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0", "-pix_fmt", "yuv420p", video]))!.WaitForExit();
        // Clicks every second on the backing; vocal clicks at 3.25 and 6.55 (source time).
        ExportIntegrationTests.WriteWav(Path.Combine(_dir, "backing.wav"), 2, 10,
            Enumerable.Range(1, 9).SelectMany(i => new[] { (0, i + 0.0), (1, i + 0.0) }).ToArray());
        ExportIntegrationTests.WriteWav(Path.Combine(_dir, "mic.wav"), 2, 10, (0, 3.25), (0, 6.55));
        return new SessionInfo
        {
            Folder = _dir,
            Video = new VideoInfo { File = "video.mp4", Fps = Fps, FrameCount = 200, StartOffset = 0 },
            Mic = new TrackInfo { File = "mic.wav", Channels = 2, StartOffset = 0 },
            Backing = new TrackInfo { File = "backing.wav", Channels = 2, StartOffset = 0 },
            Edit = new EditState { TrimStart = 0.5, TrimEnd = 9.0, VocalGainDb = 0, BackingGainDb = 0, MicChannel = MicChannel.Left },
        };
    }

    private async Task<List<double>> DecodeLuma(string file)
    {
        // extractplanes keeps raw Y values (format=gray would rescale TV range to full range).
        var psi = Ffmpeg.StartInfo(["-hide_banner", "-nostdin", "-i", file, "-map", "0:v", "-vf", "extractplanes=y", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"]);
        psi.StandardOutputEncoding = null;
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms);
        await p.WaitForExitAsync();
        var bytes = ms.ToArray();
        const int frameBytes = 64 * 64;
        var result = new List<double>();
        for (int f = 0; f + frameBytes <= bytes.Length; f += frameBytes)
        {
            double sum = 0;
            for (int i = f; i < f + frameBytes; i++) sum += bytes[i];
            result.Add(sum / frameBytes);
        }
        return result;
    }

    [Fact]
    public async Task Video_frames_and_audio_follow_the_cut_map()
    {
        if (!Ffmpeg.IsAvailable) return;
        var s = MakeSession();
        // Delete 3.0–4.0 (contains the first vocal click) and 6.0–6.25 (tiny cut before the second one).
        s.Edit.Cuts = EditTimeline.AddCut(EditTimeline.AddCut([], new TimeRange(3.0, 4.0), Fps), new TimeRange(6.0, 6.25), Fps);
        var segs = EditTimeline.KeptSegments(s);
        double outLen = EditTimeline.OutputDuration(s);
        Assert.Equal(8.5 - 1.25, outLen, 6);

        var outPath = Path.Combine(_dir, "out.mp4");
        await Ffmpeg.RunAsync(ExportBuilder.BuildArgs(s, ExportKind.MixVideo, outPath));

        // Picture: exactly the kept frames, in order.
        var luma = await DecodeLuma(outPath);
        Assert.Equal((int)Math.Round(outLen * Fps), luma.Count);
        int k = 0;
        foreach (var seg in segs)
        {
            for (long f = (long)Math.Round(seg.Start * Fps); f < (long)Math.Round(seg.End * Fps); f++, k++)
            {
                double expected = 16 + (f % 90) * LumaStep;
                Assert.True(Math.Abs(luma[k] - expected) < LumaStep / 2, $"out frame {k}: luma {luma[k]:0.0}, expected source frame {f} ({expected})");
            }
        }

        // Sound: backing clicks land where the cut map says; the deleted vocal click is gone.
        var pcm = await Ffmpeg.DecodeAudioAsync(outPath, 48000, 2);
        Assert.InRange(pcm.Length / 2 / 48000.0, outLen - 0.03, outLen + 0.03);
        foreach (double src in new[] { 2.0, 5.0, 7.0, 8.0 })
        {
            double expectedOut = EditTimeline.TimelineToOutput(s, src)!.Value;
            Assert.InRange(PeakTime(pcm, expectedOut - 0.2, expectedOut + 0.2), expectedOut - 0.003, expectedOut + 0.003);
        }
        double vocalOut = EditTimeline.TimelineToOutput(s, 6.55)!.Value;
        Assert.InRange(PeakTime(pcm, vocalOut - 0.2, vocalOut + 0.2), vocalOut - 0.003, vocalOut + 0.003);
        Assert.Null(EditTimeline.TimelineToOutput(s, 3.25));
    }

    private static double PeakTime(float[] stereo, double from, double to)
    {
        int a = (int)(from * 48000), b = Math.Min(stereo.Length / 2, (int)(to * 48000));
        int best = a; float max = 0;
        for (int i = a; i < b; i++)
        {
            float v = Math.Abs(stereo[i * 2]);
            if (v > max) { max = v; best = i; }
        }
        return best / 48000.0;
    }
}
