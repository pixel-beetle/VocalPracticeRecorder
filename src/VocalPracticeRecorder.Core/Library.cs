namespace VocalPracticeRecorder.Core;

public sealed class SongGroup
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>Takes in chronological order; Take number = index + 1.</summary>
    public List<SessionInfo> Takes { get; init; } = [];
    public DateTime LatestAt => Takes.Count == 0 ? DateTime.MinValue : Takes.Max(t => t.CreatedAt);
    public int TakeNumberOf(SessionInfo s) => Takes.IndexOf(s) + 1;
}

public static class Library
{
    /// <summary>Groups takes by song; newest-practised song first, takes chronological inside.</summary>
    public static List<SongGroup> Group(IEnumerable<SessionInfo> sessions) =>
        sessions
            .GroupBy(s => TitleCleaner.GroupKey(s.SongTitle))
            .Select(g =>
            {
                var takes = g.OrderBy(s => s.CreatedAt).ToList();
                return new SongGroup { Key = g.Key, Title = takes[^1].SongTitle, Takes = takes };
            })
            .OrderByDescending(g => g.LatestAt)
            .ToList();

    public static List<SessionInfo> LoadAll(string recordingsRoot)
    {
        var list = new List<SessionInfo>();
        if (!Directory.Exists(recordingsRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(recordingsRoot))
        {
            if (!File.Exists(Path.Combine(dir, SessionInfo.FileName))) continue;
            try { list.Add(SessionInfo.Load(dir)); }
            catch { /* skip corrupt sessions */ }
        }
        return list;
    }

    /// <summary>Human readable tags shown under each take.</summary>
    public static List<string> Tags(SessionInfo s)
    {
        var tags = new List<string>();
        if (s.Starred) tags.Add("最佳");
        tags.Add(FormatDuration(s.Duration));
        switch (s.Edit.AlignMode)
        {
            case AlignMode.Auto: tags.Add($"自动对齐 {s.Edit.VocalOffsetMs:+0;-0;0}ms"); break;
            case AlignMode.Remembered: tags.Add($"沿用 {s.Edit.VocalOffsetMs:+0;-0;0}ms"); break;
            case AlignMode.Calibrated: tags.Add($"设备延迟 {s.Edit.VocalOffsetMs:+0;-0;0}ms"); break;
            case AlignMode.Manual: tags.Add($"手动 {s.Edit.VocalOffsetMs:+0;-0;0}ms"); break;
        }
        bool trimmed = s.Edit.TrimStart > 0.05 || (s.Edit.TrimEnd > 0 && s.Edit.TrimEnd < s.Duration - 0.05);
        if (trimmed) tags.Add($"已剪 → {FormatDuration(s.EffectiveTrimEnd - s.Edit.TrimStart)}");
        if (s.Exports.Count > 0) tags.Add("已导出");
        if (s.Incomplete) tags.Add("未完成");
        return tags;
    }

    public static string FormatDuration(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static string FormatTimecode(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }
}

public static class ChannelSelector
{
    /// <summary>
    /// Picks the mic channel. Interfaces like the Scarlett Solo expose "Analogue 1+2" as stereo with the mic
    /// on input 1 only, so a vocal recorded "as is" would sit in the left ear.
    /// </summary>
    public static MicChannel Detect(float[] interleaved, int channels)
    {
        if (channels < 2) return MicChannel.Left;
        double l = 0, r = 0;
        for (int i = 0; i + 1 < interleaved.Length; i += channels)
        {
            l += interleaved[i] * (double)interleaved[i];
            r += interleaved[i + 1] * (double)interleaved[i + 1];
        }
        const double ratio = 0.1; // -10 dB energy difference
        if (r < l * ratio) return MicChannel.Left;
        if (l < r * ratio) return MicChannel.Right;
        return MicChannel.Mix;
    }

    public static float[] Extract(float[] interleaved, int channels, MicChannel ch)
    {
        int frames = interleaved.Length / Math.Max(1, channels);
        var mono = new float[frames];
        if (channels == 1)
        {
            Array.Copy(interleaved, mono, frames);
            return mono;
        }
        for (int f = 0; f < frames; f++)
        {
            int i = f * channels;
            mono[f] = ch switch
            {
                MicChannel.Left => interleaved[i],
                MicChannel.Right => interleaved[i + 1],
                _ => 0.5f * (interleaved[i] + interleaved[i + 1]),
            };
        }
        return mono;
    }

    public static float[] Downmix(float[] interleaved, int channels) =>
        channels == 1 ? interleaved : Extract(interleaved, channels, MicChannel.Mix);
}
