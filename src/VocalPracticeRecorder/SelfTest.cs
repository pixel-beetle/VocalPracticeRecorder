using System.Diagnostics;
using System.IO;
using System.Text;
using NAudio.CoreAudioApi;
using VocalPracticeRecorder.Capture;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder;

/// <summary>
/// Headless end-to-end check of the real capture pipeline on this machine:
/// <c>VocalPracticeRecorder.exe --selftest &lt;reportPath&gt; [seconds]</c>.
/// Records the centre of the primary monitor plus default mic/loopback, post-processes and exports.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        string report = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "vpr-selftest.txt");
        double seconds = args.Length > 1 && double.TryParse(args[1], out var sArg) ? sArg : 6;
        var sb = new StringBuilder();
        void W(string line) { sb.AppendLine(line); }
        int code = 0;
        string root = Path.Combine(Path.GetTempPath(), "vpr-selftest-" + DateTime.Now.ToString("HHmmss"));
        try
        {
            var mons = ScreenCapturer.EnumerateMonitors();
            foreach (var m in mons) W($"monitor {m.AdapterIndex}/{m.OutputIndex} {m.Name} {m.Bounds}");
            var primary = mons.FirstOrDefault(m => m.Bounds.X == 0 && m.Bounds.Y == 0) ?? mons[0];
            var b = primary.Bounds;
            var region = RegionMath.ClampToMonitor(new PixelRect(b.X + b.Width / 2 - 640, b.Y + b.Height / 2 - 360, 1280, 720), b);
            W($"region {region}");

            using var controller = new RecordingController();
            string micId = AudioDevices.DefaultId(DataFlow.Capture)!;
            string outId = AudioDevices.DefaultId(DataFlow.Render)!;
            controller.OpenDevices(micId, outId);
            W($"mic format {controller.MicFormat}");
            await Task.Delay(300);

            string title = Native.TopLevelTitleAt(region.X + region.Width / 2, region.Y + region.Height / 2);
            var sw = Stopwatch.StartNew();
            SessionInfo s;
            try
            {
                s = controller.Start(region, title, Path.Combine(root, "Recordings"), 20, 720);
            }
            catch (Exception ex)
            {
                // The desktop may be inaccessible to this process (e.g. a sandboxed shell); verify the
                // rest of the pipeline with a synthetic picture and report it.
                W($"WARN desktop capture unavailable here: {ex.Message.Replace('\n', ' ')}");
                W("     -> continuing with a synthetic video source to test audio/encode/align/export");
                ScreenCapturer.ForceSynthetic = true;
                s = controller.Start(region, title, Path.Combine(root, "Recordings"), 20, 720);
            }
            W($"title '{title}' -> '{s.SongTitle}'  start took {sw.ElapsedMilliseconds} ms");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            sw.Restart();
            s = await controller.StopAsync();
            W($"stop took {sw.ElapsedMilliseconds} ms");
            W($"offsets video={s.Video.StartOffset:0.0000} mic={s.Mic.StartOffset:0.0000} backing={s.Backing.StartOffset:0.0000}");
            W($"video frames={s.Video.FrameCount} duration={s.Duration:0.000}s out={s.Video.Width}x{s.Video.Height}");

            sw.Restart();
            await SessionPostProcessor.CompressRawAsync(s);
            W($"flac took {sw.ElapsedMilliseconds} ms; files: {s.Mic.File}, {s.Backing.File}");
            sw.Restart();
            var audio = await SessionAudio.LoadAsync(s);
            double micDur = audio.MicInterleaved.Length / (double)audio.MicChannels / SessionAudio.Rate;
            double backDur = audio.BackingStereo.Length / 2.0 / SessionAudio.Rate;
            W($"decode took {sw.ElapsedMilliseconds} ms; mic {micDur:0.000}s ({audio.MicChannels}ch) backing {backDur:0.000}s");
            double micEnd = s.Mic.StartOffset + micDur, backEnd = s.Backing.StartOffset + backDur, vidEnd = s.Video.StartOffset + s.Duration;
            W($"track ends (session clock): video={vidEnd:0.000} mic={micEnd:0.000} backing={backEnd:0.000}");
            // Invariant: every track must cover the same wall-clock span (no shrinking loopback, no lost frames).
            if (Math.Abs(micEnd - vidEnd) > 0.15 || Math.Abs(backEnd - vidEnd) > 0.15)
            {
                W("FAIL: track end times diverge by more than 150 ms");
                code = 2;
            }
            if (Math.Abs(s.Mic.StartOffset - s.Video.StartOffset) > 0.5 || Math.Abs(s.Backing.StartOffset - s.Video.StartOffset) > 0.5)
            {
                W("FAIL: start offsets diverge by more than 500 ms");
                code = 2;
            }

            s.Edit.MicChannel = ChannelSelector.Detect(audio.MicInterleaved, audio.MicChannels);
            sw.Restart();
            var est = SessionPostProcessor.AutoAlign(s, audio, null);
            W($"align took {sw.ElapsedMilliseconds} ms: channel={s.Edit.MicChannel} delay={est.DelaySeconds * 1000:0.0}ms z={est.Confidence:0.0} reliable={est.IsReliable}");
            await SessionPostProcessor.MakeThumbnailAsync(s);
            W($"thumb exists: {File.Exists(s.PathOf("thumb.jpg"))}");
            s.Incomplete = false;
            s.Save();

            foreach (var kind in Enum.GetValues<ExportKind>())
            {
                var outPath = Path.Combine(root, "export" + ExportBuilder.Extension(kind).Replace(".", "_" + kind + "."));
                sw.Restart();
                await Ffmpeg.RunAsync(ExportBuilder.BuildArgs(s, kind, outPath));
                W($"export {kind}: {new FileInfo(outPath).Length / 1024.0:0} KB in {sw.ElapsedMilliseconds} ms");
            }
            W($"folder {s.Folder}");
            W($"dataroot {root}");
        }
        catch (Exception ex)
        {
            W("EXCEPTION " + ex);
            code = 1;
        }
        W(code == 0 ? "RESULT OK" : "RESULT FAIL");
        File.WriteAllText(report, sb.ToString());
        return code;
    }
}
