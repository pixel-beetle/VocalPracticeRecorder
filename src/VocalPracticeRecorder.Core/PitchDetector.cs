using System.Numerics;

namespace VocalPracticeRecorder.Core;

/// <summary>Intonation quality zone derived from absolute cent deviation.</summary>
public enum PitchZone
{
    Silent,
    InTune,
    SlightlyOff,
    OffPitch,
}

/// <summary>Immutable pitch analysis result for one frame.</summary>
public readonly record struct PitchReading(
    double FrequencyHz,
    double MidiNote,
    int NearestMidi,
    double Cents,
    double Clarity,
    float Rms,
    bool IsVoiced)
{
    public static readonly PitchReading Silent = new(0, 0, 0, 0, 0, 0, false);

    public string NoteName => IsVoiced ? PitchMath.MidiToNoteName(NearestMidi) : "--";

    public PitchZone Zone
    {
        get
        {
            if (!IsVoiced) return PitchZone.Silent;
            double a = Math.Abs(Cents);
            if (a <= 12.0) return PitchZone.InTune;
            if (a <= 28.0) return PitchZone.SlightlyOff;
            return PitchZone.OffPitch;
        }
    }

    public string CentsText => IsVoiced ? $"{(Cents >= 0 ? "+" : "")}{(int)Math.Round(Cents)}¢" : "";
}

/// <summary>Equal-temperament pitch conversions (A4 = 440 Hz, MIDI 69).</summary>
public static class PitchMath
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly bool[] SharpFlags = [false, true, false, true, false, false, true, false, true, false, true, false];

    public const double A4Hz = 440.0;
    public const int A4Midi = 69;

    public static double HzToMidi(double hz) => hz > 0 ? A4Midi + 12.0 * Math.Log2(hz / A4Hz) : 0;

    public static double MidiToHz(double midi) => A4Hz * Math.Pow(2.0, (midi - A4Midi) / 12.0);

    public static string MidiToNoteName(int midi)
    {
        int pc = ((midi % 12) + 12) % 12;
        int octave = (int)Math.Floor(midi / 12.0) - 1;
        return $"{Names[pc]}{octave}";
    }

    public static bool IsBlackKey(int midi) => SharpFlags[((midi % 12) + 12) % 12];

    public static bool IsCNote(int midi) => ((midi % 12) + 12) % 12 == 0;

    public static PitchReading FromFrequency(double hz, double clarity, float rms)
    {
        if (hz <= 0 || double.IsNaN(hz) || double.IsInfinity(hz)) return PitchReading.Silent;
        double midi = HzToMidi(hz);
        int nearest = (int)Math.Round(midi, MidpointRounding.AwayFromZero);
        double cents = Math.Clamp((midi - nearest) * 100.0, -50.0, 50.0);
        return new PitchReading(hz, midi, nearest, cents, clarity, rms, IsVoiced: true);
    }
}

/// <summary>
/// FFT-accelerated YIN pitch detector tuned for live singing voices (65 Hz C2 .. 1350 Hz E6).
/// Reuses all scratch buffers so steady-state analysis allocates zero heap memory.
/// </summary>
public sealed class YinPitchDetector
{
    public const double DefaultMinHz = 65.0;
    public const double DefaultMaxHz = 1350.0;

    private readonly int _sampleRate;
    private readonly int _tauMin;
    private readonly int _tauMax;
    private readonly int _windowSize;
    private readonly int _requiredSamples;
    private readonly int _fftSize;

    // Pre-allocated scratch buffers (zero GC pressure on audio callbacks).
    private readonly double[] _centered;
    private readonly double[] _prefixEnergy;
    private readonly Complex[] _fftA;
    private readonly Complex[] _fftB;
    private readonly double[] _diff;
    private readonly double[] _cmndf;

    public int SampleRate => _sampleRate;
    public int RequiredSamples => _requiredSamples;

    public YinPitchDetector(int sampleRate, double minHz = DefaultMinHz, double maxHz = DefaultMaxHz)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _tauMin = Math.Max(2, (int)Math.Floor(sampleRate / maxHz));
        _tauMax = Math.Max(_tauMin + 4, (int)Math.Ceiling(sampleRate / minHz));
        // Integration window >= 22 ms and >= tauMax so low male fundamentals (C2..E2) get full correlation support.
        _windowSize = Math.Max(_tauMax, (int)Math.Ceiling(sampleRate * 0.022));
        _requiredSamples = _windowSize + _tauMax;
        _fftSize = Fft.NextPow2(_requiredSamples);

