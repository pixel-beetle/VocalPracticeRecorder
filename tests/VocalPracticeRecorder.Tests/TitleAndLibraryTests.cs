using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Tests;

public class TitleAndLibraryTests
{
    [Theory]
    [InlineData("【ニコカラ】アイドル（On Vocal）_哔哩哔哩_bilibili - 个人 - Microsoft​ Edge", "アイドル")]
    [InlineData("【ニコカラ】アイドル【off vocal】_哔哩哔哩_bilibili - Google Chrome", "アイドル")]
    [InlineData("(3) 【カラオケ】夜に駆ける / YOASOBI_哔哩哔哩_bilibili - Microsoft Edge", "夜に駆ける / YOASOBI")]
    [InlineData("ロキ [ニコカラHD]_哔哩哔哩_bilibili", "ロキ")]
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
}
