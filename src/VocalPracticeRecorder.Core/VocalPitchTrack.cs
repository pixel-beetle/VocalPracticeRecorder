namespace VocalPracticeRecorder.Core;

/// <summary>
/// Offline vocal pitch contour extracted from a recorded mic track at a fixed 20 ms grid (50 Hz),
/// cached compactly per channel in the session folder for instant editor overlay and MP4 burn-in.
/// Coordinates are stored in mic-source time so user VocalOffsetMs nudges shift the curve automatically.
/// </summary>
public sealed class VocalPitchTrack
{
    public const double DefaultStepSeconds = 0.02;
    private const uint FileMagic = 0x56505431; // "VPT1"

    public double StepSeconds { get; init; } = DefaultStepSeconds;
    public MicChannel Channel { get; init; }
    /// <summary>Fractional MIDI note per step; 0 = silent/unvoiced.</summary>
    public float[] Midi { get; init; } = [];
    public float MinMidi { get; init; }
    public float MaxMidi { get; init; }

    public int Count => Midi.Length;
    public double Duration => Midi.Length * StepSeconds;

    /// <summary>Returns the pitch reading at a timeline timestamp (accounting for VocalPlacement), or Silent.</summary>
    public PitchReading ReadingAtTimeline(double timelineSec, double vocalPlacement)
    {
        if (Midi.Length == 0) return PitchReading.Silent;
        double micSec = timelineSec - vocalPlacement;
        int idx = (int)Math.Round(micSec / StepSeconds);
        if (idx < 0 || idx >= Midi.Length) return PitchReading.Silent;
        float m = Midi[idx];
        if (m <= 0) return PitchReading.Silent;
        return PitchMath.FromFrequency(PitchMath.MidiToHz(m), 0.95, 0.2f);
    }

    /// <summary>
    /// Extracts a pitch contour from mono vocal samples using the exact same streaming YIN tracker
    /// as live monitoring (single source of truth across live &amp; offline).
    /// </summary>
    public static VocalPitchTrack Analyze(float[] vocalMono, int sampleRate, MicChannel channel, CancellationToken ct = default)
    {
        if (vocalMono.Length == 0 || sampleRate < 8000)
            return new VocalPitchTrack { Channel = channel, Midi = [], MinMidi = 55, MaxMidi = 73 };

        var tracker = new PitchTracker();
        tracker.Configure(sampleRate, channels: 1, useSyntheticClock: true);

        int hop = Math.Max(64, (int)Math.Round(sampleRate * DefaultStepSeconds));
        int totalSteps = (vocalMono.Length + hop - 1) / hop;
        var midis = new float[totalSteps];
        float minM = float.MaxValue, maxM = float.MinValue;
        int voicedCount = 0;

        // Feed in exact 20 ms chunks matching PitchTracker's hop size; skip heavy FFT on pure dead silence.
        int silentRun = 0;
        for (int step = 0; step < totalSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            int start = step * hop;
            int len = Math.Min(hop, vocalMono.Length - start);
            var span = vocalMono.AsSpan(start, len);

            float peak = 0;
            for (int i = 0; i < len; i++)
            {
                float a = Math.Abs(span[i]);
                if (a > peak) peak = a;
            }

            if (peak < 0.0025f && silentRun >= 4)
            {
                silentRun++;
                midis[step] = 0;
                continue;
            }
            if (peak < 0.0025f) silentRun++;
            else silentRun = 0;

            tracker.PushInterleaved(span, 1);
            var r = tracker.Latest;
            if (r.IsVoiced)
            {
                float m = (float)r.MidiNote;
                midis[step] = m;
                if (m < minM) minM = m;
                if (m > maxM) maxM = m;
                voicedCount++;
            }
        }

        if (voicedCount == 0)
        {
            minM = 55;
            maxM = 73;
        }
        else if (maxM - minM < 12)
        {
            float c = 0.5f * (minM + maxM);
            minM = c - 6;
            maxM = c + 6;
        }

        return new VocalPitchTrack
        {
            StepSeconds = DefaultStepSeconds,
            Channel = channel,
            Midi = midis,
            MinMidi = minM,
            MaxMidi = maxM,
        };
    }

    public static string CachePath(SessionInfo s, MicChannel ch) =>
        s.PathOf($"pitch-{ch.ToString().ToLowerInvariant()}.bin");

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            w.Write(FileMagic);
            w.Write(StepSeconds);
            w.Write((int)Channel);
            w.Write(MinMidi);
            w.Write(MaxMidi);
            w.Write(Midi.Length);
            for (int i = 0; i < Midi.Length; i++) w.Write(Midi[i]);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static VocalPitchTrack? TryLoad(string path, MicChannel expectedChannel)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != FileMagic) return null;
            double step = r.ReadDouble();
            var ch = (MicChannel)r.ReadInt32();
            if (ch != expectedChannel || step <= 0 || step > 0.2) return null;
            float minM = r.ReadSingle();
            float maxM = r.ReadSingle();
            int len = r.ReadInt32();
            if (len < 0 || len > 1_000_000) return null;
            var midis = new float[len];
            for (int i = 0; i < len; i++) midis[i] = r.ReadSingle();
            return new VocalPitchTrack { StepSeconds = step, Channel = ch, Midi = midis, MinMidi = minM, MaxMidi = maxM };
        }
        catch { return null; }
    }

    /// <summary>Loads cached pitch track if available; otherwise analyzes vocalMono and caches it.</summary>
    public static VocalPitchTrack LoadOrAnalyze(SessionInfo s, float[] vocalMono, int sampleRate, MicChannel ch, CancellationToken ct = default)
    {
        string path = CachePath(s, ch);
        var cached = TryLoad(path, ch);
        if (cached != null) return cached;
        var track = Analyze(vocalMono, sampleRate, ch, ct);
        try { if (!string.IsNullOrEmpty(s.Folder) && Directory.Exists(s.Folder)) track.Save(path); } catch { }
        return track;
    }
}
