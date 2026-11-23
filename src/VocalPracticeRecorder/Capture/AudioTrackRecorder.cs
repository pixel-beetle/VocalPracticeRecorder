using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VocalPracticeRecorder.Capture;

/// <summary>
/// One WASAPI capture stream (mic or loopback). Writes a WAV file when recording, otherwise only meters.
/// Uses the packet QPC timestamp to know exactly when the first sample was captured, and the device
/// position to fill gaps with silence so the file stays sample-accurate against the wall clock.
/// </summary>
public sealed class AudioTrackRecorder : IDisposable
{
    private readonly WasapiRecorder _recorder;
    private readonly object _lock = new();
    private WaveFileWriter? _writer;
    private long _clockOrigin;
    private long _firstDevicePos = -1;
    private long _framesWritten;
    private float _peak;

    public WaveFormat WaveFormat => _recorder.WaveFormat;
    public string DeviceName { get; }
    public string DeviceId { get; }
    public bool IsLoopback { get; }
    /// <summary>Seconds from session origin to the first written sample; NaN until the first packet.</summary>
    public double StartOffset { get; private set; } = double.NaN;
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public long GapFramesFilled { get; private set; }
    public Exception? Error { get; private set; }

    public AudioTrackRecorder(MMDevice device, bool loopback)
    {
        DeviceName = device.FriendlyName;
        DeviceId = device.ID;
        IsLoopback = loopback;
        _recorder = Build(device, loopback, raw: !loopback) ?? Build(device, loopback, raw: false)
            ?? throw new InvalidOperationException($"无法打开音频设备：{device.FriendlyName}");
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) => { if (e.Exception != null) Error = e.Exception; };
    }

    private static WasapiRecorder? Build(MMDevice device, bool loopback, bool raw)
    {
        try
        {
            var b = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithBufferLength(100);
            if (loopback) b = b.WithLoopbackCapture();
            else if (raw) b = b.WithRawMode(); // bypass Windows "enhancements" on the vocal
            return b.Build();
        }
        catch
        {
            return null;
        }
    }

    public void StartMonitoring() => _recorder.StartRecording();

    /// <summary>Starts writing to <paramref name="path"/>; samples before this call are discarded.</summary>
    public void BeginFile(string path, long clockOrigin)
    {
        lock (_lock)
        {
            _clockOrigin = clockOrigin;
            _firstDevicePos = -1;
            _framesWritten = 0;
            StartOffset = double.NaN;
            _writer = new WaveFileWriter(path, _recorder.WaveFormat);
        }
    }

    public void EndFile()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Peak level since the last call, 0..1.</summary>
    public float TakePeak()
    {
        float p = _peak;
        _peak = 0;
        return p;
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        var fmt = _recorder.WaveFormat;
        int block = fmt.BlockAlign;
        int frames = buffer.Length / block;
        bool silent = (flags & AudioClientBufferFlags.Silent) != 0;

        bool isFloat = fmt.BitsPerSample == 32 &&
                       (fmt.Encoding == WaveFormatEncoding.IeeeFloat || fmt.Encoding == WaveFormatEncoding.Extensible);
        if (!silent && isFloat)
        {
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer);
            float max = _peak;
            for (int i = 0; i < floats.Length; i++)
            {
                float v = Math.Abs(floats[i]);
                if (v > max) max = v;
            }
            _peak = max;
        }

        lock (_lock)
        {
            if (_writer == null) return;
            if (_firstDevicePos < 0)
            {
                _firstDevicePos = devicePosition;
                // qpcPosition is in 100 ns units; convert our Stopwatch origin to the same base.
                double qpcSeconds = qpcPosition / 1e7;
                double originSeconds = _clockOrigin / (double)Stopwatch.Frequency;
                StartOffset = qpcSeconds - originSeconds;
            }
            else
            {
                long expected = _firstDevicePos + _framesWritten;
                long gap = devicePosition - expected;
                // Fill dropouts / loopback idle periods with silence (cap at 10 s to ignore bogus positions).
                if (gap > 0 && gap < fmt.SampleRate * 10L)
                {
                    WriteSilence(gap, block);
                    GapFramesFilled += gap;
                }
            }

            if (silent) WriteSilence(frames, block);
            else
            {
                _writer.Write(buffer);
                Interlocked.Add(ref _framesWritten, frames);
            }
        }
    }

    private readonly byte[] _zeros = new byte[64 * 1024];

    private void WriteSilence(long frames, int block)
    {
        long bytes = frames * block;
        while (bytes > 0)
        {
            int n = (int)Math.Min(bytes, _zeros.Length);
            _writer!.Write(_zeros, 0, n);
            bytes -= n;
        }
        Interlocked.Add(ref _framesWritten, frames);
    }

    public void Dispose()
    {
        try { _recorder.StopRecording(); } catch { }
        EndFile();
        _recorder.Dispose();
    }
}
