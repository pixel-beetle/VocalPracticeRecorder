using System.Globalization;
using System.IO;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder;

/// <summary>Generates realistic-looking demo sessions with FFmpeg (used by --snapshot demo).</summary>
internal static class DemoData
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // Music-like backing: bass + chord + overtones with a 2 Hz beat envelope and noise hi-hats; 2 s intro.
    private const string Backing =
        "if(gt(t,2)*lt(t,58), (0.35*sin(2*PI*110*t)+0.2*sin(2*PI*277*t)+0.2*sin(2*PI*330*t)+0.08*sin(2*PI*1320*t))*(0.4+0.6*exp(-6*mod(t,0.5)))+0.12*(2*random(0)-1)*exp(-40*mod(t,0.25)), 0)";

    private static string Voice(double shift)
    {
        string T = $"(t-{F(shift)})";
        string phase = $"2*PI*(392+30*sin(2*PI*0.9*{T}))*{T}";
        return $"if(gt({T},4)*lt({T},56)*gt(sin(2*PI*0.18*{T}),-0.3), (0.45*sin({phase})+0.15*sin(2*{phase})+0.06*sin(3*{phase}))*(0.6+0.4*sin(2*PI*0.4*{T})), 0)";
    }

    public static async Task<string> CreateAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "vpr-demo-" + DateTime.Now.ToString("HHmmss"));
        string rec = Path.Combine(root, "Recordings");
        var now = DateTime.Now;
        var songs = new (string Title, int Takes, double Delay)[]
        {
            ("【ニコカラ】アイドル（On Vocal）_哔哩哔哩_bilibili - 个人 - Microsoft​ Edge", 3, 0.080),
            ("【カラオケ】夜に駆ける / YOASOBI_哔哩哔哩_bilibili - Google Chrome", 1, 0.0),
        };
        int n = 0;
        for (int si = 0; si < songs.Length; si++)
        {
            var (title, takes, delay) = songs[si];
            for (int t = 0; t < takes; t++, n++)
            {
                var s = new SessionInfo
                {
                    Id = $"demo{n}",
                    CreatedAt = now.AddDays(si - songs.Length).AddMinutes(t * 7),
                    WindowTitle = title,
                    SongTitle = TitleCleaner.Clean(title),
                    Folder = Path.Combine(rec, $"demo{n}"),
                    Video = new VideoInfo { File = "video.mp4", Fps = 20, FrameCount = 60 * 20, Width = 1280, Height = 720, StartOffset = 0.15 },
                    Mic = new TrackInfo { File = "mic.flac", SampleRate = 48000, Channels = 2, StartOffset = 0.02, DeviceName = "Focusrite USB (Analogue 1 + 2)", DeviceId = "mic" },
                    Backing = new TrackInfo { File = "backing.flac", SampleRate = 48000, Channels = 2, StartOffset = 0.03, DeviceName = "Focusrite USB", DeviceId = "out" },
                };
                Directory.CreateDirectory(s.Folder);
                await Ffmpeg.RunAsync(["-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=20:duration=60",
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", s.PathOf("video.mp4")]);
                await Ffmpeg.RunAsync(["-hide_banner", "-y", "-f", "lavfi", "-i", $"aevalsrc='{Backing}|{Backing}':s=48000:d=60",
                    "-c:a", "flac", s.PathOf("backing.flac")]);

                // Mic (Scarlett-style: everything on input 1 / left): voice + room bleed of the real backing file.
                // Timeline delay = mic-side delay − (backing start − mic start) = adelay − 10 ms.
                string voiceSrc = $"aevalsrc='{Voice(t * 0.3)}':s=48000:d=60";
                string graph = delay > 0
                    ? $"[1:a]pan=mono|c0=0.5*c0+0.5*c1,adelay={F((delay + 0.01) * 1000)},volume=0.08[b];[0:a][b]amix=inputs=2:normalize=0:duration=first,pan=stereo|c0=c0|c1=0.001*c0[m]"
                    : "[0:a]pan=stereo|c0=c0|c1=0.001*c0[m]";
                await Ffmpeg.RunAsync(["-hide_banner", "-y", "-f", "lavfi", "-i", voiceSrc, "-i", s.PathOf("backing.flac"),
                    "-filter_complex", graph, "-map", "[m]", "-c:a", "flac", s.PathOf("mic.flac")]);

                var audio = await SessionAudio.LoadAsync(s);
                s.Edit.MicChannel = ChannelSelector.Detect(audio.MicInterleaved, audio.MicChannels);
                var est = SessionPostProcessor.AutoAlign(s, audio, null);
                Log.Info($"demo {s.Id}: expected delay {delay * 1000:0}ms -> est {est.DelaySeconds * 1000:0.0}ms support={est.Confidence:0}% bands={est.BandsAgreeing} reliable={est.IsReliable} ch={s.Edit.MicChannel}");
                if (t == 1) { s.Edit.TrimStart = 1.6; s.Edit.TrimEnd = 57.5; s.Starred = true; }
                await SessionPostProcessor.MakeThumbnailAsync(s);
                s.Save();
            }
        }
        return root;
    }
}
