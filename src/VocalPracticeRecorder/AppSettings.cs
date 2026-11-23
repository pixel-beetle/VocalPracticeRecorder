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

    [System.Text.Json.Serialization.JsonIgnore]
    public PixelRect? Region
    {
        get => RegionW is > 0 && RegionH is > 0 ? new PixelRect(RegionX ?? 0, RegionY ?? 0, RegionW.Value, RegionH.Value) : null;
        set
        {
            RegionX = value?.X; RegionY = value?.Y; RegionW = value?.Width; RegionH = value?.Height;
        }
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VocalPracticeRecorder", "settings.json");

    public string Root => DataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "练唱录制");
    public string RecordingsDir => Path.Combine(Root, "Recordings");
    public string ExportsDir => Path.Combine(Root, "Exports");

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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
        }
        catch { }
    }
}
