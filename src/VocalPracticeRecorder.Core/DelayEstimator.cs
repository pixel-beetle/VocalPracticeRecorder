using System.Numerics;

namespace VocalPracticeRecorder.Core;

public sealed record DelayEstimate(
    /// <summary>How much later (seconds) the reference content appears in the target. Positive = target lags.</summary>
    double DelaySeconds,
    /// <summary>Percentage (0–100) of (window, band) analyses that support this delay.</summary>
    double Confidence,
    bool IsReliable,
    int Windows = 0,
    int BandsAgreeing = 0);

/// <summary>
/// Estimates the delay between the clean backing track (loopback) and the microphone track.
/// When singing over speakers the mic picks up the backing through the room, so a cross-correlation
/// peak gives the exact delay. Two traps are handled explicitly:
/// <list type="bullet">
/// <item>Periodic music (steady beats) creates alias peaks → correlation curves of several windows are
/// accumulated coherently, and each window only has to come close to its own best peak.</item>
/// <item>A singer matching a backing note creates a narrow-band match → real bleed is a filtered copy of the
/// whole spectrum, so the delay must show up independently in at least two frequency bands.</item>
/// </list>
/// </summary>
public static class DelayEstimator
{
    public const int AnalysisRate = 8000;
    public const int MinBandsAgreeing = 2;
    public const double AgreeToleranceSeconds = 0.002;
    /// <summary>PHAT-β partial whitening: 1 = full PHAT (fragile on tonal music), 0 = plain correlation.</summary>
    private const double PhatBeta = 0.75;
    private static readonly (double Lo, double Hi)[] Bands = [(80, 500), (500, 1500), (1500, 3800)];

    public static Action<string>? Trace { get; set; }

    public static DelayEstimate Estimate(
        ReadOnlySpan<float> reference, ReadOnlySpan<float> target, int sampleRate,
        double minLagSeconds = -0.2, double maxLagSeconds = 0.8)
    {
        float[] r = Downsample(reference, sampleRate, AnalysisRate);
        float[] t = Downsample(target, sampleRate, AnalysisRate);
        int len = Math.Min(r.Length, t.Length);
        double totalSec = len / (double)AnalysisRate;
        if (totalSec < 4) return new DelayEstimate(0, 0, false);

        double winSec = Math.Clamp(totalSec / 5, 4, 20);
        int win = (int)(winSec * AnalysisRate);
        int count = len / win;
        int minLag = (int)Math.Floor(minLagSeconds * AnalysisRate);
        int maxLag = (int)Math.Ceiling(maxLagSeconds * AnalysisRate);

        // Rank windows by backing energy; skip near-silent ones (intro/outro).
        var energies = new List<(int Start, double E)>();
        for (int w = 0; w < count; w++)
        {
            double e = 0;
            for (int i = w * win; i < (w + 1) * win; i++) e += r[i] * (double)r[i];
            energies.Add((w * win, e));
        }
        double maxE = energies.Count > 0 ? energies.Max(x => x.E) : 0;
        var chosen = energies.Where(x => x.E > maxE * 0.02).OrderByDescending(x => x.E).Take(8).ToList();
        if (chosen.Count == 0) return new DelayEstimate(0, 0, false);

        int lagCount = maxLag - minLag + 1;
        var bandSums = new double[Bands.Length][];
        for (int b = 0; b < Bands.Length; b++) bandSums[b] = new double[lagCount];
        foreach (var (start, _) in chosen)
        {
            var bandCurves = WindowCurves(r, t, start, win, len, minLag, maxLag);
            for (int b = 0; b < Bands.Length; b++)
                for (int i = 0; i < lagCount; i++) bandSums[b][i] += bandCurves[b][i];
        }
        // Normalise each band's accumulated curve to peak 1, then combine.
        var sum = new double[lagCount];
        foreach (var bs in bandSums)
        {
            double m = Math.Max(1e-12, bs.Max());
            for (int i = 0; i < lagCount; i++) { bs[i] /= m; sum[i] += bs[i]; }
        }

        int bestIdx = 0;
        for (int i = 1; i < lagCount; i++) if (sum[i] > sum[bestIdx]) bestIdx = i;
        double frac = 0;
        if (bestIdx > 0 && bestIdx < lagCount - 1)
        {
            double a = sum[bestIdx - 1], b = sum[bestIdx], c = sum[bestIdx + 1];
            double denom = a - 2 * b + c;
            if (Math.Abs(denom) > 1e-12) frac = Math.Clamp(0.5 * (a - c) / denom, -0.5, 0.5);
        }
        double lag = (minLag + bestIdx + frac) / AnalysisRate;

        // A band agrees if its accumulated curve peaks (within tolerance) at the consensus delay.
        int tol = (int)Math.Ceiling(AgreeToleranceSeconds * AnalysisRate);
        int bandsOk = 0;
        double strength = 0;
        for (int b = 0; b < Bands.Length; b++)
        {
            double local = 0;
            for (int i = Math.Max(0, bestIdx - tol); i <= Math.Min(lagCount - 1, bestIdx + tol); i++) local = Math.Max(local, bandSums[b][i]);
            strength += local;
            if (local >= BandPeakRatio) bandsOk++;
            Trace?.Invoke($"band{b} local={local:0.00}");
        }
        double pct = 100.0 * strength / Bands.Length;
        bool reliable = bandsOk >= MinBandsAgreeing;
        return new DelayEstimate(lag, Math.Round(pct), reliable, chosen.Count, bandsOk);
    }

