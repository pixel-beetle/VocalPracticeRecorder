using System.Diagnostics;

namespace VocalPracticeRecorder.Core;

/// <summary>One timestamped point in the scrolling pitch history.</summary>
public readonly record struct PitchSample(double TimeSeconds, PitchReading Reading);

/// <summary>
/// Thread-safe streaming real-time pitch tracker. Accepts interleaved multi-channel float mic buffers
/// (automatically isolating the active channel on single-mic stereo interfaces like Focusrite Scarlett Solo),
/// runs YIN at ~50 Hz hop rate with onset/release hysteresis and 3-frame median glitch rejection,
/// and exposes the latest reading plus a rolling history buffer for UI rendering.
/// </summary>
public sealed class PitchTracker
{
    private const double HopSeconds = 0.020; // 50 Hz analysis rate
    private const double HistorySeconds = 8.0;
    private const float OnsetRmsGate = 0.0063f;   // ~ -44 dBFS
    private const float SustainRmsGate = 0.0040f; // ~ -48 dBFS
    private const double OnsetThreshold = 0.15;
    private const double SustainThreshold = 0.20;

    private readonly object _lock = new();
    private readonly PitchSample[] _history;
    private int _historyHead;
    private int _historyCount;

    private int _inputSampleRate;
    private int _channels;
    private int _decimation = 1;
    private double _decimAccum;
    private int _decimCount;

    // Continuous stateful 50 Hz high-pass filter (removes desk rumble / AC hum without window-edge transients).
    private double _hpAlpha;
    private double _hpPrevX;
    private double _hpPrevY;

    private YinPitchDetector? _detector;
    private float[] _ring = [];
    private int _ringPos;
    private int _ringFilled;
    private float[] _linear = [];
    private int _hopSamples;
    private int _sinceLastHop;

    // Auto channel activity tracker (isolates active mic on 2ch single-mic interfaces like Scarlett Solo).
    private double[] _chEnergy = [];

    // Temporal smoothing & hysteresis state.
    private readonly double[] _recentMidis = new double[3];
    private int _voicedStreak;
    private int _unvoicedStreak;
    private double? _lastVoicedHz;
    private double _lastVoicedTime = -10;
    private PitchReading _latest = PitchReading.Silent;

    private readonly long _clockStart = Stopwatch.GetTimestamp();
    private double _syntheticClock;
    private bool _useSyntheticClock;
    private bool _frozen;

    public PitchTracker()
    {
        _history = new PitchSample[(int)Math.Ceiling(HistorySeconds / HopSeconds) + 16];
    }

