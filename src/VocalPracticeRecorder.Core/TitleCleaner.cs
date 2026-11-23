using System.Text.RegularExpressions;

namespace VocalPracticeRecorder.Core;

/// <summary>Turns a browser window title (e.g. a Bilibili tab) into a readable song name.</summary>
public static partial class TitleCleaner
{
    public const string Unknown = "未命名";

    private static readonly string[] BrowserSuffixes =
    [
        "Google Chrome", "Microsoft Edge", "Microsoft​ Edge", "Mozilla Firefox", "Firefox",
        "Brave", "Opera", "Vivaldi", "Chromium", "Arc",
    ];

    private static readonly string[] SiteSuffixes =
    [
        "_哔哩哔哩_bilibili", "_哔哩哔哩", "_bilibili", "- 哔哩哔哩", "哔哩哔哩 (゜-゜)つロ 干杯~-bilibili",
        "- YouTube", "- ニコニコ動画", "- niconico",
    ];

    // Tags that carry no song identity: 【ニコカラ】, [off vocal], (On Vocal), 【カラオケ】, etc.
    [GeneratedRegex(@"[【\[\(（](?:[^】\]\)）]*?)(ニコカラ|カラオケ|karaoke|off\s*vocal|on\s*vocal|offvocal|onvocal|伴奏|消音|歌词|字幕|中字|高音質|高画質|HD|4K|1080P|MV|PV)(?:[^】\]\)）]*?)[】\]\)）]", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    // Browser profile segment, e.g. " - 个人 - Microsoft Edge" or " - Work".
    [GeneratedRegex(@"\s+-\s+(个人|個人|Personal|Profile\s*\d*|工作|Work|用户配置\s*\d*|ユーザー\s*\d*|人员\s*\d*)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfileRegex();

    [GeneratedRegex(@"^\(\d+\)\s*")]
    private static partial Regex NotificationCountRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    public static string Clean(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return Unknown;
        string s = windowTitle.Trim();

        // Strip browser name suffix " - Microsoft Edge".
        foreach (var b in BrowserSuffixes)
        {
            var suffix = " - " + b;
            int i = s.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && i + suffix.Length >= s.Length - 1) { s = s[..i]; break; }
        }
        s = ProfileRegex().Replace(s, "");
        // Edge appends " 和另外 N 个页面" for tab groups.
        int extra = s.IndexOf(" 和另外 ", StringComparison.Ordinal);
        if (extra > 0) s = s[..extra];

        foreach (var site in SiteSuffixes)
        {
            int i = s.IndexOf(site, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) { s = s[..i]; break; }
        }

        s = NotificationCountRegex().Replace(s, "");
        s = TagRegex().Replace(s, " ");
        s = s.Trim(' ', '-', '_', '|', '/', '　');
        s = MultiSpaceRegex().Replace(s, " ");
        return string.IsNullOrWhiteSpace(s) ? Unknown : s;
    }

    /// <summary>Normalised key for grouping takes of the same song.</summary>
    public static string GroupKey(string songTitle) =>
        MultiSpaceRegex().Replace(songTitle.Trim().ToLowerInvariant(), " ");
}
