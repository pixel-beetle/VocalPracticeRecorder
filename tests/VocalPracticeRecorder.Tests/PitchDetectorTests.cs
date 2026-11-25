using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Tests;

public class PitchDetectorTests
{
    private static float[] MakeTone(int sampleRate, double seconds, Func<double, double> freqAt, double[] harmonics, double amp = 0.25)
    {
        int n = (int)(sampleRate * seconds);
        var buf = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double f = freqAt(t);
            phase += 2.0 * Math.PI * f / sampleRate;
            double s = 0;
            for (int h = 0; h < harmonics.Length; h++)
                s += harmonics[h] * Math.Sin((h + 1) * phase);
            buf[i] = (float)(s * amp);
        }
        return buf;
    }

    [Fact]
    public void PitchMath_NoteNamesAndZones_MatchEqualTemperament()
    {
        Assert.Equal(69.0, PitchMath.HzToMidi(440.0), 6);
        Assert.Equal("A4", PitchMath.MidiToNoteName(69));
        Assert.Equal("C4", PitchMath.MidiToNoteName(60));
        Assert.Equal("C#4", PitchMath.MidiToNoteName(61));
        Assert.Equal("C2", PitchMath.MidiToNoteName(36));
        Assert.True(PitchMath.IsCNote(60));
        Assert.False(PitchMath.IsCNote(61));
        Assert.True(PitchMath.IsBlackKey(61));
        Assert.False(PitchMath.IsBlackKey(60));

        var inTune = PitchMath.FromFrequency(PitchMath.MidiToHz(69.08), 0.95, 0.2f);
        Assert.Equal("A4", inTune.NoteName);
        Assert.Equal(PitchZone.InTune, inTune.Zone);
        Assert.Equal("+8¢", inTune.CentsText);

        var near = PitchMath.FromFrequency(PitchMath.MidiToHz(69.20), 0.95, 0.2f);
        Assert.Equal(PitchZone.SlightlyOff, near.Zone);

        var off = PitchMath.FromFrequency(PitchMath.MidiToHz(68.62), 0.95, 0.2f);
        Assert.Equal("A4", off.NoteName);
        Assert.Equal(PitchZone.OffPitch, off.Zone);
        Assert.Equal("-38¢", off.CentsText);
    }

    [Theory]
    [InlineData(65.41, "C2")]
    [InlineData(110.00, "A2")]
    [InlineData(146.83, "D3")]
    [InlineData(261.63, "C4")]
    [InlineData(440.00, "A4")]
    [InlineData(659.25, "E5")]
    [InlineData(1046.50, "C6")]
    public void YinDetector_PureSinesAcrossVocalRange_WithinTwoCents(double hz, string expectedNote)
    {
        var det = new YinPitchDetector(48000);
        var buf = MakeTone(48000, 0.12, _ => hz, [1.0]);
        var r = det.Detect(buf);

        Assert.True(r.IsVoiced, $"Expected voiced for {hz} Hz");
        Assert.Equal(expectedNote, r.NoteName);
        double errCents = 1200.0 * Math.Log2(r.FrequencyHz / hz);
        Assert.InRange(errCents, -2.5, 2.5);
    }

    [Theory]
    [InlineData(130.81, "C3")]
    [InlineData(220.00, "A3")]
    [InlineData(392.00, "G4")]
    [InlineData(698.46, "F5")]
    [InlineData(987.77, "B5")]
    public void YinDetector_RichHarmonicAndStrongSecondHarmonicVoice_NoOctaveError(double hz, string expectedNote)
    {
        var det = new YinPitchDetector(48000);
        // Simulate bright resonant singing voice where 2nd harmonic is 3.5x stronger than fundamental.
        var buf = MakeTone(48000, 0.12, _ => hz, [1.0, 3.5, 0.8, 0.4], amp: 0.08);
        var r = det.Detect(buf);

        Assert.True(r.IsVoiced, $"Expected voiced for harmonic {hz} Hz");
        Assert.Equal(expectedNote, r.NoteName);
        double errCents = 1200.0 * Math.Log2(r.FrequencyHz / hz);
        Assert.InRange(errCents, -3.5, 3.5);
    }

    [Fact]
    public void YinDetector_RejectsSilenceNoiseAndMainsHum()
    {
        var det = new YinPitchDetector(48000);

        Assert.False(det.Detect(new float[det.RequiredSamples]).IsVoiced);

        var rng = new Random(42);
        var noise = new float[det.RequiredSamples];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)((rng.NextDouble() * 2 - 1) * 0.25);
        Assert.False(det.Detect(noise).IsVoiced);

        // 50 Hz AC mains hum must be rejected (below vocal minimum 65 Hz & attenuated by HPF).
        var hum = MakeTone(48000, 0.12, _ => 50.0, [1.0], amp: 0.2);
        Assert.False(det.Detect(hum).IsVoiced);
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void PitchTracker_MultipleSampleRates_AccurateWithinThreeCents(int sampleRate)
    {
        var tracker = new PitchTracker();
        tracker.Configure(sampleRate, channels: 1, useSyntheticClock: true);

        const double targetHz = 329.63; // E4
        var mono = MakeTone(sampleRate, 0.45, _ => targetHz, [1.0, 0.7, 0.4], amp: 0.15);
        int chunk = sampleRate / 50; // 20 ms chunks like WASAPI callbacks
        for (int i = 0; i < mono.Length; i += chunk)
            tracker.PushInterleaved(mono.AsSpan(i, Math.Min(chunk, mono.Length - i)), 1);

        var r = tracker.Latest;
        Assert.True(r.IsVoiced, $"Expected voiced at {sampleRate} Hz");
        Assert.Equal("E4", r.NoteName);
        double errCents = 1200.0 * Math.Log2(r.FrequencyHz / targetHz);
        Assert.InRange(errCents, -3.5, 3.5);
    }

    [Theory]
    [InlineData(0)] // Left-only (Focusrite Scarlett Solo input 1)
    [InlineData(1)] // Right-only (input 2)
    public void PitchTracker_SingleMicStereoInterface_IsolatesActiveChannel(int activeCh)
    {
        var tracker = new PitchTracker();
        tracker.Configure(48000, channels: 2, useSyntheticClock: true);

        const double targetHz = 415.30; // G#4
        var mono = MakeTone(48000, 0.40, _ => targetHz, [1.0, 0.9, 0.5], amp: 0.12);
        var stereo = new float[mono.Length * 2];
        var rng = new Random(7);
        for (int i = 0; i < mono.Length; i++)
        {
            stereo[i * 2 + activeCh] = mono[i];
            // Unplugged input carries faint floor hiss.
            stereo[i * 2 + (1 - activeCh)] = (float)((rng.NextDouble() * 2 - 1) * 0.001);
        }

        const int chunkFrames = 960;
        for (int f = 0; f < mono.Length; f += chunkFrames)
        {
            int n = Math.Min(chunkFrames, mono.Length - f);
            tracker.PushInterleaved(stereo.AsSpan(f * 2, n * 2), 2);
        }

        var r = tracker.Latest;
        Assert.True(r.IsVoiced);
        Assert.Equal("G#4", r.NoteName);
        double errCents = 1200.0 * Math.Log2(r.FrequencyHz / targetHz);
        Assert.InRange(errCents, -3.5, 3.5);
    }

    [Fact]
    public void PitchTracker_TracksVocalVibratoAndReturnsToSilentAfterStop()
    {
        var tracker = new PitchTracker();
        tracker.Configure(48000, channels: 1, useSyntheticClock: true);

        // 5.5 Hz vibrato around G4 (MIDI 67) +/- 0.35 semitones (35 cents)
        var vibrato = MakeTone(48000, 0.9,
            t => PitchMath.MidiToHz(67.0 + 0.35 * Math.Sin(2.0 * Math.PI * 5.5 * t)),
            [1.0, 0.8, 0.5], amp: 0.15);

        const int chunk = 960;
        for (int i = 0; i < vibrato.Length; i += chunk)
            tracker.PushInterleaved(vibrato.AsSpan(i, Math.Min(chunk, vibrato.Length - i)), 1);

        var history = new List<PitchSample>();
        tracker.CopyHistory(history, 2.0, out _);
        var voiced = history.Where(s => s.Reading.IsVoiced).ToList();
        Assert.True(voiced.Count >= 30, $"Expected continuous vibrato tracking, got {voiced.Count} voiced frames");
        Assert.True(voiced.Max(s => s.Reading.MidiNote) > 67.20, "Vibrato upper peak lost");
        Assert.True(voiced.Min(s => s.Reading.MidiNote) < 66.80, "Vibrato lower peak lost");

        // Now feed silence -> tracker must drop back to silent cleanly.
        tracker.PushSilence(48000 / 4);
        Assert.False(tracker.Latest.IsVoiced);
    }
}
