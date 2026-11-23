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
    public MicChannel MicChannel { get; set; } = MicChannel.Left;
    public AlignMode AlignMode { get; set; } = AlignMode.None;
    public double? AutoDelayMs { get; set; }
    public double? AutoConfidence { get; set; }
    /// <summary>True when the last auto alignment found backing bleed in the mic (speaker mode).</summary>
    public bool AutoReliable { get; set; }

    public EditState Clone() => (EditState)MemberwiseClone();
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
        var path = System.IO.Path.Combine(Folder, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static SessionInfo Load(string folder)
    {
        var json = File.ReadAllText(System.IO.Path.Combine(folder, FileName));
        var s = JsonSerializer.Deserialize<SessionInfo>(json, JsonOptions) ?? throw new InvalidDataException("Empty session file");
        s.Folder = folder;
        return s;
    }

    public string PathOf(string file) => System.IO.Path.Combine(Folder, file);
}
