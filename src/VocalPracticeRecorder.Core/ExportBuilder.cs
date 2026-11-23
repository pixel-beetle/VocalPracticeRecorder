using System.Globalization;
using System.Text;

namespace VocalPracticeRecorder.Core;

public enum ExportKind
{
    /// <summary>MP4: lyrics video + vocal/backing mix (AAC 320k).</summary>
    MixVideo,
    /// <summary>MP3 320k: vocal/backing mix.</summary>
    MixAudio,
    /// <summary>MP3 320k mono: vocal only (dry).</summary>
    DryVocal,
}

public sealed class ExportSettings
{
    public int VideoCrf { get; set; } = 28;
    public string VideoPreset { get; set; } = "medium";
    public int AudioBitrateK { get; set; } = 320;
    public double LimiterCeiling { get; set; } = 0.97;
}

/// <summary>Builds the single FFmpeg invocation that renders an edited session.</summary>
public static class ExportBuilder
{
    public static string Extension(ExportKind kind) => kind == ExportKind.MixVideo ? ".mp4" : ".mp3";

    public static string KindLabel(ExportKind kind) => kind switch
    {
        ExportKind.MixVideo => "视频",
        ExportKind.MixAudio => "混音",
        ExportKind.DryVocal => "干声",
        _ => kind.ToString(),
    };

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Pan expression selecting the mic channel, producing <paramref name="layout"/>.</summary>
    public static string MicPan(MicChannel ch, int sourceChannels, string layout)
    {
        string src = sourceChannels <= 1 ? "c0" : ch switch
        {
            MicChannel.Left => "c0",
            MicChannel.Right => "c1",
            _ => "0.5*c0+0.5*c1",
        };
        return layout == "mono" ? $"pan=mono|c0={src}" : $"pan=stereo|c0={src}|c1={src}";
    }

    /// <summary>
    /// Filter chain that takes a track placed at <paramref name="placement"/> seconds on the timeline
    /// and returns exactly the [trimStart, trimEnd) window of it (silence-padded where the track is absent).
    /// </summary>
    public static string PlaceAndTrim(double placement, double trimStart, double trimEnd)
    {
        double length = trimEnd - trimStart;
        double srcStart = trimStart - placement;
        double srcEnd = trimEnd - placement;
        var sb = new StringBuilder();
        if (srcStart >= 0)
        {
            sb.Append($"atrim=start={F(srcStart)}:end={F(srcEnd)},asetpts=PTS-STARTPTS");
        }
        else
        {
            long delayMs = (long)Math.Round(-srcStart * 1000);
            sb.Append($"atrim=start=0:end={F(Math.Max(0.001, srcEnd))},asetpts=PTS-STARTPTS,adelay=delays={delayMs}:all=1");
        }
        sb.Append($",apad=whole_dur={F(length)},atrim=end={F(length)}");
        return sb.ToString();
    }

    public static string Volume(double db) => $"volume={F(db)}dB";

    /// <summary>Fade applied at internal joins so deleting a section never clicks.</summary>
    public const double JoinFadeSeconds = 0.005;

