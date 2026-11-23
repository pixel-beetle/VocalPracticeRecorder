using System.Globalization;

namespace VocalPracticeRecorder.Core;

/// <summary>Decoded audio of a session, kept in memory for waveform display, playback and alignment.</summary>
public sealed class SessionAudio
{
    public const int Rate = 48000;

    /// <summary>Mic samples, interleaved, <see cref="MicChannels"/> channels.</summary>
    public required float[] MicInterleaved { get; init; }
    public required int MicChannels { get; init; }
    /// <summary>Backing track, interleaved stereo.</summary>
    public required float[] BackingStereo { get; init; }

    private float[]? _micMono;
    private MicChannel _micMonoChannel;

    public float[] MicMono(MicChannel ch)
    {
        if (_micMono == null || _micMonoChannel != ch)
        {
            _micMono = ChannelSelector.Extract(MicInterleaved, MicChannels, ch);
            _micMonoChannel = ch;
        }
        return _micMono;
    }

    private float[]? _backingMono;
    public float[] BackingMono => _backingMono ??= ChannelSelector.Downmix(BackingStereo, 2);

    public static async Task<SessionAudio> LoadAsync(SessionInfo s, CancellationToken ct = default)
    {
        int micCh = Math.Clamp(s.Mic.Channels, 1, 2);
        var micTask = Ffmpeg.DecodeAudioAsync(s.PathOf(s.Mic.File), Rate, micCh, ct);
        var backTask = Ffmpeg.DecodeAudioAsync(s.PathOf(s.Backing.File), Rate, 2, ct);
        await Task.WhenAll(micTask, backTask).ConfigureAwait(false);
        return new SessionAudio { MicInterleaved = micTask.Result, MicChannels = micCh, BackingStereo = backTask.Result };
    }

    /// <summary>Copies a mono track into a timeline-aligned buffer (zero padded / cropped).</summary>
    public static float[] PlaceOnTimeline(float[] mono, double placementSeconds, int totalSamples)
    {
        var dst = new float[totalSamples];
        int offset = (int)Math.Round(placementSeconds * Rate);
        int srcStart = Math.Max(0, -offset);
        int dstStart = Math.Max(0, offset);
        int count = Math.Min(mono.Length - srcStart, totalSamples - dstStart);
        if (count > 0) Array.Copy(mono, srcStart, dst, dstStart, count);
        return dst;
    }
}

public static class SessionPostProcessor
{
    /// <summary>Converts raw WAV captures to FLAC, keeping the WAV only if conversion fails.</summary>
    public static async Task CompressRawAsync(SessionInfo s, CancellationToken ct = default)
    {
        async Task Convert(TrackInfo t)
        {
            if (!t.File.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return;
            var src = s.PathOf(t.File);
            if (!File.Exists(src)) return;
            var dstName = System.IO.Path.ChangeExtension(t.File, ".flac");
            var dst = s.PathOf(dstName);
            await Ffmpeg.RunAsync(["-hide_banner", "-y", "-nostdin", "-i", src, "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-compression_level", "5", dst], null, ct).ConfigureAwait(false);
            t.File = dstName;
            try { File.Delete(src); } catch { }
        }
        await Task.WhenAll(Convert(s.Mic), Convert(s.Backing)).ConfigureAwait(false);
    }

    public static async Task MakeThumbnailAsync(SessionInfo s, CancellationToken ct = default)
    {
        double at = Math.Max(0, s.Duration * 0.4);
        try
        {
            await Ffmpeg.RunAsync(["-hide_banner", "-y", "-nostdin", "-ss", at.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", s.PathOf(s.Video.File), "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "4", s.PathOf("thumb.jpg")], null, ct).ConfigureAwait(false);
        }
        catch { /* thumbnail is cosmetic */ }
    }

    /// <summary>Estimates vocal delay from backing bleed and writes the result into the edit state.</summary>
    public static DelayEstimate AutoAlign(SessionInfo s, SessionAudio audio, double? rememberedOffsetMs)
    {
        int total = (int)(s.Duration * SessionAudio.Rate);
        if (total <= 0) total = audio.BackingMono.Length;
        var back = SessionAudio.PlaceOnTimeline(audio.BackingMono, s.BackingPlacement, total);
        var mic = SessionAudio.PlaceOnTimeline(audio.MicMono(s.Edit.MicChannel), s.RawVocalPlacement, total);
        var est = DelayEstimator.Estimate(back, mic, SessionAudio.Rate);
        ApplyEstimate(s.Edit, est, rememberedOffsetMs);
        return est;
    }

    public static void ApplyEstimate(EditState e, DelayEstimate est, double? rememberedOffsetMs)
    {
        e.AutoDelayMs = Math.Round(est.DelaySeconds * 1000, 1);
        e.AutoConfidence = Math.Round(est.Confidence, 1);
        e.AutoReliable = est.IsReliable;
        if (est.IsReliable)
        {
            e.VocalOffsetMs = Math.Round(-est.DelaySeconds * 1000);
            e.AlignMode = AlignMode.Auto;
        }
        else if (rememberedOffsetMs.HasValue)
        {
            e.VocalOffsetMs = rememberedOffsetMs.Value;
            e.AlignMode = AlignMode.Remembered;
        }
        else
        {
            e.VocalOffsetMs = 0;
            e.AlignMode = AlignMode.None;
        }
    }
}
