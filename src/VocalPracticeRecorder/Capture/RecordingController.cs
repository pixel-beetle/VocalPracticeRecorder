using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VocalPracticeRecorder.Core;

#pragma warning disable CS0618 // WasapiOut is fine for a silence keep-alive stream

namespace VocalPracticeRecorder.Capture;

public sealed record AudioDeviceItem(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class AudioDevices
{
    public static List<AudioDeviceItem> List(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDeviceItem(d.ID, d.FriendlyName)).ToList();
    }

    public static string? DefaultId(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        return en.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var d) ? d.ID : null;
    }

    public static MMDevice Get(string id)
    {
        using var en = new MMDeviceEnumerator();
        return en.GetDevice(id);
    }
}

/// <summary>Owns the live audio devices (for meters) and runs one recording session at a time.</summary>
public sealed class RecordingController : IDisposable
{
    private AudioTrackRecorder? _mic;
    private AudioTrackRecorder? _loopback;
    private WasapiOut? _silence;
    private ScreenCapturer? _video;
    private SessionInfo? _session;
    private long _origin;

    public bool IsRecording => _session != null;
    public SessionInfo? Current => _session;
    public double Elapsed => _session == null ? 0 : (Stopwatch.GetTimestamp() - _origin) / (double)Stopwatch.Frequency;

    public float TakeMicPeak() => _mic?.TakePeak() ?? 0;
    public float TakeBackingPeak() => _loopback?.TakePeak() ?? 0;
    public WaveFormat? MicFormat => _mic?.WaveFormat;
    public bool DevicesOpen => _mic != null && _loopback != null;

    public void OpenDevices(string micId, string outputId)
    {
        if (IsRecording) throw new InvalidOperationException("录制中不能切换设备。");
        CloseDevices();
        var micDev = AudioDevices.Get(micId);
        var outDev = AudioDevices.Get(outputId);
        _mic = new AudioTrackRecorder(micDev, loopback: false);
        _loopback = new AudioTrackRecorder(outDev, loopback: true);

        // WASAPI loopback delivers no packets while nothing is playing; rendering silence keeps
        // the loopback clock running so the backing track never shrinks.
        try
        {
            var silenceDev = AudioDevices.Get(outputId);
            _silence = new WasapiOut(silenceDev, AudioClientShareMode.Shared, true, 100);
            using (var client = silenceDev.CreateAudioClient())
                _silence.Init(new SilenceProvider(client.MixFormat));
            _silence.Play();
        }
        catch { _silence?.Dispose(); _silence = null; }

        _mic.StartMonitoring();
        _loopback.StartMonitoring();
    }

    public void CloseDevices()
    {
        _mic?.Dispose(); _mic = null;
        _loopback?.Dispose(); _loopback = null;
        try { _silence?.Stop(); } catch { }
        _silence?.Dispose(); _silence = null;
    }

    public SessionInfo Start(PixelRect region, string windowTitle, string recordingsDir, int fps, int maxHeight)
    {
        if (_mic == null || _loopback == null) throw new InvalidOperationException("请先选择音频设备。");
        if (IsRecording) throw new InvalidOperationException("已经在录制。");

        var now = DateTime.Now;
        string id = now.ToString("yyyyMMdd_HHmmss");
        Directory.CreateDirectory(recordingsDir);
        string folder = Path.Combine(recordingsDir, id);
        for (int i = 2; Directory.Exists(folder); i++) folder = Path.Combine(recordingsDir, $"{id}_{i}");
        Directory.CreateDirectory(folder);

        var s = new SessionInfo
        {
            Id = Path.GetFileName(folder),
            Folder = folder,
            CreatedAt = now,
            WindowTitle = windowTitle,
            SongTitle = TitleCleaner.Clean(windowTitle),
            Incomplete = true,
            Mic = new TrackInfo
            {
                File = "mic.wav", DeviceName = _mic.DeviceName, DeviceId = _mic.DeviceId,
                SampleRate = _mic.WaveFormat.SampleRate, Channels = _mic.WaveFormat.Channels,
            },
            Backing = new TrackInfo
            {
                File = "backing.wav", DeviceName = _loopback.DeviceName, DeviceId = _loopback.DeviceId,
                SampleRate = _loopback.WaveFormat.SampleRate, Channels = _loopback.WaveFormat.Channels,
            },
            Video = new VideoInfo { File = "video.mp4", Fps = fps },
        };
        s.Save();

        _origin = Stopwatch.GetTimestamp();
        _mic.BeginFile(s.PathOf(s.Mic.File), _origin);
        _loopback.BeginFile(s.PathOf(s.Backing.File), _origin);
        _video = new ScreenCapturer(region, fps, maxHeight, s.PathOf(s.Video.File), _origin);
        try
        {
            _video.Start();
        }
        catch
        {
            _mic.EndFile();
            _loopback.EndFile();
            _video.Dispose();
            _video = null;
            try { Directory.Delete(folder, true); } catch { }
            throw;
        }
        _session = s;
        return s;
    }

    /// <summary>Stops capture and returns the session with raw files and timing filled in.</summary>
    public async Task<SessionInfo> StopAsync()
    {
        var s = _session ?? throw new InvalidOperationException("没有在录制。");
        var video = _video!;
        await video.StopAsync();
        _mic!.EndFile();
        _loopback!.EndFile();
        _session = null;
        _video = null;

        s.Video.StartOffset = video.StartOffset;
        s.Video.FrameCount = video.FramesWritten;
        s.Video.Width = video.OutputWidth;
        s.Video.Height = video.OutputHeight;
        s.Mic.StartOffset = double.IsNaN(_mic.StartOffset) ? video.StartOffset : _mic.StartOffset;
        s.Backing.StartOffset = double.IsNaN(_loopback.StartOffset) ? video.StartOffset : _loopback.StartOffset;
        s.Save();
        video.Dispose();

        if (video.Error != null) throw video.Error;
        if (double.IsNaN(_mic.StartOffset)) throw new InvalidOperationException("麦克风没有收到任何音频数据，请检查输入设备。");
        return s;
    }

    /// <summary>Stops and deletes the current recording.</summary>
    public async Task DiscardAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await StopAsync(); } catch { }
        try { Directory.Delete(s.Folder, true); } catch { }
    }

    public void Dispose()
    {
        _video?.Dispose();
        CloseDevices();
    }
}
