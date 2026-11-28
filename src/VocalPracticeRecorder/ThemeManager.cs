using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VocalPracticeRecorder;

public sealed record ColorPreset(string Name, string Hex);

/// <summary>
/// Central runtime theme palette controller. Derives hover/pressed/subtle variants mathematically from
/// the user's chosen Accent, Vocal, and Backing colors and updates Application.Resources live.
/// Default accent (#E04B7A) is aligned with our app icon's coral→rose gradient (#FF6B5E → #C2185B).
/// </summary>
public static class ThemeManager
{
    public const string DefaultAccentHex = "#E04B7A";
    public const string DefaultVocalHex = "#4AA3F0";
    public const string DefaultBackingHex = "#967AE6";

    public static readonly ColorPreset[] AccentPresets =
    [
        new("桃红（图标）", "#E04B7A"),
        new("珊瑚橘", "#EB6558"),
        new("钴蓝", "#3B8EEA"),
        new("紫罗兰", "#8B6CE8"),
        new("翡翠绿", "#2FB584"),
        new("琥珀金", "#E59A38"),
    ];

    public static readonly ColorPreset[] VocalPresets =
    [
        new("天蓝", "#4AA3F0"),
        new("青绿", "#38C6A8"),
        new("樱粉", "#EC6B96"),
        new("暖橙", "#F09B46"),
        new("淡紫", "#A485F5"),
        new("草绿", "#62C26A"),
    ];

    public static readonly ColorPreset[] BackingPresets =
    [
        new("紫罗兰", "#967AE6"),
        new("靛蓝", "#5596E6"),
        new("湖青", "#46B8A6"),
        new("陶橘", "#E08454"),
        new("莓粉", "#D6689F"),
        new("雾灰", "#8A96A8"),
    ];

    public static Color CurrentAccent { get; private set; } = ParseOrDefault(DefaultAccentHex, Color.FromRgb(0xE0, 0x4B, 0x7A));
    public static Color CurrentVocal { get; private set; } = ParseOrDefault(DefaultVocalHex, Color.FromRgb(0x4A, 0xA3, 0xF0));
    public static Color CurrentBacking { get; private set; } = ParseOrDefault(DefaultBackingHex, Color.FromRgb(0x96, 0x7A, 0xE6));

    public static event Action? ThemeChanged;

    public static void Apply(AppSettings settings) =>
        Apply(settings.AccentColorHex, settings.VocalColorHex, settings.BackingColorHex);

    public static void Apply(string? accentHex, string? vocalHex, string? backingHex)
    {
        var accent = ParseOrDefault(accentHex, ParseOrDefault(DefaultAccentHex, Color.FromRgb(0xE0, 0x4B, 0x7A)));
        var vocal = ParseOrDefault(vocalHex, ParseOrDefault(DefaultVocalHex, Color.FromRgb(0x4A, 0xA3, 0xF0)));
        var backing = ParseOrDefault(backingHex, ParseOrDefault(DefaultBackingHex, Color.FromRgb(0x96, 0x7A, 0xE6)));

        CurrentAccent = accent;
        CurrentVocal = vocal;
        CurrentBacking = backing;

        var res = Application.Current?.Resources;
        if (res == null) return;

        var accentHover = Mix(accent, Colors.White, 0.14);
        var accentPressed = Mix(accent, Colors.Black, 0.16);
        var accentSoftBg = Color.FromArgb(0x28, accent.R, accent.G, accent.B);
        var accentSoftBorder = Color.FromArgb(0x78, accent.R, accent.G, accent.B);
        var accentSoftText = Mix(accent, Colors.White, 0.42);
        var accentPressedSoft = Color.FromArgb(0x3A, accent.R, accent.G, accent.B);

        SetBrush(res, "AccentBrush", accent);
        SetBrush(res, "AccentHoverBrush", accentHover);
        SetBrush(res, "AccentPressedBrush", accentPressed);
        SetBrush(res, "AccentSubtleBgBrush", accentSoftBg);
        SetBrush(res, "AccentSubtleBorderBrush", accentSoftBorder);
        SetBrush(res, "AccentSubtlePressedBrush", accentPressedSoft);
        SetBrush(res, "AccentTextSoftBrush", accentSoftText);

        SetBrush(res, "PlayAccentBrush", accent);
        SetBrush(res, "PlayAccentHoverBrush", accentHover);
        SetBrush(res, "PlayAccentPressedBrush", accentPressed);

        // Keep Fluent fallback keys in lockstep so ComboBox popups & focus visuals never diverge.
        SetBrush(res, "AccentFillColorDefaultBrush", accent);
        SetBrush(res, "AccentFillColorSecondaryBrush", accentHover);
        SetBrush(res, "AccentFillColorTertiaryBrush", accentPressed);
        SetBrush(res, "AccentTextFillColorPrimaryBrush", accentSoftText);
        SetBrush(res, "SystemAccentColorPrimaryBrush", accent);
        SetBrush(res, "SystemAccentColorSecondaryBrush", accentHover);
        SetBrush(res, "SystemAccentColorTertiaryBrush", accentPressed);

        SetBrush(res, "VocalBrush", vocal);
        SetBrush(res, "BackingBrush", backing);

        ThemeChanged?.Invoke();
    }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)) return false;
        color = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseOrDefault(string? hex, Color fallback) =>
        TryParseHex(hex, out var c) ? c : fallback;

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Clamp(Math.Round(a.R + (b.R - a.R) * t), 0, 255),
        (byte)Math.Clamp(Math.Round(a.G + (b.G - a.G) * t), 0, 255),
        (byte)Math.Clamp(Math.Round(a.B + (b.B - a.B) * t), 0, 255));

    private static void SetBrush(ResourceDictionary res, string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        res[key] = b;
    }
}
