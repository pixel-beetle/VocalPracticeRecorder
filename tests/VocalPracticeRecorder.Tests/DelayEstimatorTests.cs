using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Tests;

public class DelayEstimatorTests
{
    private const int Rate = 48000;

    private static float[] Noise(int n, int seed, float amp = 0.3f)
    {
        var r = new Random(seed);
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)((r.NextDouble() * 2 - 1) * amp);
        return a;
    }

    /// <summary>
    /// Music-like backing: steady 120 BPM beat (strongly periodic → competing correlation peaks),
    /// bass + chord tones and short noise "hi-hat" bursts.
    /// </summary>
    private static float[] Music(int n, int seed, double hats = 0.15, double introSec = 0)
    {
        var rnd = new Random(seed);
        var a = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            if (t < introSec) continue;
            double beat = t % 0.5, hat = t % 0.25;
            double env = 0.4 + 0.6 * Math.Exp(-6 * beat);
            double tone = 0.35 * Math.Sin(2 * Math.PI * 110 * t) + 0.2 * Math.Sin(2 * Math.PI * 277 * t) + 0.2 * Math.Sin(2 * Math.PI * 330 * t)
                          + 0.08 * Math.Sin(2 * Math.PI * 1320 * t) + 0.04 * Math.Sin(2 * Math.PI * 2640 * t);
            double noise = hats * (rnd.NextDouble() * 2 - 1) * Math.Exp(-40 * hat);
            a[i] = (float)(tone * env + noise);
        }
        return a;
    }

    /// <summary>Worst case seen in the demo data: perfectly periodic pure tones, short song, silent intro.</summary>
    [Fact]
    public void Pure_periodic_tones_short_song_still_reliable()
    {
        int n = Rate * 34;
        var backing = Music(n, 1, hats: 0, introSec: 2);
        var mic = Mix(Voice(n, 4), backing, (int)Math.Round(0.090 * Rate), 0.12f);
        var est = DelayEstimator.Estimate(backing, mic, Rate);
        Assert.True(est.IsReliable, $"confidence {est.Confidence}");
        Assert.InRange(est.DelaySeconds, 0.089, 0.091);
    }

    /// <summary>
    /// Realistic speaker practice: 4-minute song, bleed only −25 dB relative to the singer (close mic,
    /// speakers at moderate volume), voice with harmonics following the melody.
    /// </summary>
    [Theory]
    [InlineData(240, 0.065)]
    [InlineData(240, 0.210)]
    [InlineData(60, 0.080)]   // short take (just the chorus)
    [InlineData(30, 0.120)]
    public void Realistic_quiet_bleed_under_loud_voice(int seconds, double delay)
    {
        int n = Rate * seconds;
        var backing = Music(n, 11, hats: 0.15, introSec: 3);
        var voice = Voice(n, 6);
        double rv = Math.Sqrt(voice.Average(x => (double)x * x)), rb = Math.Sqrt(backing.Average(x => (double)x * x));
        float bleed = (float)(rv / rb * Math.Pow(10, -25 / 20.0));
        var mic = Mix(voice, backing, (int)Math.Round(delay * Rate), bleed);
        var est = DelayEstimator.Estimate(backing, mic, Rate);
        Assert.True(est.IsReliable, $"confidence {est.Confidence} bands {est.BandsAgreeing}");
        Assert.InRange(est.DelaySeconds, delay - 0.001, delay + 0.001);
    }

    /// <summary>
    /// Singer imitating the melody: same notes as the backing, but like a real voice: continuous phase,
    /// slightly detuned notes (±15 cents), ±50 cent vibrato, human timing (note changes ±50 ms around
    /// the beat) and soft 40 ms attacks instead of clicks.
    /// </summary>
    private static float[] Voice(int n, double startSec = 1)
    {
        var rnd = new Random(42);
        int notes = n / Rate / 2 + 2;
        var starts = new double[notes];
        var detunes = new double[notes];
        for (int k = 0; k < notes; k++)
        {
            starts[k] = 2 * k + (k == 0 ? 0 : rnd.NextDouble() * 0.1 - 0.05);
            detunes[k] = Math.Pow(2, (rnd.NextDouble() * 30 - 15) / 1200);
        }
        var a = new float[n];
        double phase = 0;
        int note = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            while (note + 1 < notes && t >= starts[note + 1]) note++;
            double f = 330 * detunes[note] * (1 + 0.03 * Math.Sin(2 * Math.PI * 5.5 * t)) * (note % 2 == 0 ? 1 : 1.122);
            phase += 2 * Math.PI * f / Rate;
            double attack = Math.Min(1, (t - starts[note]) / 0.04);
            a[i] = t > startSec ? (float)(attack * (0.4 * Math.Sin(phase) + 0.1 * Math.Sin(2 * phase))) : 0f;
        }
        return a;
    }

    private static float[] Mix(float[] voice, float[] backing, int delaySamples, float bleed)
    {
        var mic = new float[voice.Length];
        for (int i = 0; i < mic.Length; i++)
        {
            int j = i - delaySamples;
            mic[i] = voice[i] + (bleed > 0 && j >= 0 && j < backing.Length ? backing[j] * bleed : 0f);
        }
        return mic;
    }

    /// <summary>Speaker scenario on noise: mic = room-attenuated, delayed backing + uncorrelated voice.</summary>
    [Theory]
    [InlineData(0.083)]
    [InlineData(0.250)]
    [InlineData(0.012)]
    [InlineData(-0.040)]
    public void Recovers_delay_of_noise_bleed_within_one_ms(double delay)
    {
        int n = Rate * 30;
        var backing = Noise(n, 1);
        var mic = Mix(Noise(n, 2, 0.6f), backing, (int)Math.Round(delay * Rate), 0.15f);
        var est = DelayEstimator.Estimate(backing, mic, Rate);
        Assert.True(est.IsReliable, $"confidence {est.Confidence}");
        Assert.InRange(est.DelaySeconds, delay - 0.001, delay + 0.001);
    }

    /// <summary>Periodic music must not lock onto a beat-period alias (regression: 80 ms estimated as 892 ms).</summary>
    [Theory]
    [InlineData(0.080, 0.12f)]
    [InlineData(0.150, 0.05f)]
    [InlineData(0.031, 0.25f)]
    public void Recovers_delay_of_periodic_music_bleed(double delay, float bleed)
    {
        int n = Rate * 60;
        var backing = Music(n, 7);
        var mic = Mix(Voice(n), backing, (int)Math.Round(delay * Rate), bleed);
        var est = DelayEstimator.Estimate(backing, mic, Rate);
        Assert.True(est.IsReliable, $"confidence {est.Confidence}");
        Assert.InRange(est.DelaySeconds, delay - 0.001, delay + 0.001);
    }

    /// <summary>Headphone scenarios: no bleed → must not claim a reliable delay, even if the voice follows the melody.</summary>
    [Fact]
    public void No_bleed_is_not_reliable()
    {
        foreach (int seconds in new[] { 30, 60, 240 })
        {
            int n = Rate * seconds;
            var a = DelayEstimator.Estimate(Noise(n, 3), Noise(n, 4), Rate);
            Assert.False(a.IsReliable, $"noise {seconds}s: bands {a.BandsAgreeing}");
            var backing = Music(n, 9, introSec: 2);
            var est = DelayEstimator.Estimate(backing, Mix(Voice(n), backing, 0, 0), Rate);
            Assert.False(est.IsReliable, $"music {seconds}s: bands {est.BandsAgreeing} delay {est.DelaySeconds}");
        }
    }

    [Fact]
    public void Apply_estimate_falls_back_to_remembered_offset()
    {
        var e = new EditState();
        SessionPostProcessor.ApplyEstimate(e, new DelayEstimate(0.2, 20, false), -120);
        Assert.Equal(AlignMode.Remembered, e.AlignMode);
        Assert.Equal(-120, e.VocalOffsetMs);

        SessionPostProcessor.ApplyEstimate(e, new DelayEstimate(0.083, 100, true), -120);
        Assert.Equal(AlignMode.Auto, e.AlignMode);
        Assert.Equal(-83, e.VocalOffsetMs);
    }
}
