using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VocalPracticeRecorder.Core;

public static class Ffmpeg
{
    private static string? _path;

    /// <summary>Locates ffmpeg.exe: next to the app, in ./ffmpeg/, then on PATH.</summary>
    public static string Path
    {
        get
        {
            if (_path != null) return _path;
            var baseDir = AppContext.BaseDirectory;
            foreach (var candidate in new[]
                     {
                         System.IO.Path.Combine(baseDir, "ffmpeg.exe"),
                         System.IO.Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
                     })
            {
                if (File.Exists(candidate)) return _path = candidate;
            }
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var p = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) return _path = p;
                }
                catch { }
            }
            throw new FileNotFoundException("找不到 ffmpeg.exe。请把 ffmpeg.exe 放到程序目录，或安装后加入 PATH。");
        }
    }

    public static bool IsAvailable
    {
        get
        {
            try { _ = Path; return true; } catch { return false; }
        }
    }

    public static ProcessStartInfo StartInfo(IEnumerable<string> args, bool redirectStdin = false, bool redirectStdout = true)
    {
        var psi = new ProcessStartInfo(Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardInput = redirectStdin,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (redirectStdout) psi.StandardOutputEncoding = Encoding.UTF8;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// Runs ffmpeg to completion. When the args contain "-progress pipe:1", <paramref name="progress"/>
    /// receives processed seconds.
    /// </summary>
    public static async Task RunAsync(IEnumerable<string> args, Action<double>? progress = null, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = StartInfo(args) };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) { err.AppendLine(e.Data); if (err.Length > 20000) err.Remove(0, 10000); } };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || progress == null) return;
            // out_time_us=12345678
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(e.Data.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
            {
                progress(us / 1_000_000.0);
            }
        };
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
        {
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string tail;
            lock (err) tail = err.ToString();
            if (tail.Length > 1500) tail = tail[^1500..];
            throw new InvalidOperationException($"FFmpeg 失败（{p.ExitCode}）：\n{tail}");
        }
    }

    /// <summary>Decodes any audio file to interleaved float32 at its native layout and the given rate.</summary>
    public static async Task<float[]> DecodeAudioAsync(string file, int sampleRate, int channels, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-i", file, "-vn",
            "-f", "f32le", "-acodec", "pcm_f32le",
            "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", channels.ToString(CultureInfo.InvariantCulture),
            "pipe:1",
        };
        var psi = StartInfo(args);
        psi.StandardOutputEncoding = null;
        using var p = new Process { StartInfo = psi };
        p.Start();
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        var errText = await errTask.ConfigureAwait(false);
        if (p.ExitCode != 0) throw new InvalidOperationException($"解码失败：{file}\n{errText[^Math.Min(800, errText.Length)..]}");
        var bytes = ms.GetBuffer();
        int count = (int)(ms.Length / 4);
        var result = new float[count];
        Buffer.BlockCopy(bytes, 0, result, 0, count * 4);
        return result;
    }
}