    /// <summary>Returns the full FFmpeg argument list (without the executable).</summary>
    public static List<string> BuildArgs(SessionInfo s, ExportKind kind, string outputPath, ExportSettings? settings = null)
    {
        settings ??= new ExportSettings();
        var e = s.Edit;
        var segs = EditTimeline.KeptSegments(s);
        if (segs.Count == 0 || segs.Sum(x => x.Length) < 0.1) throw new InvalidOperationException("剪辑后的长度太短。");
        int n = segs.Count;

        var args = new List<string> { "-hide_banner", "-y", "-nostdin" };
        int idx = 0;
        int videoIdx = -1;
        if (kind == ExportKind.MixVideo)
        {
            args.AddRange(["-i", s.PathOf(s.Video.File)]);
            videoIdx = idx++;
        }
        args.AddRange(["-i", s.PathOf(s.Mic.File)]);
        int micIdx = idx++;
        bool useBacking = kind != ExportKind.DryVocal && !e.BackingMuted;
        int backIdx = -1;
        if (useBacking)
        {
            args.AddRange(["-i", s.PathOf(s.Backing.File)]);
            backIdx = idx++;
        }

        var fg = new List<string>();
        string layout = kind == ExportKind.DryVocal ? "mono" : "stereo";
        string Labels(string p) => string.Concat(Enumerable.Range(0, n).Select(i => $"[{p}{i}]"));

        // Shared pre-processing, then one branch per kept segment.
        fg.Add($"[{micIdx}:a]{MicPan(e.MicChannel, s.Mic.Channels, layout)},aresample=48000,{Volume(e.VocalGainDb)},asplit={n}{Labels("vi")}");
        if (useBacking)
            fg.Add($"[{backIdx}:a]aformat=channel_layouts=stereo,aresample=48000,{Volume(e.BackingGainDb)},asplit={n}{Labels("bi")}");
        if (videoIdx >= 0)
            fg.Add($"[{videoIdx}:v]split={n}{Labels("xi")}");

        int fps = Math.Max(1, s.Video.Fps);
        for (int i = 0; i < n; i++)
        {
            var (a, b) = (segs[i].Start, segs[i].End);
            double len = b - a;
            var fades = new List<string>();
            if (i > 0) fades.Add($"afade=t=in:d={F(JoinFadeSeconds)}");
            if (i < n - 1) fades.Add($"afade=t=out:st={F(len - JoinFadeSeconds)}:d={F(JoinFadeSeconds)}");
            string fade = fades.Count > 0 ? "," + string.Join(",", fades) : "";
            fg.Add($"[vi{i}]{PlaceAndTrim(s.VocalPlacement, a, b)}[vs{i}]");
            if (useBacking)
            {
                fg.Add($"[bi{i}]{PlaceAndTrim(s.BackingPlacement, a, b)}[bs{i}]");
                fg.Add($"[vs{i}][bs{i}]amix=inputs=2:duration=longest:normalize=0{fade}[as{i}]");
            }
            else fg.Add($"[vs{i}]anull{fade}[as{i}]");
            if (videoIdx >= 0)
            {
                // Frame indices are exact because every boundary is snapped to the frame grid.
                long f0 = (long)Math.Round(a * fps), f1 = (long)Math.Round(b * fps);
                fg.Add($"[xi{i}]trim=start_frame={f0}:end_frame={f1},setpts=PTS-STARTPTS[xs{i}]");
            }
        }

        string limiter = $"alimiter=limit={F(settings.LimiterCeiling)}:level=false:latency=true";
        if (videoIdx >= 0)
        {
            string pairs = string.Concat(Enumerable.Range(0, n).Select(i => $"[xs{i}][as{i}]"));
            fg.Add($"{pairs}concat=n={n}:v=1:a=1[vout][acat]");
        }
        else
        {
            fg.Add($"{Labels("as")}concat=n={n}:v=0:a=1[acat]");
        }
        fg.Add($"[acat]{limiter}[aout]");
        args.AddRange(["-filter_complex", string.Join(";", fg)]);

        if (videoIdx >= 0)
        {
            args.AddRange(["-map", "[vout]", "-map", "[aout]",
                "-c:v", "libx264", "-preset", settings.VideoPreset, "-crf", settings.VideoCrf.ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", $"{settings.AudioBitrateK}k", "-ar", "48000",
                "-movflags", "+faststart"]);
        }
        else
        {
            args.AddRange(["-map", "[aout]", "-c:a", "libmp3lame", "-b:a", $"{settings.AudioBitrateK}k", "-ar", "48000"]);
        }
        args.Add("-progress");
        args.Add("pipe:1");
        args.Add(outputPath);
        return args;
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.');
        if (s.Length > 80) s = s[..80].Trim();
        return string.IsNullOrEmpty(s) ? "export" : s;
    }

    public static string DefaultFileName(SessionInfo s, int takeNumber, ExportKind kind) =>
        SanitizeFileName($"{s.SongTitle} Take{takeNumber} {s.CreatedAt:MMdd-HHmm} {KindLabel(kind)}") + Extension(kind);
}
