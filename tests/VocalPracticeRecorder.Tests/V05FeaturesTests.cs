using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Tests;

public class V05FeaturesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vpr-v05-" + Guid.NewGuid().ToString("N"));

    public V05FeaturesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static float[] Sine(int sr, double sec, double hz, float amp = 0.25f)
    {
        int n = (int)(sr * sec);
        var buf = new float[n];
        for (int i = 0; i < n; i++) buf[i] = amp * MathF.Sin((float)(2 * Math.PI * hz * i / sr));
        return buf;
    }

    private static double Rms(float[] buf, int from, int count)
    {
        double sum = 0;
        int end = Math.Min(buf.Length, from + count);
        for (int i = from; i < end; i++) sum += buf[i] * (double)buf[i];
        return Math.Sqrt(sum / Math.Max(1, end - from));
    }

    [Fact]
    public void VocalPolish_RemovesLowRumble_AddsSubtleStereoTail_AndNeverClips()
    {
        const int sr = 48000;
        // 1. 35 Hz plosive rumble should be attenuated well below a 440 Hz vocal tone.
        var rumble = Sine(sr, 0.5, 35.0, 0.3f);
        var vocal = Sine(sr, 0.5, 440.0, 0.3f);
        var pRumble = VocalPolish.ProcessMono(rumble, sr);
        var pVocal = VocalPolish.ProcessMono(vocal, sr);
        double rumbleRatio = Rms(pRumble, sr / 5, sr / 5) / Rms(pVocal, sr / 5, sr / 5);
        Assert.True(rumbleRatio < 0.25, $"Expected 35 Hz rumble strongly attenuated vs 440 Hz, ratio={rumbleRatio:0.00}");

        // 2. Short vocal burst followed by silence -> subtle stereo reverb tail (L != R, never loud washout).
        var burst = new float[sr]; // 1.0 s total, tone in first 0.2 s
        var tone = Sine(sr, 0.2, 440.0, 0.35f);
        Array.Copy(tone, burst, tone.Length);
        var stereo = VocalPolish.ProcessStereo(burst, sr);

        double tailL = 0, tailR = 0, diffLR = 0;
        int tailStart = (int)(0.30 * sr), tailEnd = (int)(0.50 * sr);
        for (int f = tailStart; f < tailEnd; f++)
        {
            float l = stereo[f * 2], r = stereo[f * 2 + 1];
            tailL += l * l;
            tailR += r * r;
            diffLR += (l - r) * (l - r);
        }
        double tailRms = Math.Sqrt(tailL / (tailEnd - tailStart));
        Assert.True(tailRms > 0.001 && tailRms < 0.06, $"Expected subtle natural reverb tail, got rms={tailRms:0.0000}");
        Assert.True(diffLR > 1e-6, "Expected true stereo decorrelated reverb tail (L != R)");

        // 3. Hot 0 dBFS input must never clip past 0.99.
        var hot = Sine(sr, 0.3, 440.0, 1.0f);
        var pHot = VocalPolish.ProcessStereo(hot, sr);
        Assert.All(pHot, s => Assert.InRange(s, -0.991f, 0.991f));
    }

    [Fact]
    public void VocalPitchTrack_AnalyzeAndBinaryCacheRoundTrip()
    {
        const int sr = 48000;
        var mono = new float[sr * 2]; // 0..1s C4 (261.63 Hz), 1..2s G4 (392.00 Hz)
        var c4 = Sine(sr, 1.0, 261.63, 0.25f);
        var g4 = Sine(sr, 1.0, 392.00, 0.25f);
        Array.Copy(c4, 0, mono, 0, c4.Length);
        Array.Copy(g4, 0, mono, c4.Length, g4.Length);

        var s = new SessionInfo { Folder = _dir };
        var track = VocalPitchTrack.LoadOrAnalyze(s, mono, sr, MicChannel.Left);
        Assert.True(File.Exists(VocalPitchTrack.CachePath(s, MicChannel.Left)));

        Assert.Equal("C4", track.ReadingAtTimeline(0.50, 0).NoteName);
        Assert.Equal("G4", track.ReadingAtTimeline(1.50, 0).NoteName);
        // Shifting VocalPlacement by +0.2s shifts timeline lookup accordingly without re-analyzing.
        Assert.Equal("C4", track.ReadingAtTimeline(0.70, 0.20).NoteName);

        var reloaded = VocalPitchTrack.TryLoad(VocalPitchTrack.CachePath(s, MicChannel.Left), MicChannel.Left);
        Assert.NotNull(reloaded);
        Assert.Equal(track.Midi.Length, reloaded!.Midi.Length);
        Assert.Equal(track.Midi[25], reloaded.Midi[25], 4);
    }

    [Fact]
    public async Task Export_WithPolishAndBottomPitchStrip_ProducesExactStackedHeightAndColoredStrip()
    {
        if (!Ffmpeg.IsAvailable) return;

        const int sr = 48000;
        var video = Path.Combine(_dir, "video.mp4");
        Process.Start(Ffmpeg.StartInfo(["-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=20:duration=4",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", video]))!.WaitForExit();

        var micMono = Sine(sr, 4.0, 440.0, 0.22f); // steady A4
        var backStereo = new float[sr * 4 * 2];
        VocalPolish.WriteWavFloat32(Path.Combine(_dir, "mic.wav"), micMono, sr, 1);
        VocalPolish.WriteWavFloat32(Path.Combine(_dir, "backing.wav"), backStereo, sr, 2);

        var s = new SessionInfo
        {
            Folder = _dir,
            SongTitle = "PitchBurnTest",
            Video = new VideoInfo { File = "video.mp4", Fps = 20, FrameCount = 80, Width = 320, Height = 240 },
            Mic = new TrackInfo { File = "mic.wav", SampleRate = sr, Channels = 1 },
            Backing = new TrackInfo { File = "backing.wav", SampleRate = sr, Channels = 2 },
            Edit = new EditState
            {
                TrimStart = 0.5,
                TrimEnd = 3.5,
                VocalPolish = true,
                MicChannel = MicChannel.Left,
                Cuts = [new TimeRange(1.5, 2.0)], // kept = 2.5s (50 frames)
            },
        };

        var polishedPath = Path.Combine(_dir, "polished.wav");
        VocalPolish.WriteWavFloat32(polishedPath, VocalPolish.ProcessStereo(micMono, sr), sr, 2);

        var track = VocalPitchTrack.Analyze(micMono, sr, MicChannel.Left);
        int stripH = PitchStripRenderer.StripHeightFor(240);
        Assert.Equal(96, stripH);

        var renderer = new PitchStripRenderer(s, track, 320, stripH);
        Assert.Equal(50, renderer.TotalFrames);

        // Verify rendered BGRA strip actually contains vibrant green (in-tune A4) pixels in the plot area!
        var midFrame = renderer.RenderFrame(25);
        int greenPx = 0;
        for (int i = 0; i < midFrame.Length; i += 4)
            if (midFrame[i + 1] > 180 && midFrame[i + 2] < 100) greenPx++;
        Assert.True(greenPx > 50, $"Expected visible green pitch curve pixels in bottom strip, got {greenPx}");

        var outMp4 = Path.Combine(_dir, "out-pitch.mp4");
        var args = ExportBuilder.BuildArgs(s, ExportKind.MixVideo, outMp4, new ExportSettings { VideoPreset = "ultrafast" },
            polishedVocalPath: polishedPath, pitchStrip: new PitchStripSpec(renderer.Width, renderer.Height, renderer.Fps));

        await Ffmpeg.RunAsync(args, ct: CancellationToken.None, writeStdin: async (stdin, ct) =>
        {
            for (int f = 0; f < renderer.TotalFrames; f++)
            {
                var raw = renderer.RenderFrame(f);
                await stdin.WriteAsync(raw, 0, raw.Length, ct);
            }
        });

        Assert.True(File.Exists(outMp4) && new FileInfo(outMp4).Length > 5000);

        // Probe exact output video dimensions: must be 320x336 (240 video + 96 dedicated bottom strip).
        using var probe = Process.Start(Ffmpeg.StartInfo(["-hide_banner", "-i", outMp4]))!;
        string info = await probe.StandardError.ReadToEndAsync();
        await probe.WaitForExitAsync();
        Assert.Contains("320x336", info);
    }

    [Fact]
    public async Task LanFileShareServer_ServesLandingPage_FullDownload_RangeRequests_AndRejectsInvalidToken()
    {
        var file = Path.Combine(_dir, "练唱测试 Take1.mp4");
        var payload = new byte[128 * 1024];
        new Random(123).NextBytes(payload);
        await File.WriteAllBytesAsync(file, payload);

        using var server = new LanFileShareServer(file);
        using var http = new HttpClient();

        // 1. Mobile landing page
        var pageResp = await http.GetAsync(server.LandingUrl("127.0.0.1"));
        Assert.Equal(HttpStatusCode.OK, pageResp.StatusCode);
        string html = await pageResp.Content.ReadAsStringAsync();
        Assert.Contains("下载到手机", html);
        Assert.Contains($"/dl/{server.Token}/", html);

        // 2. HEAD check
        using var headReq = new HttpRequestMessage(HttpMethod.Head, server.DownloadUrl("127.0.0.1"));
        var headResp = await http.SendAsync(headReq);
        Assert.Equal(HttpStatusCode.OK, headResp.StatusCode);
        Assert.Equal(payload.Length, headResp.Content.Headers.ContentLength);

        // 3. Partial Range request (206)
        using var rangeReq = new HttpRequestMessage(HttpMethod.Get, server.DownloadUrl("127.0.0.1"));
        rangeReq.Headers.Range = new RangeHeaderValue(100, 199);
        var rangeResp = await http.SendAsync(rangeReq);
        Assert.Equal(HttpStatusCode.PartialContent, rangeResp.StatusCode);
        var slice = await rangeResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, slice.Length);
        Assert.Equal(payload[100..200], slice);

        // 4. Full attachment download increments DownloadCount
        var dlResp = await http.GetAsync(server.DownloadUrl("127.0.0.1"));
        Assert.Equal(HttpStatusCode.OK, dlResp.StatusCode);
        Assert.Equal("attachment", dlResp.Content.Headers.ContentDisposition?.DispositionType);
        var downloaded = await dlResp.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, downloaded);
        Assert.Equal(1, server.DownloadCount);

        // 5. Invalid token -> 404
        var bad = await http.GetAsync($"http://127.0.0.1:{server.Port}/dl/wrongtoken");
        Assert.Equal(HttpStatusCode.NotFound, bad.StatusCode);
    }

    [Fact]
    public void LibraryCleanup_PreservesStarredAndSharedSplitMedia_AndGroupSpanFormatsDates()
    {
        var recRoot = Path.Combine(_dir, "Recordings");
        var now = new DateTime(2026, 11, 28, 21, 0, 0);

        // Folder 1: old unstarred single take -> whole folder removed
        var f1 = Path.Combine(recRoot, "old_unstarred");
        Directory.CreateDirectory(f1);
        File.WriteAllBytes(Path.Combine(f1, "video.mp4"), new byte[10000]);
        new SessionInfo { Id = "old_unstarred", Folder = f1, SongTitle = "SongA", CreatedAt = now.AddDays(-20), Starred = false }.Save();

        // Folder 2: old starred single take -> completely preserved
        var f2 = Path.Combine(recRoot, "old_starred");
        Directory.CreateDirectory(f2);
        File.WriteAllBytes(Path.Combine(f2, "video.mp4"), new byte[10000]);
        new SessionInfo { Id = "old_starred", Folder = f2, SongTitle = "SongA", CreatedAt = now.AddDays(-25), Starred = true }.Save();

        // Folder 3: split takes sharing one folder (Take 1 old unstarred + Take 2 starred) -> ONLY Take 1 json removed!
        var f3 = Path.Combine(recRoot, "split_mixed");
        Directory.CreateDirectory(f3);
        File.WriteAllBytes(Path.Combine(f3, "video.mp4"), new byte[20000]);
        new SessionInfo { Id = "split_mixed", Folder = f3, JsonFile = "session.json", SongTitle = "SongA", CreatedAt = now.AddDays(-20), Starred = false }.Save();
        new SessionInfo { Id = "split_mixed-2", Folder = f3, JsonFile = "take-2.json", SongTitle = "SongA", CreatedAt = now.AddDays(-20), Starred = true }.Save();

        // Folder 4: recent unstarred take -> preserved when olderThanDays = 14
        var f4 = Path.Combine(recRoot, "recent_unstarred");
        Directory.CreateDirectory(f4);
        File.WriteAllBytes(Path.Combine(f4, "video.mp4"), new byte[10000]);
        new SessionInfo { Id = "recent_unstarred", Folder = f4, SongTitle = "SongA", CreatedAt = now.AddDays(-3), Starred = false }.Save();

        var plan = LibraryCleanup.Analyze(recRoot, olderThanDays: 14, nowUtcOrLocal: now);
        Assert.Equal(2, plan.TakeCount);
        Assert.Single(plan.WholeFoldersToRemove);
        Assert.Equal(f1, plan.WholeFoldersToRemove[0]);
        Assert.Single(plan.IndividualFilesToRemove);
        Assert.Equal(Path.Combine(f3, "session.json"), plan.IndividualFilesToRemove[0]);
        Assert.True(plan.ReclaimableBytes >= 10000);

        // Excluded active session is never touched
        var planExcl = LibraryCleanup.Analyze(recRoot, olderThanDays: 14, nowUtcOrLocal: now, excludeSessionId: "old_unstarred");
        Assert.Equal(1, planExcl.TakeCount);
        Assert.Empty(planExcl.WholeFoldersToRemove);

        // Group span badge check
        var gDay = new SongGroup { Takes = [new() { CreatedAt = now.AddDays(-27) }, new() { CreatedAt = now }] };
        Assert.Equal("2 · 跨 27 天", gDay.SpanBadge);
        var gMonth = new SongGroup { Takes = [new() { CreatedAt = now.AddDays(-95) }, new() { CreatedAt = now }] };
        Assert.Equal("2 · 跨 3 个月", gMonth.SpanBadge);
    }
}
