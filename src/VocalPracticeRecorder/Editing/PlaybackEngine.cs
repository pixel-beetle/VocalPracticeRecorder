using NAudio.CoreAudioApi;
using NAudio.Wave;
using VocalPracticeRecorder.Core;

#pragma warning disable CS0618 // WasapiOut: simple, proven, fine for preview playback

namespace VocalPracticeRecorder.Editing;

/// <summary>
/// Real-time preview mixer. Reads the edit state on every buffer, so offset/gain changes are heard instantly.
/// The output stream runs continuously (silence while paused) for instant, consistent start latency.
/// </summary>
public sealed class PlaybackEngine : ISampleProvider, IDisposable
{
    private const int Rate = SessionAudio.Rate;
    private const int LatencyMs = 60;

    private readonly SessionInfo _s;
    private readonly SessionAudio _audio;
    private readonly WasapiOut? _out;
    private long _pos;          // timeline sample being rendered next
    private volatile bool _playing;
    private long _endSample;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    public bool IsPlaying => _playing;
    public event Action? Ended;
    public string? OutputError { get; }

    public PlaybackEngine(SessionInfo s, SessionAudio audio)
    {
        _s = s;
        _audio = audio;
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, LatencyMs);
            _out.Init(this.ToWaveProvider());
            _out.Play();
        }
        catch (Exception ex)
        {
            OutputError = ex.Message;
            _out?.Dispose();
            _out = null;
        }
    }

    /// <summary>Audible position on the timeline (seconds), compensating output latency.</summary>
    public double Position
    {
        get
        {
            double p = Interlocked.Read(ref _pos) / (double)Rate;
            return _playing ? Math.Max(0, p - LatencyMs / 1000.0) : p;
        }
    }

    public void Seek(double seconds) => Interlocked.Exchange(ref _pos, (long)(Math.Max(0, seconds) * Rate));

    public void Play(double from, double until)
    {
        Seek(from);
        _endSample = (long)(until * Rate);
        _playing = true;
    }

    public void Pause()
    {
        if (!_playing) return;
        // Snap the cursor back to what was actually heard.
        double heard = Position;
        _playing = false;
        Seek(heard);
    }

    public int Read(Span<float> buffer)
    {
        int count = buffer.Length;
        if (!_playing)
        {
            buffer.Clear();
            return count;
        }

        var e = _s.Edit;
        float vg = (float)Math.Pow(10, e.VocalGainDb / 20);
        float bg = e.BackingMuted ? 0f : (float)Math.Pow(10, e.BackingGainDb / 20);
        var vocal = _audio.MicMono(e.MicChannel);
        var back = _audio.BackingStereo;
        long vOff = (long)Math.Round(_s.VocalPlacement * Rate);
        long bOff = (long)Math.Round(_s.BackingPlacement * Rate);
        long backFrames = back.Length / 2;

        long pos = Interlocked.Read(ref _pos);
        int frames = count / 2;
        bool ended = false;
        // Kept segments as sample ranges: playback jumps over deleted sections exactly like the export.
        var segs = EditTimeline.KeptSegments(_s);
        int si = 0;
        while (si < segs.Count && pos >= (long)Math.Round(segs[si].End * Rate)) si++;
        if (si < segs.Count && pos < (long)Math.Round(segs[si].Start * Rate))
        {
            pos = (long)Math.Round(segs[si].Start * Rate);
            Interlocked.Increment(ref _jumps);
        }
        long segEnd = si < segs.Count ? (long)Math.Round(segs[si].End * Rate) : 0;
        for (int i = 0; i < frames; i++)
        {
            if (si < segs.Count && pos >= segEnd)
            {
                si++;
                if (si < segs.Count)
                {
                    pos = (long)Math.Round(segs[si].Start * Rate);
                    segEnd = (long)Math.Round(segs[si].End * Rate);
                    Interlocked.Increment(ref _jumps);
                }
            }
            long t = pos;
            if (si >= segs.Count || t >= _endSample)
            {
                buffer[(i * 2)..].Clear();
                ended = true;
                break;
            }
            pos++;
            float v = 0, l = 0, r = 0;
            long vi = t - vOff;
            if (vi >= 0 && vi < vocal.Length) v = vocal[vi] * vg;
            long bi = t - bOff;
            if (bi >= 0 && bi < backFrames)
            {
                l = back[bi * 2] * bg;
                r = back[bi * 2 + 1] * bg;
            }
            buffer[i * 2] = Math.Clamp(v + l, -1f, 1f);
            buffer[i * 2 + 1] = Math.Clamp(v + r, -1f, 1f);
        }
        Interlocked.Exchange(ref _pos, pos);
        if (ended)
        {
            _playing = false;
            Ended?.Invoke();
        }
        return count;
    }

    private long _jumps;
    /// <summary>Increments whenever playback skipped over a deleted section (UI re-syncs the video).</summary>
    public long JumpCount => Interlocked.Read(ref _jumps);

    public void Dispose()
    {
        _playing = false;
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
    }
}
