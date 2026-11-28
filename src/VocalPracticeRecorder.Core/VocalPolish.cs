namespace VocalPracticeRecorder.Core;

/// <summary>
/// Light vocal polish chain shared identically between real-time preview and export:
/// 80 Hz 2nd-order Butterworth HPF (removes plosives/rumble) -> subtle air shelf (+1.5 dB @ 5.5 kHz)
/// -> gentle soft-knee vocal compressor (2.4:1) -> subtle natural stereo space reverb (~13% wet, ~0.95 s tail)
/// -> soft ceiling limiter.
/// </summary>
public static class VocalPolish
{
    private const double WetMix = 0.13;

    /// <summary>Processes mono vocal into natural stereo interleaved float samples (length = mono.Length * 2).</summary>
    public static float[] ProcessStereo(float[] mono, int sampleRate)
    {
        if (mono.Length == 0) return [];
        int n = mono.Length;
        var dry = new float[n];
        ApplyToneAndDynamics(mono, dry, sampleRate);

        var revL = new StereoReverbChannel(sampleRate, stereoOffsetSamples: 0);
        var revR = new StereoReverbChannel(sampleRate, stereoOffsetSamples: 23);

        var stereo = new float[n * 2];
        const float dryGain = (float)(1.0 - WetMix * 0.35);
        const float wetGain = (float)WetMix;

        for (int i = 0; i < n; i++)
        {
            float d = dry[i];
            float wl = revL.Process(d);
            float wr = revR.Process(d);
            stereo[i * 2] = SoftClip(d * dryGain + wl * wetGain);
            stereo[i * 2 + 1] = SoftClip(d * dryGain + wr * wetGain);
        }
        return stereo;
    }

    /// <summary>Processes mono vocal into mono float samples (for mono dry-vocal MP3 export when polish is on).</summary>
    public static float[] ProcessMono(float[] mono, int sampleRate)
    {
        if (mono.Length == 0) return [];
        var stereo = ProcessStereo(mono, sampleRate);
        var outMono = new float[mono.Length];
        for (int i = 0; i < outMono.Length; i++)
            outMono[i] = 0.5f * (stereo[i * 2] + stereo[i * 2 + 1]);
        return outMono;
    }