    /// <summary>Configures or resets the tracker for a newly opened microphone stream.</summary>
    public void Configure(int sampleRate, int channels, bool useSyntheticClock = false)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));

        lock (_lock)
        {
            _inputSampleRate = sampleRate;
            _channels = channels;
            // Decimate >50 kHz streams (96/192 kHz) back into 32..50 kHz to keep FFT & latency compact.
            _decimation = sampleRate > 50000 ? Math.Max(2, (int)Math.Round(sampleRate / 48000.0)) : 1;
            int effRate = sampleRate / _decimation;
            _decimAccum = 0;
            _decimCount = 0;
            double rc = 1.0 / (2.0 * Math.PI * 50.0);
            _hpAlpha = rc / (rc + 1.0 / effRate);
            _hpPrevX = 0;
            _hpPrevY = 0;

            _detector = new YinPitchDetector(effRate);
            int req = _detector.RequiredSamples;
            _ring = new float[req];
            _linear = new float[req];
            _ringPos = 0;
            _ringFilled = 0;
            _hopSamples = Math.Max(64, (int)Math.Round(effRate * HopSeconds));
            _sinceLastHop = 0;

            _chEnergy = new double[channels];
            _voicedStreak = 0;
            _unvoicedStreak = 0;
            _lastVoicedHz = null;
            _lastVoicedTime = -10;
            _latest = PitchReading.Silent;
            _historyHead = 0;
            _historyCount = 0;
            _useSyntheticClock = useSyntheticClock;
            _syntheticClock = 0;
            _frozen = false;
        }
    }

    public PitchReading Latest
    {
        get { lock (_lock) return _latest; }
    }

    /// <summary>Returns the latest reading, or Silent if no analysis frame arrived within <paramref name="maxAgeSeconds"/>.</summary>
    public PitchReading GetLiveReading(double maxAgeSeconds = 0.25)
    {
        lock (_lock)
        {
            if (!_latest.IsVoiced) return PitchReading.Silent;
            if (_useSyntheticClock) return _latest;
            double now = (Stopwatch.GetTimestamp() - _clockStart) / (double)Stopwatch.Frequency;
            return (now - _lastVoicedTime) <= maxAgeSeconds ? _latest : PitchReading.Silent;
        }
    }

    public double CurrentTimeSeconds
    {
        get
        {
            lock (_lock)
                return _useSyntheticClock ? _syntheticClock : (Stopwatch.GetTimestamp() - _clockStart) / (double)Stopwatch.Frequency;
        }
    }

    /// <summary>Feeds raw interleaved float samples from WASAPI (or unit test buffers).</summary>
    public void PushInterleaved(ReadOnlySpan<float> interleaved, int channels)
    {
        lock (_lock)
        {
            if (_frozen || _detector == null || channels <= 0) return;
            if (channels != _channels)
                Configure(_inputSampleRate, channels, _useSyntheticClock);

            int frames = interleaved.Length / channels;
            if (frames <= 0) return;

            // Update fast exponential per-channel energy (~0.25 s time constant) to auto-isolate single-channel mics.
            int activeCh = PickActiveChannel(interleaved, frames, channels);
            double frameStepSec = 1.0 / _inputSampleRate;

            for (int f = 0; f < frames; f++)
            {
                float sample;
                if (activeCh >= 0)
                {
                    sample = interleaved[f * channels + activeCh];
                }
                else
                {
                    float sum = 0;
                    int baseIdx = f * channels;
                    for (int c = 0; c < channels; c++) sum += interleaved[baseIdx + c];
                    sample = sum / channels;
                }

                if (_useSyntheticClock) _syntheticClock += frameStepSec;

                if (_decimation > 1)
                {
                    _decimAccum += sample;
                    if (++_decimCount < _decimation) continue;
                    sample = (float)(_decimAccum / _decimation);
                    _decimAccum = 0;
                    _decimCount = 0;
                }

                double filtered = _hpAlpha * (_hpPrevY + sample - _hpPrevX);
                _hpPrevX = sample;
                _hpPrevY = filtered;

                _ring[_ringPos] = (float)filtered;
                if (++_ringPos == _ring.Length) _ringPos = 0;
                if (_ringFilled < _ring.Length) _ringFilled++;

                if (++_sinceLastHop >= _hopSamples && _ringFilled == _ring.Length)
                {
                    _sinceLastHop = 0;
                    AnalyzeCurrentWindow();
                }
            }
        }
    }

    /// <summary>Notifies tracker of a silent buffer so the display decays smoothly to silent.</summary>
    public void PushSilence(int frames)
    {
        if (frames <= 0) return;
        lock (_lock)
        {
            if (_frozen || _detector == null) return;
            int effFrames = frames / _decimation;
            if (_useSyntheticClock) _syntheticClock += frames / (double)_inputSampleRate;
            for (int i = 0; i < effFrames; i++)
            {
                _ring[_ringPos] = 0;
                if (++_ringPos == _ring.Length) _ringPos = 0;
                if (_ringFilled < _ring.Length) _ringFilled++;
                if (++_sinceLastHop >= _hopSamples && _ringFilled == _ring.Length)
                {
                    _sinceLastHop = 0;
                    AnalyzeCurrentWindow();
                }
            }
        }
    }

    /// <summary>Copies the recent history within [now - windowSeconds, now] in chronological order.</summary>
    public void CopyHistory(List<PitchSample> dest, double windowSeconds, out double nowSeconds)
    {
        dest.Clear();
        lock (_lock)
        {
            nowSeconds = _useSyntheticClock
                ? _syntheticClock
                : (Stopwatch.GetTimestamp() - _clockStart) / (double)Stopwatch.Frequency;
            double cutoff = nowSeconds - windowSeconds;
            int start = (_historyHead - _historyCount + _history.Length) % _history.Length;
            for (int i = 0; i < _historyCount; i++)
            {
                var item = _history[(start + i) % _history.Length];
                if (item.TimeSeconds >= cutoff) dest.Add(item);
            }
        }
    }

    /// <summary>Injects synthetic pitch samples directly for deterministic UI snapshot verification.</summary>
    public void LoadSnapshotSamples(IReadOnlyList<PitchSample> samples, double nowSeconds)
    {
        lock (_lock)
        {
            _useSyntheticClock = true;
            _frozen = true;
            _syntheticClock = nowSeconds;
            _historyHead = 0;
            _historyCount = 0;
            _latest = PitchReading.Silent;
            foreach (var s in samples)
            {
                AppendHistory(s);
                _latest = s.Reading;
            }
        }
    }

    private int PickActiveChannel(ReadOnlySpan<float> interleaved, int frames, int channels)
    {
        if (channels == 1) return 0;

        // Measure chunk RMS^2 per channel and blend into smoothed tracker.
        double alpha = Math.Min(1.0, frames / (_inputSampleRate * 0.25));
        int bestCh = 0;
        double bestE = -1, secondE = -1;
        for (int c = 0; c < channels; c++)
        {
            double sum = 0;
            for (int f = 0; f < frames; f++)
            {
                float v = interleaved[f * channels + c];
                sum += v * v;
            }
            double meanSq = sum / frames;
            _chEnergy[c] = _chEnergy[c] * (1.0 - alpha) + meanSq * alpha;
            if (_chEnergy[c] > bestE) { secondE = bestE; bestE = _chEnergy[c]; bestCh = c; }
            else if (_chEnergy[c] > secondE) { secondE = _chEnergy[c]; }
        }

        // If one channel has >= 9x power (+9.5 dB, e.g. mic plugged into Scarlett Solo ch1 only), isolate it.
        if (bestE > 1e-8 && bestE >= secondE * 9.0) return bestCh;
        return -1; // balanced stereo -> average
    }

    private void AnalyzeCurrentWindow()
    {
        int n = _ring.Length;
        int tail = n - _ringPos;
        Array.Copy(_ring, _ringPos, _linear, 0, tail);
        if (_ringPos > 0) Array.Copy(_ring, 0, _linear, tail, _ringPos);

        double now = _useSyntheticClock
            ? _syntheticClock
            : (Stopwatch.GetTimestamp() - _clockStart) / (double)Stopwatch.Frequency;

        bool sustaining = _voicedStreak >= 2 && (now - _lastVoicedTime) < 0.14;
        double? priorHz = (now - _lastVoicedTime) < 0.14 ? _lastVoicedHz : null;

        var raw = _detector!.Detect(
            _linear,
            threshold: sustaining ? SustainThreshold : OnsetThreshold,
            rmsGate: sustaining ? SustainRmsGate : OnsetRmsGate,
            priorHz: priorHz);

        PitchReading emitted;
        if (raw.IsVoiced)
        {
            _unvoicedStreak = 0;
            _recentMidis[_voicedStreak % 3] = raw.MidiNote;
            _voicedStreak++;
            _lastVoicedHz = raw.FrequencyHz;
            _lastVoicedTime = now;

            if (_voicedStreak == 1)
            {
                // Require 2 consecutive voiced frames (~40 ms) to suppress brief percussive clicks/plosives.
                emitted = PitchReading.Silent;
            }
            else if (_voicedStreak == 2)
            {
                // Ensure the first two onset frames agree within 3 semitones before lighting up.
                if (Math.Abs(_recentMidis[1] - _recentMidis[0]) <= 3.0)
                    emitted = raw;
                else
                {
                    _voicedStreak = 1;
                    _recentMidis[0] = raw.MidiNote;
                    emitted = PitchReading.Silent;
                }
            }
            else
            {
                // 3-point median eliminates single-frame octave spikes without smearing 5-7 Hz vibrato.
                double medMidi = Median3(_recentMidis[0], _recentMidis[1], _recentMidis[2]);
                double medHz = PitchMath.MidiToHz(medMidi);
                emitted = PitchMath.FromFrequency(medHz, raw.Clarity, raw.Rms);
            }
        }
        else
        {
            _unvoicedStreak++;
            if (_unvoicedStreak >= 2)
            {
                _voicedStreak = 0;
                emitted = PitchReading.Silent;
            }
            else
            {
                // Hold through a single-frame (20 ms) breath/consonant dip.
                emitted = _latest;
            }
        }

        _latest = emitted;
        AppendHistory(new PitchSample(now, emitted));
    }

    private void AppendHistory(PitchSample sample)
    {
        _history[_historyHead] = sample;
        _historyHead = (_historyHead + 1) % _history.Length;
        if (_historyCount < _history.Length) _historyCount++;
    }

    private static double Median3(double a, double b, double c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return b;
    }
}
