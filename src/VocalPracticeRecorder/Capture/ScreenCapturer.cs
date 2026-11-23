using System.Diagnostics;
using System.Text;
using Vortice.DXGI;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Capture;

public sealed record MonitorInfo(int AdapterIndex, int OutputIndex, string Name, PixelRect Bounds);

/// <summary>
/// Captures a desktop region and pipes constant-frame-rate raw frames to FFmpeg.
/// Frame k is always the desktop as of (StartOffset + k / fps) on the session clock,
/// duplicating or dropping frames as needed, so video stays locked to the audio clock.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    /// <summary>Test hook: use a synthetic pattern instead of the desktop.</summary>
    internal static bool ForceSynthetic { get; set; }

    private readonly PixelRect _region;
    private readonly int _fps;
    private readonly int _maxHeight;
    private readonly string _outputPath;
    private readonly long _clockOrigin;
    private Thread? _thread;
    private volatile bool _stop;
    private Process? _ffmpeg;
    private readonly StringBuilder _ffmpegLog = new();

    public double StartOffset { get; private set; }
    public long FramesWritten { get; private set; }
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public string SourceName { get; private set; } = "";
    public Exception? Error { get; private set; }

    /// <param name="region">Physical desktop coordinates.</param>
    /// <param name="clockOrigin">Stopwatch timestamp that defines t = 0 for the session.</param>
    public ScreenCapturer(PixelRect region, int fps, int maxHeight, string outputPath, long clockOrigin)
    {
        _region = region;
        _fps = fps;
        _maxHeight = maxHeight;
        _outputPath = outputPath;
        _clockOrigin = clockOrigin;
    }

    public static List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        if (!d.AttachedToDesktop) continue;
                        var c = d.DesktopCoordinates;
                        list.Add(new MonitorInfo((int)a, (int)o, d.DeviceName,
                            new PixelRect(c.Left, c.Top, c.Right - c.Left, c.Bottom - c.Top)));
                    }
                }
            }
        }
        return list;
    }

    private double Now => (Stopwatch.GetTimestamp() - _clockOrigin) / (double)Stopwatch.Frequency;

    private static IFrameSource CreateSource(MonitorInfo monitor, PixelRect region)
    {
        if (ForceSynthetic) return new SyntheticFrameSource(region.Width, region.Height);
        try
        {
            return new DxgiFrameSource(monitor, region);
        }
        catch (Exception dxgiError)
        {
            try
            {
                return new GdiFrameSource(region);
            }
            catch (Exception gdiError)
            {
                throw new InvalidOperationException(
                    $"无法读取屏幕画面。\nDXGI：{dxgiError.Message.Trim()}\nGDI：{gdiError.Message.Trim()}", dxgiError);
            }
        }
    }

    /// <summary>Starts capture. Throws synchronously if the capture or encoder cannot be set up.</summary>
    public void Start()
    {
        var monitors = EnumerateMonitors();
        int mi = RegionMath.PickMonitor(_region, monitors.Select(m => m.Bounds).ToList());
        if (mi < 0) throw new InvalidOperationException("选区不在任何显示器上。");
        var monitor = monitors[mi];
        var region = RegionMath.ClampToMonitor(_region, monitor.Bounds);
        if (region.IsEmpty) throw new InvalidOperationException("选区太小。");

        (OutputWidth, OutputHeight) = RegionMath.OutputSize(region.Width, region.Height, _maxHeight);

        var ready = new ManualResetEventSlim();
        Exception? startError = null;
        _thread = new Thread(() =>
        {
            try { Run(monitor, region, ready); }
            catch (Exception ex)
            {
                if (!ready.IsSet) { startError = ex; ready.Set(); }
                else Error = ex;
            }
        })
        { IsBackground = true, Name = "ScreenCapture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait();
        if (startError != null) throw startError;
    }

    private Process StartEncoder(int w, int h)
    {
        var args = new List<string>
        {
            "-hide_banner", "-y", "-nostdin",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-framerate", _fps.ToString(), "-i", "pipe:0",
        };
        if (OutputWidth != w || OutputHeight != h)
            args.AddRange(["-vf", $"scale={OutputWidth}:{OutputHeight}:flags=bicubic"]);
        // Intermediate quality: it is re-encoded once on export.
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-g", (_fps * 2).ToString(), "-movflags", "+faststart", _outputPath]);
        var psi = Ffmpeg.StartInfo(args, redirectStdin: true, redirectStdout: false);
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_ffmpegLog) { _ffmpegLog.AppendLine(e.Data); if (_ffmpegLog.Length > 8000) _ffmpegLog.Remove(0, 4000); }
        };
        p.Start();
        p.BeginErrorReadLine();
        return p;
    }

    private void Run(MonitorInfo monitor, PixelRect region, ManualResetEventSlim ready)
    {
        using var source = CreateSource(monitor, region);
        SourceName = source.Name;
        int w = region.Width, h = region.Height;
        var frame = new byte[w * h * 4];

        // Prime with the current desktop image before declaring t0 for the video.
        for (int i = 0; i < 10; i++) if (source.TryGrab(frame, 100)) break;

        _ffmpeg = StartEncoder(w, h);
        var stdin = _ffmpeg.StandardInput.BaseStream;
        double start = Now;
        StartOffset = start;
        ready.Set();

        long k = 0;
        try
        {
            while (!_stop)
            {
                double now = Now;
                double due = start + k / (double)_fps;
                if (now >= due)
                {
                    // Emit every frame whose slot has passed (duplicates keep CFR aligned to the clock).
                    while (start + k / (double)_fps <= now)
                    {
                        stdin.Write(frame, 0, frame.Length);
                        k++;
                    }
                    FramesWritten = k;
                    continue;
                }
                int wait = (int)Math.Ceiling((due - now) * 1000);
                source.TryGrab(frame, Math.Clamp(wait, 1, 100));
            }
            // Fill up to the exact stop time.
            double end = Now;
            while (start + k / (double)_fps <= end)
            {
                stdin.Write(frame, 0, frame.Length);
                k++;
            }
            FramesWritten = k;
        }
        finally
        {
            try { stdin.Flush(); stdin.Close(); } catch { }
        }
    }

    public string EncoderLog { get { lock (_ffmpegLog) return _ffmpegLog.ToString(); } }

    public async Task StopAsync()
    {
        _stop = true;
        if (_thread != null) await Task.Run(() => _thread.Join());
        if (_ffmpeg != null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await _ffmpeg.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { _ffmpeg.Kill(true); } catch { } }
            if (_ffmpeg.HasExited && _ffmpeg.ExitCode != 0 && Error == null)
                Error = new InvalidOperationException("视频编码失败：\n" + EncoderLog);
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { if (_ffmpeg is { HasExited: false }) _ffmpeg.Kill(true); } catch { }
        _ffmpeg?.Dispose();
    }
}