    /// <summary>A band agrees when its accumulated curve at the consensus lag is at least this fraction of its peak.</summary>
    private const double BandPeakRatio = 0.9;

    /// <summary>Band-limited GCC-PHAT-β curves over the lag range for one window, each normalised to peak 1.</summary>
    private static double[][] WindowCurves(float[] r, float[] t, int start, int win, int len, int minLag, int maxLag)
    {
        int n = Fft.NextPow2(win + Math.Abs(minLag) + Math.Abs(maxLag) + 1);
        var R = new Complex[n];
        var T = new Complex[n];
        // Target window is extended by the lag range so delayed content is still inside it.
        for (int i = 0; i < win; i++) R[i] = r[start + i] * Hann(i, win);
        int tStart = Math.Max(0, start + minLag);
        int tEnd = Math.Min(len, start + win + maxLag);
        for (int i = tStart; i < tEnd; i++)
        {
            int k = i - start;
            T[((k % n) + n) % n] = t[i];
        }
        Fft.Transform(R, false);
        Fft.Transform(T, false);

        double maxMag = 0;
        var G = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            G[k] = T[k] * Complex.Conjugate(R[k]);
            maxMag = Math.Max(maxMag, G[k].Magnitude);
        }
        double floor = maxMag * 1e-6;
        for (int k = 0; k < n; k++)
        {
            double mag = G[k].Magnitude;
            G[k] = mag > floor ? G[k] / Math.Pow(mag, PhatBeta) : Complex.Zero;
        }

        int lagCount = maxLag - minLag + 1;
        var result = new double[Bands.Length][];
        var B = new Complex[n];
        for (int b = 0; b < Bands.Length; b++)
        {
            int lo = (int)(Bands[b].Lo / AnalysisRate * n), hi = (int)(Bands[b].Hi / AnalysisRate * n);
            Array.Clear(B);
            for (int k = lo; k < hi && k < n / 2; k++)
            {
                B[k] = G[k];
                B[n - k] = G[n - k]; // keep the spectrum Hermitian so the result stays real
            }
            Fft.Transform(B, true);
            var values = new double[lagCount];
            double best = 1e-12;
            for (int i = 0; i < lagCount; i++)
            {
                int lag = minLag + i;
                double v = B[((lag % n) + n) % n].Real;
                values[i] = v > 0 ? v : 0;
                if (v > best) best = v;
            }
            for (int i = 0; i < lagCount; i++) values[i] /= best;
            result[b] = values;
        }
        return result;
    }

    private static double Hann(int i, int n) => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));

    /// <summary>Box-filter + decimate. Good enough for delay estimation.</summary>
    public static float[] Downsample(ReadOnlySpan<float> src, int srcRate, int dstRate)
    {
        if (srcRate == dstRate) return src.ToArray();
        double ratio = (double)srcRate / dstRate;
        int outLen = (int)(src.Length / ratio);
        var dst = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            int a = (int)(i * ratio);
            int b = Math.Min(src.Length, (int)((i + 1) * ratio));
            double sum = 0;
            for (int j = a; j < b; j++) sum += src[j];
            dst[i] = b > a ? (float)(sum / (b - a)) : 0f;
        }
        return dst;
    }
}
