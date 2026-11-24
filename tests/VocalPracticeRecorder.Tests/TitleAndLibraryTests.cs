using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Tests;

public class TitleAndLibraryTests
{
    [Theory]
    [InlineData("【ニコカラ】アイドル（On Vocal）_哔哩哔哩_bilibili - 个人 - Microsoft​ Edge", "アイドル")]
    [InlineData("【ニコカラ】アイドル【off vocal】_哔哩哔哩_bilibili - Google Chrome", "アイドル")]
    [InlineData("(3) 【カラオケ】夜に駆ける / YOASOBI_哔哩哔哩_bilibili - Microsoft Edge", "夜に駆ける / YOASOBI")]
    [InlineData("ロキ [ニコカラHD]_哔哩哔哩_bilibili", "ロキ")]
    // Real title from the first recording (Edge tab group + "用户配置 1" profile).
    [InlineData("【中日字幕】ツキマシテハ from ゼンジン未到—Mrs.GREEN APPLE_哔哩哔哩_bilibili 和另外 14 个页面 - 用户配置 1 - Microsoft​ Edge",
        "ツキマシテハ from ゼンジン未到—Mrs.GREEN APPLE")]
    [InlineData("【ニコカラ】アイドル - 用户配置 1 - Microsoft​ Edge", "アイドル")]
    [InlineData("", TitleCleaner.Unknown)]
    [InlineData("哔哩哔哩 (゜-゜)つロ 干杯~-bilibili - Google Chrome", TitleCleaner.Unknown)]
    public void Cleans_browser_titles(string raw, string expected) =>
        Assert.Equal(expected, TitleCleaner.Clean(raw));

    [Fact]
    public void Same_song_variants_share_group_key_and_takes_are_chronological()
    {
        var t0 = new DateTime(2026, 11, 20, 20, 0, 0);
        var sessions = new List<SessionInfo>
        {
            new() { Id = "b", SongTitle = "アイドル", CreatedAt = t0.AddMinutes(10) },
            new() { Id = "a", SongTitle = "アイドル ", CreatedAt = t0 },
            new() { Id = "c", SongTitle = "ロキ", CreatedAt = t0.AddDays(1) },
        };
        var groups = Library.Group(sessions);
        Assert.Equal(2, groups.Count);
        Assert.Equal("ロキ", groups[0].Title); // most recently practised first
        Assert.Equal(["a", "b"], groups[1].Takes.Select(t => t.Id));
        Assert.Equal(2, groups[1].TakeNumberOf(sessions[0]));
    }

    [Fact]
    public void Detects_mic_on_left_input_of_stereo_interface()
    {
        var data = new float[2000];
        for (int i = 0; i < data.Length; i += 2) { data[i] = 0.5f * MathF.Sin(i); data[i + 1] = 0.001f; }
        Assert.Equal(MicChannel.Left, ChannelSelector.Detect(data, 2));
        for (int i = 0; i < data.Length; i += 2) data[i + 1] = data[i];
        Assert.Equal(MicChannel.Mix, ChannelSelector.Detect(data, 2));
        Assert.Equal(MicChannel.Left, ChannelSelector.Detect(data, 1));
    }
}

public class RegionMathTests
{
    private static readonly PixelRect Monitor = new(0, 0, 2560, 1440);

    [Fact]
    public void Clamped_region_is_even_inside_monitor_or_empty()
    {
        var rnd = new Random(5);
        for (int k = 0; k < 2000; k++)
        {
            var r = RegionMath.FromDrag(rnd.Next(-500, 3000), rnd.Next(-500, 2000), rnd.Next(-500, 3000), rnd.Next(-500, 2000));
            var c = RegionMath.ClampToMonitor(r, Monitor);
            if (c.IsEmpty) continue;
            Assert.True(c.Width % 2 == 0 && c.Height % 2 == 0);
            Assert.True(c.Width >= RegionMath.MinSize && c.Height >= RegionMath.MinSize);
            Assert.True(c.X >= 0 && c.Y >= 0 && c.Right <= Monitor.Right && c.Bottom <= Monitor.Bottom);
        }
    }

    [Fact]
    public void Picks_monitor_by_centre_then_overlap()
    {
        var mons = new List<PixelRect> { new(0, 0, 1920, 1080), new(1920, -200, 2560, 1440) };
        Assert.Equal(1, RegionMath.PickMonitor(new PixelRect(1800, 0, 600, 400), mons));
        Assert.Equal(0, RegionMath.PickMonitor(new PixelRect(100, 100, 50, 50), mons));
        Assert.Equal(-1, RegionMath.PickMonitor(new PixelRect(-900, -900, 50, 50), mons));
    }

    [Theory]
    [InlineData(1920, 1080, 720)]
    [InlineData(1281, 721, 720)]
    [InlineData(853, 479, 720)]
    [InlineData(3000, 1000, 720)]
    public void Output_size_is_even_capped_and_keeps_aspect(int w, int h, int max)
    {
        var (ow, oh) = RegionMath.OutputSize(w, h, max);
        Assert.True(ow % 2 == 0 && oh % 2 == 0);
        Assert.True(oh <= max);
        Assert.InRange((double)ow / oh, (double)w / h * 0.98, (double)w / h * 1.02);
    }

    [Fact]
    public void Window_local_hole_keeps_border_and_handles_negative_monitor_coords()
    {
        var win = new PixelRect(-1200, 100, 1000, 700);
        var vp = new PixelRect(-1194, 150, 988, 644);
        var h = RegionMath.WindowLocalHole(win, vp, minBorder: 3);
        Assert.Equal(new PixelRect(6, 50, 988, 644), h);
        // Even if viewport touches the outer window edge, minBorder is preserved for resize hit-testing.
        var clamped = RegionMath.WindowLocalHole(win, win, minBorder: 4);
        Assert.Equal(new PixelRect(4, 4, 992, 692), clamped);
        // Too small yields empty (no hole).
        Assert.True(RegionMath.WindowLocalHole(win, new PixelRect(-1190, 150, 20, 20)).IsEmpty);
    }

    [Fact]
    public void Expand_for_editor_keeps_large_window_and_clamps_small_window_in_work_area()
    {
        var work = new PixelRect(0, 0, 1920, 1040);
        // Already large enough -> untouched (zero jump).
        var big = new PixelRect(100, 80, 1280, 800);
        Assert.Equal(big, RegionMath.ExpandForEditor(big, work, 980, 680));
        // Small window near bottom-right corner -> grows to min size and stays strictly inside work area.
        var small = new PixelRect(1200, 600, 640, 400);
        var exp = RegionMath.ExpandForEditor(small, work, 980, 680);
        Assert.Equal(980, exp.Width);
        Assert.Equal(680, exp.Height);
        Assert.True(exp.X >= work.X && exp.Y >= work.Y && exp.Right <= work.Right && exp.Bottom <= work.Bottom);
    }

    [Fact]
    public void Clamp_window_to_monitors_rescues_offscreen_saved_bounds()
    {
        var mons = new List<PixelRect> { new(0, 0, 1920, 1080) };
        var unpluggedSecondMonitor = new PixelRect(2500, 200, 1000, 700);
        var c = RegionMath.ClampWindowToMonitors(unpluggedSecondMonitor, mons);
        Assert.True(c.X >= 0 && c.Y >= 0 && c.Right <= 1920 && c.Bottom <= 1080);
        Assert.Equal(1000, c.Width);
        Assert.Equal(700, c.Height);
    }
}