        _centered = new double[_requiredSamples];
        _prefixEnergy = new double[_requiredSamples + 1];
        _fftA = new Complex[_fftSize];
        _fftB = new Complex[_fftSize];
        _diff = new double[_tauMax + 1];
        _cmndf = new double[_tauMax + 1];
    }

    /// <summary>
    /// Estimates fundamental frequency from at least <see cref="RequiredSamples"/> mono float samples.
    /// </summary>
    public PitchReading Detect(
        ReadOnlySpan<float> mono,
        double threshold = 0.16,
        float rmsGate = 0.0055f,
        double? priorHz = null)
    {
        if (mono.Length < _requiredSamples) return PitchReading.Silent;
        var slice = mono[^_requiredSamples..];

        // 1. Zero-phase DC removal (no per-window IIR startup transient!) and RMS energy.
        double mean = 0;
        for (int i = 0; i < _requiredSamples; i++) mean += slice[i];
        mean /= _requiredSamples;

        double sumSq = 0;
        _prefixEnergy[0] = 0;
        for (int i = 0; i < _requiredSamples; i++)
        {
            double y = slice[i] - mean;
            _centered[i] = y;
            sumSq += y * y;
            _prefixEnergy[i + 1] = sumSq;
        }

        float rms = (float)Math.Sqrt(sumSq / _requiredSamples);
        if (rms < rmsGate) return new PitchReading(0, 0, 0, 0, 0, rms, false);

        // 2. Exact YIN difference function via FFT cross-correlation:
        //    d(tau) = sum_{j=0}^{W-1} x[j]^2 + sum_{j=0}^{W-1} x[j+tau]^2 - 2 * sum_{j=0}^{W-1} x[j] * x[j+tau]
        Array.Clear(_fftA);
        Array.Clear(_fftB);
        for (int i = 0; i < _windowSize; i++) _fftA[i] = new Complex(_centered[i], 0);
        for (int i = 0; i < _requiredSamples; i++) _fftB[i] = new Complex(_centered[i], 0);

        Fft.Transform(_fftA, inverse: false);
        Fft.Transform(_fftB, inverse: false);
        for (int i = 0; i < _fftSize; i++)
            _fftA[i] = Complex.Conjugate(_fftA[i]) * _fftB[i];
        Fft.Transform(_fftA, inverse: true);

        // 3. Difference function d(tau) and Cumulative Mean Normalized Difference Function (CMNDF).
        double energy0 = _prefixEnergy[_windowSize];
        double runningSum = 0;
        _diff[0] = 0;
        _cmndf[0] = 1.0;
        for (int tau = 1; tau <= _tauMax; tau++)
        {
            double energyTau = _prefixEnergy[_windowSize + tau] - _prefixEnergy[tau];
            double d = energy0 + energyTau - 2.0 * _fftA[tau].Real;
            if (d < 0) d = 0;
            _diff[tau] = d;
            runningSum += d;
            _cmndf[tau] = runningSum > 1e-12 ? d * tau / runningSum : 1.0;
        }

        // 4. First valley below threshold.
        int bestTau = -1;
        for (int tau = _tauMin; tau <= _tauMax; tau++)
        {
            if (_cmndf[tau] < threshold)
            {
                while (tau + 1 <= _tauMax && _cmndf[tau + 1] <= _cmndf[tau]) tau++;
                bestTau = tau;
                break;
            }
        }
        if (bestTau < 0) return new PitchReading(0, 0, 0, 0, 0, rms, false);

        // 5a. Strong-2nd-harmonic octave-up guard: if 2*bestTau is dramatically cleaner, the voice had
        //     a much louder 2nd harmonic than fundamental; prefer the true fundamental at 2*bestTau.
        if (_cmndf[bestTau] > 0.04)
        {
            int dbl = bestTau * 2;
            if (dbl <= _tauMax)
            {
                int dblMin = LocalMin(dbl, 3);
                if (_cmndf[dblMin] < _cmndf[bestTau] * 0.45 && _cmndf[bestTau] - _cmndf[dblMin] > 0.05)
                    bestTau = dblMin;
            }
        }

        // 5b. Tracking continuity assist for borderline octave flips when singing continuously.
        if (priorHz is > 0)
        {
            double priorTau = _sampleRate / priorHz.Value;
            if (priorTau >= _tauMin && priorTau <= _tauMax)
            {
                double ratio = bestTau / priorTau;
                if (ratio > 1.8 && ratio < 2.2)
                {
                    int halfMin = LocalMin((int)Math.Round(bestTau * 0.5), 3);
                    if (halfMin >= _tauMin && _cmndf[halfMin] < threshold + 0.06 && _cmndf[halfMin] <= _cmndf[bestTau] + 0.05)
                        bestTau = halfMin;
                }
                else if (ratio > 0.45 && ratio < 0.56)
                {
                    int dblMin = LocalMin((int)Math.Round(bestTau * 2.0), 3);
                    if (dblMin <= _tauMax && _cmndf[dblMin] < threshold && _cmndf[dblMin] <= _cmndf[bestTau] + 0.03)
                        bestTau = dblMin;
                }
            }
        }

        // 6. Parabolic interpolation on raw d(tau) (YIN Step 5) for unbiased sub-cent precision.
        int diffTau = bestTau;
        if (diffTau > _tauMin && _diff[diffTau - 1] < _diff[diffTau]) diffTau--;
        else if (diffTau < _tauMax && _diff[diffTau + 1] < _diff[diffTau]) diffTau++;

        double refinedTau = diffTau;
        if (diffTau > _tauMin && diffTau < _tauMax)
        {
            double s0 = _diff[diffTau - 1];
            double s1 = _diff[diffTau];
            double s2 = _diff[diffTau + 1];
            double denom = 2.0 * (s0 - 2.0 * s1 + s2);
            if (Math.Abs(denom) > 1e-12)
            {
                double delta = (s0 - s2) / denom;
                if (Math.Abs(delta) <= 1.0) refinedTau += delta;
            }
        }

        double hz = _sampleRate / refinedTau;
        if (hz < DefaultMinHz * 0.95 || hz > DefaultMaxHz * 1.05)
            return new PitchReading(0, 0, 0, 0, 0, rms, false);

        double clarity = Math.Clamp(1.0 - _cmndf[bestTau], 0.0, 1.0);
        return PitchMath.FromFrequency(hz, clarity, rms);
    }

    private int LocalMin(int center, int radius)
    {
        int lo = Math.Max(_tauMin, center - radius);
        int hi = Math.Min(_tauMax, center + radius);
        int best = lo;
        for (int i = lo + 1; i <= hi; i++)
            if (_cmndf[i] < _cmndf[best]) best = i;
        return best;
    }
}
