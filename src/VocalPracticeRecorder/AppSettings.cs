using System.IO;
using System.Text.Json;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder;

public sealed class AppSettings
{
    public string? MicDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public int? RegionX { get; set; }
    public int? RegionY { get; set; }
    public int? RegionW { get; set; }
    public int? RegionH { get; set; }
    /// <summary>Saved physical bounds of the main window in capture mode.</summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowW { get; set; }
    public int? WindowH { get; set; }
    public bool SidebarOpen { get; set; }
    public bool TopmostInCapture { get; set; } = true;
    public int Fps { get; set; } = 20;
    public int MaxHeight { get; set; } = 720;
    public ExportKind LastExportKind { get; set; } = ExportKind.MixVideo;
    public ExportSettings Export { get; set; } = new();
    /// <summary>Last manual vocal offset per "mic|output" device pair, used when auto alignment is impossible (headphones).</summary>
    public Dictionary<string, double> RememberedOffsets { get; set; } = [];
    /// <summary>Recent confident bleed delays per device pair (ms); their median is the device round-trip latency.</summary>
    public Dictionary<string, List<double>> MeasuredLatencies { get; set; } = [];

    public DeviceCalibration CalibrationFor(string comboKey)
    {
        double? latency = MeasuredLatencies.TryGetValue(comboKey, out var list) && list.Count > 0
            ? list.OrderBy(x => x).ElementAt(list.Count / 2) : null;
        double? manual = RememberedOffsets.TryGetValue(comboKey, out var m) ? m : null;
        return new DeviceCalibration(latency, manual);
    }

    public void RecordLatency(string comboKey, double ms)
    {
        if (!MeasuredLatencies.TryGetValue(comboKey, out var list)) MeasuredLatencies[comboKey] = list = [];
        list.Add(Math.Round(ms, 1));
        if (list.Count > 5) list.RemoveAt(0);
        Save();
    }
    public string? DataRoot { get; set; }
    /// <summary>Optional separate export folder; null = &lt;Root&gt;\Exports.</summary>
    public string? ExportDirOverride { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public PixelRect? Region
    {
        get => RegionW is > 0 && RegionH is > 0 ? new PixelRect(RegionX ?? 0, RegionY ?? 0, RegionW.Value, RegionH.Value) : null;
        set
        {
            RegionX = value?.X; RegionY = value?.Y; RegionW = value?.Width; RegionH = value?.Height;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public PixelRect? CaptureWindowBounds
    {
        get => WindowW is > 0 && WindowH is > 0 ? new PixelRect(WindowX ?? 0, WindowY ?? 0, WindowW.Value, WindowH.Value) : null;
        set
        {
            WindowX = value?.X; WindowY = value?.Y; WindowW = value?.Width; WindowH = value?.Height;
        }
    }

    /// <summary>Set false for headless snapshot/test runs so they never overwrite the user's real settings.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Persist { get; set; } = true;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VocalPracticeRecorder", "settings.json");

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "练唱录制");
    [System.Text.Json.Serialization.JsonIgnore] public string Root => DataRoot ?? DefaultRoot;
    [System.Text.Json.Serialization.JsonIgnore] public string RecordingsDir => Path.Combine(Root, "Recordings");
    [System.Text.Json.Serialization.JsonIgnore] public string ExportsDir => ExportDirOverride ?? Path.Combine(Root, "Exports");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }
}