    /// <summary>Writes standard 32-bit IEEE float WAV so FFmpeg consumes the exact processed samples losslessly.</summary>
    public static void WriteWavFloat32(string path, float[] interleaved, int sampleRate, int channels)
    {
        int dataBytes = interleaved.Length * 4;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)3); // WAVE_FORMAT_IEEE_FLOAT
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 4);
        w.Write((short)(channels * 4));
        w.Write((short)32);
        w.Write("data"u8);
        w.Write(dataBytes);
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleaved.AsSpan());
        fs.Write(bytes);
    }

    private static void ApplyToneAndDynamics(float[] input, float[] output, int sampleRate)
    {
        var hpf = Biquad.HighPass(sampleRate, cutoffHz: 80.0, q: 0.7071);
        var air = Biquad.HighShelf(sampleRate, freqHz: 5500.0, gainDb: 1.5, slope: 0.7);

        // Soft-knee RMS/peak hybrid vocal compressor: threshold -18 dBFS, ratio 2.4:1, knee 6 dB, +2.0 dB makeup.
        const double threshDb = -18.0;
        const double ratio = 2.4;
        const double kneeDb = 6.0;
        const double halfKnee = kneeDb * 0.5;
        double makeupLin = Math.Pow(10.0, 2.0 / 20.0);

        double atkCoeff = Math.Exp(-1.0 / (sampleRate * 0.012));
        double relCoeff = Math.Exp(-1.0 / (sampleRate * 0.130));
        double env = 0;

        for (int i = 0; i < input.Length; i++)
        {
            double x = air.Process(hpf.Process(input[i]));
            double ax = Math.Abs(x);
            env = ax > env ? atkCoeff * env + (1.0 - atkCoeff) * ax : relCoeff * env + (1.0 - relCoeff) * ax;

            double envDb = 20.0 * Math.Log10(env + 1e-9);
            double over = envDb - threshDb;
            double gainRedDb = 0;
            if (over > halfKnee)
                gainRedDb = over * (1.0 - 1.0 / ratio);
            else if (over > -halfKnee)
            {
                double t = over + halfKnee;
                gainRedDb = (1.0 - 1.0 / ratio) * t * t / (2.0 * kneeDb);
            }

            double g = Math.Pow(10.0, -gainRedDb / 20.0) * makeupLin;
            output[i] = (float)(x * g);
        }
    }

    private static float SoftClip(float x)
    {
        const float knee = 0.92f;
        float ax = Math.Abs(x);
        if (ax <= knee) return x;
        float extra = ax - knee;
        float compressed = knee + (1.0f - knee) * MathF.Tanh(extra / (1.0f - knee));
        return MathF.CopySign(Math.Min(0.99f, compressed), x);
    }

    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public static Biquad HighPass(int fs, double cutoffHz, double q)
        {
            double w0 = 2.0 * Math.PI * cutoffHz / fs;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin / (2.0 * q);
            double a0 = 1.0 + alpha;
            return new Biquad
            {
                _b0 = (1.0 + cos) * 0.5 / a0,
                _b1 = -(1.0 + cos) / a0,
                _b2 = (1.0 + cos) * 0.5 / a0,
                _a1 = -2.0 * cos / a0,
                _a2 = (1.0 - alpha) / a0,
            };
        }

        public static Biquad HighShelf(int fs, double freqHz, double gainDb, double slope)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freqHz / fs;
            double cos = Math.Cos(w0), sin = Math.Sin(w0);
            double alpha = sin * 0.5 * Math.Sqrt((a + 1.0 / a) * (1.0 / slope - 1.0) + 2.0);
            double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;
            double a0 = (a + 1.0) - (a - 1.0) * cos + twoSqrtAAlpha;
            return new Biquad
            {
                _b0 = a * ((a + 1.0) + (a - 1.0) * cos + twoSqrtAAlpha) / a0,
                _b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cos) / a0,
                _b2 = a * ((a + 1.0) + (a - 1.0) * cos - twoSqrtAAlpha) / a0,
                _a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cos) / a0,
                _a2 = ((a + 1.0) - (a - 1.0) * cos - twoSqrtAAlpha) / a0,
            };
        }

        public double Process(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }

    /// <summary>Compact warm studio room reverb (4 damped combs + 2 series all-pass diffusers).</summary>
    private sealed class StereoReverbChannel
    {
        private static readonly int[] BaseComb44k = [1116, 1277, 1422, 1617];
        private static readonly int[] BaseAp44k = [225, 556];

        private readonly Comb[] _combs;
        private readonly AllPass[] _aps;
        private readonly float[] _preDelay;
        private int _preIdx;

        public StereoReverbChannel(int sampleRate, int stereoOffsetSamples)
        {
            double scale = sampleRate / 44100.0;
            // Subtle 14 ms pre-delay keeps vocal diction crisp and upfront before room bloom.
            _preDelay = new float[Math.Max(1, (int)Math.Round(sampleRate * 0.014))];
            _combs = new Comb[BaseComb44k.Length];
            for (int i = 0; i < BaseComb44k.Length; i++)
            {
                int len = Math.Max(16, (int)Math.Round((BaseComb44k[i] + stereoOffsetSamples) * scale));
                _combs[i] = new Comb(len, feedback: 0.72f, damp: 0.40f);
            }
            _aps = new AllPass[BaseAp44k.Length];
            for (int i = 0; i < BaseAp44k.Length; i++)
            {
                int len = Math.Max(8, (int)Math.Round((BaseAp44k[i] + stereoOffsetSamples / 2) * scale));
                _aps[i] = new AllPass(len, feedback: 0.5f);
            }
        }

        public float Process(float input)
        {
            float delayed = _preDelay[_preIdx];
            _preDelay[_preIdx] = input;
            if (++_preIdx == _preDelay.Length) _preIdx = 0;

            float sum = 0;
            for (int i = 0; i < _combs.Length; i++) sum += _combs[i].Process(delayed);
            sum *= 0.25f;
            for (int i = 0; i < _aps.Length; i++) sum = _aps[i].Process(sum);
            return sum;
        }

        private sealed class Comb(int length, float feedback, float damp)
        {
            private readonly float[] _buf = new float[length];
            private int _idx;
            private float _lp;

            public float Process(float x)
            {
                float y = _buf[_idx];
                _lp = y * (1f - damp) + _lp * damp;
                _buf[_idx] = x + _lp * feedback;
                if (++_idx == _buf.Length) _idx = 0;
                return y;
            }
        }

        private sealed class AllPass(int length, float feedback)
        {
            private readonly float[] _buf = new float[length];
            private int _idx;

            public float Process(float x)
            {
                float bufOut = _buf[_idx];
                float y = -x + bufOut;
                _buf[_idx] = x + bufOut * feedback;
                if (++_idx == _buf.Length) _idx = 0;
                return y;
            }
        }
    }
}
