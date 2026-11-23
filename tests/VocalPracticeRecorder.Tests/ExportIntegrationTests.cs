using System.Diagnostics;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Tests;

/// <summary>
/// End-to-end invariant: whatever trim/offset/placement is chosen, the exported file must contain
/// each track's content exactly where the timeline says, and last exactly trimEnd - trimStart.
/// Uses the real ffmpeg; skipped silently if ffmpeg is unavailable.
/// </summary>
public class ExportIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpr-test-" + Guid.NewGuid().ToString("N"));

    public ExportIntegrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    internal static void WriteWav(string path, int channels, double seconds, params (int Channel, double At)[] clicks)
    {
        const int rate = 48000;
        int frames = (int)(seconds * rate);
        var pcm = new short[frames * channels];
        foreach (var (ch, at) in clicks)
        {
            int start = (int)Math.Round(at * rate);
            for (int i = 0; i < 24; i++) pcm[(start + i) * channels + ch] = (short)(i < 12 ? 29000 : -29000);
        }
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);
    }

    private SessionInfo MakeSession()
    {
        var video = Path.Combine(_dir, "video.mp4");
        Process.Start(Ffmpeg.StartInfo(["-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=20:duration=10",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", video]))!.WaitForExit();
        // Mic: Scarlett-style stereo, mic only on the left input. Click at source t=3.0.
        WriteWav(Path.Combine(_dir, "mic.wav"), 2, 10, (0, 3.0));
        // Backing: click on both channels at source t=5.0.
        WriteWav(Path.Combine(_dir, "backing.wav"), 2, 10, (0, 5.0), (1, 5.0));
        return new SessionInfo
        {
            Folder = _dir,
            SongTitle = "テスト",
            Video = new VideoInfo { File = "video.mp4", StartOffset = 0.2, Fps = 20, FrameCount = 200, Width = 320, Height = 240 },
            Mic = new TrackInfo { File = "mic.wav", StartOffset = 0.5, SampleRate = 48000, Channels = 2 },
            Backing = new TrackInfo { File = "backing.wav", StartOffset = 0.1, SampleRate = 48000, Channels = 2 },
            Edit = new EditState { TrimStart = 1.0, TrimEnd = 8.0, VocalOffsetMs = -100, MicChannel = MicChannel.Left, VocalGainDb = 0, BackingGainDb = 0 },
        };
    }

    // Timeline: vocal click = 3.0 + (0.5-0.2) - 0.1 = 3.2 → output 2.2 ; backing click = 5.0 + (0.1-0.2) = 4.9 → output 3.9
    private const double VocalClickOut = 2.2, BackingClickOut = 3.9, Length = 7.0;

    private static double PeakTime(float[] interleaved, int channels, double from, double to, int channel = 0)
    {
        const int rate = 48000;
        int a = (int)(from * rate), b = Math.Min(interleaved.Length / channels, (int)(to * rate));
        int best = a; float max = 0;
        for (int i = a; i < b; i++)
        {
            float v = Math.Abs(interleaved[i * channels + channel]);
            if (v > max) { max = v; best = i; }
        }
        return (double)best / rate;
    }

    private async Task<float[]> ExportAndDecode(SessionInfo s, ExportKind kind, int channels)
    {
        var outPath = Path.Combine(_dir, "out" + kind + ExportBuilder.Extension(kind));
        await Ffmpeg.RunAsync(ExportBuilder.BuildArgs(s, kind, outPath));
        return await Ffmpeg.DecodeAudioAsync(outPath, 48000, channels);
    }

    [Fact]
    public async Task Mix_audio_places_both_tracks_on_the_timeline_and_has_exact_length()
    {
        if (!Ffmpeg.IsAvailable) return;
        var s = MakeSession();
        var pcm = await ExportAndDecode(s, ExportKind.MixAudio, 2);
        Assert.InRange(pcm.Length / 2 / 48000.0, Length - 0.03, Length + 0.03);
        // Vocal must be centred (both channels), not left-only.
        Assert.InRange(PeakTime(pcm, 2, 1.5, 3.0, 0), VocalClickOut - 0.003, VocalClickOut + 0.003);
        Assert.InRange(PeakTime(pcm, 2, 1.5, 3.0, 1), VocalClickOut - 0.003, VocalClickOut + 0.003);
        Assert.InRange(PeakTime(pcm, 2, 3.2, 5.0, 0), BackingClickOut - 0.003, BackingClickOut + 0.003);
    }

    [Fact]
    public async Task Dry_vocal_excludes_backing()
    {
        if (!Ffmpeg.IsAvailable) return;
        var s = MakeSession();
        var pcm = await ExportAndDecode(s, ExportKind.DryVocal, 1);
        Assert.InRange(PeakTime(pcm, 1, 1.5, 3.0), VocalClickOut - 0.003, VocalClickOut + 0.003);
        float backingRegionMax = 0;
        for (int i = (int)(3.7 * 48000); i < (int)(4.1 * 48000); i++) backingRegionMax = Math.Max(backingRegionMax, Math.Abs(pcm[i]));
        Assert.True(backingRegionMax < 0.01, $"backing leaked: {backingRegionMax}");
    }

    [Fact]
    public async Task Mix_video_keeps_audio_in_sync_with_trim()
    {
        if (!Ffmpeg.IsAvailable) return;
        var s = MakeSession();
        // Vocal placed before the trim start must be cropped, not shifted.
        s.Edit.TrimStart = 0.0;
        var pcm = await ExportAndDecode(s, ExportKind.MixVideo, 2);
        Assert.InRange(pcm.Length / 2 / 48000.0, 8.0 - 0.05, 8.0 + 0.05);
        Assert.InRange(PeakTime(pcm, 2, 2.5, 4.0), 3.2 - 0.003, 3.2 + 0.003);
        Assert.InRange(PeakTime(pcm, 2, 4.2, 6.0), 4.9 - 0.003, 4.9 + 0.003);
    }

    [Fact]
    public void Limiter_never_auto_levels()
    {
        var args = string.Join(" ", ExportBuilder.BuildArgs(MakeSessionStub(), ExportKind.MixAudio, "x.mp3"));
        Assert.Contains("level=false", args);
        Assert.Contains("normalize=0", args);
    }

    private SessionInfo MakeSessionStub() => new()
    {
        Folder = _dir,
        Video = new VideoInfo { Fps = 20, FrameCount = 200 },
        Mic = new TrackInfo { File = "mic.wav", Channels = 2 },
        Backing = new TrackInfo { File = "backing.wav", Channels = 2 },
    };
}
