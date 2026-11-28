using System.Text.Json;
using System.Text.Json.Serialization;

namespace VocalPracticeRecorder.Core;

public enum MicChannel { Left, Right, Mix }

public enum AlignMode
{
    /// <summary>No alignment applied yet.</summary>
    None,
    /// <summary>Delay measured from backing-track bleed in the mic (speakers).</summary>
    Auto,
    /// <summary>Re-used offset remembered for this device combination (headphones).</summary>
    Remembered,
    /// <summary>User adjusted by hand.</summary>
    Manual,
    /// <summary>Device round-trip latency measured in earlier speaker recordings (headphones).</summary>
    Calibrated,
}

public sealed class TrackInfo
{
    public string File { get; set; } = "";
    /// <summary>Seconds from the session clock origin to the first sample/frame.</summary>
    public double StartOffset { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string DeviceName { get; set; } = "";
    public string DeviceId { get; set; } = "";
}

public sealed class VideoInfo
{
    public string File { get; set; } = "video.mp4";
    public double StartOffset { get; set; }
    public int Fps { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long FrameCount { get; set; }
    public double Duration => Fps > 0 ? (double)FrameCount / Fps : 0;
}

public sealed class EditState
{
    public double TrimStart { get; set; }
    /// <summary>Timeline seconds; &lt;= 0 means "until the end".</summary>
    public double TrimEnd { get; set; }
    /// <summary>Extra shift of the vocal track in ms. Positive = vocal later, negative = vocal earlier.</summary>
    public double VocalOffsetMs { get; set; }
    public double VocalGainDb { get; set; }
    public double BackingGainDb { get; set; } = -3;
    public bool BackingMuted { get; set; }
    /// <summary>Optional light vocal polish (HPF + gentle compression + subtle natural reverb); off by default.</summary>
    public bool VocalPolish { get; set; }
    public MicChannel MicChannel { get; set; } = MicChannel.Left;
    public AlignMode AlignMode { get; set; } = AlignMode.None;
    public double? AutoDelayMs { get; set; }
    public double? AutoConfidence { get; set; }
    /// <summary>True when the last auto alignment found backing bleed in the mic (speaker mode).</summary>
    public bool AutoReliable { get; set; }
    /// <summary>Deleted ranges on the timeline (sorted, non-overlapping, frame-snapped).</summary>
    public List<TimeRange> Cuts { get; set; } = [];

    public EditState Clone()
    {
        var c = (EditState)MemberwiseClone();
        c.Cuts = [.. Cuts];
        return c;
    }
}

public sealed class ExportRecord
{
    public string Path { get; set; } = "";
    public ExportKind Kind { get; set; }
    public DateTime At { get; set; }
}

public sealed class SessionInfo
{
    public const string FileName = "session.json";

    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string WindowTitle { get; set; } = "";
    public string SongTitle { get; set; } = TitleCleaner.Unknown;
    public bool Starred { get; set; }
    public string Note { get; set; } = "";
    /// <summary>True while capture is running or post-processing has not finished.</summary>
    public bool Incomplete { get; set; }
    public VideoInfo Video { get; set; } = new();
    public TrackInfo Mic { get; set; } = new();
    public TrackInfo Backing { get; set; } = new();
    public EditState Edit { get; set; } = new();
    public List<ExportRecord> Exports { get; set; } = [];

    [JsonIgnore] public string Folder { get; set; } = "";
    /// <summary>
    /// JSON file of this take inside <see cref="Folder"/>. A recording folder holds the media once;
    /// takes split from it are extra <c>take-N.json</c> files sharing that media.
    /// </summary>
    [JsonIgnore] public string JsonFile { get; set; } = FileName;
    [JsonIgnore] public bool IsPrimaryTake => JsonFile == FileName;
    [JsonIgnore] public string ThumbFile => IsPrimaryTake ? "thumb.jpg" : $"thumb-{System.IO.Path.GetFileNameWithoutExtension(JsonFile)}.jpg";

    public double Duration => Video.Duration;

    /// <summary>Where vocal sample 0 lands on the timeline (seconds), including the user offset.</summary>
    public double VocalPlacement => Mic.StartOffset - Video.StartOffset + Edit.VocalOffsetMs / 1000.0;
    /// <summary>Where vocal sample 0 lands on the timeline without the user offset.</summary>
    public double RawVocalPlacement => Mic.StartOffset - Video.StartOffset;
    public double BackingPlacement => Backing.StartOffset - Video.StartOffset;

    public double EffectiveTrimEnd => Edit.TrimEnd > 0 ? Math.Min(Edit.TrimEnd, Duration) : Duration;

    public string DeviceComboKey => $"{Mic.DeviceId}|{Backing.DeviceId}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save()
    {
        var path = System.IO.Path.Combine(Folder, JsonFile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static SessionInfo Load(string folder, string jsonFile = FileName)
    {
        var json = File.ReadAllText(System.IO.Path.Combine(folder, jsonFile));
        var s = JsonSerializer.Deserialize<SessionInfo>(json, JsonOptions) ?? throw new InvalidDataException("Empty session file");
        s.Folder = folder;
        s.JsonFile = jsonFile;
        return s;
    }

    /// <summary>Is this a take file name (session.json or take-N.json)?</summary>
    public static bool IsTakeFile(string fileName) =>
        fileName.Equals(FileName, StringComparison.OrdinalIgnoreCase) ||
        (fileName.StartsWith("take-", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Splits this take at timeline time <paramref name="t"/>: this take keeps everything before t and a new
    /// take (same media, new JSON file in the same folder) gets everything after. Neither file is saved here.
    /// </summary>
    public SessionInfo SplitAt(double t)
    {
        double at = EditTimeline.Snap(t, Video.Fps);
        if (at <= Edit.TrimStart + 0.5 || at >= EffectiveTrimEnd - 0.5)
            throw new InvalidOperationException("拆分点离开头或结尾太近（至少要留 0.5 秒）。");
        int n = 2;
        while (File.Exists(System.IO.Path.Combine(Folder, $"take-{n}.json"))) n++;
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var second = JsonSerializer.Deserialize<SessionInfo>(json, JsonOptions)!;
        second.Folder = Folder;
        second.JsonFile = $"take-{n}.json";
        second.Id = $"{System.IO.Path.GetFileName(Folder)}-{n}";
        second.CreatedAt = CreatedAt.AddSeconds(at); // keeps chronological take order
        second.Starred = false;
        second.Exports = [];
        second.Edit.TrimStart = at;
        second.Edit.TrimEnd = Edit.TrimEnd;
        Edit.TrimEnd = at;
        return second;
    }

    public string PathOf(string file) => System.IO.Path.Combine(Folder, file);
}
