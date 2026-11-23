# 练唱录制 (VocalPracticeRecorder)

打开 B 站ニコカラ → 框选画面 → 录 → 快速对齐/剪头尾 → 一键导出小文件发给声乐老师。

## 使用

1. **新录音**：选麦克风和「伴奏来源」（要录的输出设备，比如 Scarlett Solo 的扬声器输出），点「框选区域」拖出视频画面。
2. **开始录制**：主窗口最小化，区域外出现红框和一个小控制条（两者都不会被录进视频）。到浏览器里点播放跟唱，唱完点「■ 停止」。
3. **编辑器**（录完自动打开）
   - 外放唱歌时，程序会在麦克风里找伴奏的回声，**自动对齐人声**；
   - 戴耳机时找不到回声，用 `←` `→`（±10ms，Shift ±50ms）边听边调，调好的值按设备组合记住，下次自动套用；
   - 拖时间轴上的黄色把手或按 `I` / `O` 裁剪头尾；`Space` 播放；`Ctrl+Z` 撤销；Ctrl+滚轮缩放；
   - Scarlett Solo 的麦克风只在输入 1（左声道），程序会自动选对通道并放到中间。
4. **导出**（`Ctrl+E`）：视频 MP4（H.264 + AAC 320k）/ 混音 MP3 320k / 干声 MP3 320k。点「复制文件」后到微信「文件传输助手」里 Ctrl+V。

录音库按曲名（自动从浏览器标题识别，去掉【ニコカラ】、_哔哩哔哩_bilibili 等）分组，组内 Take 1/2/3…，可标「最佳」、重命名、删除（进回收站）。

数据位置：`视频\练唱录制\Recordings`（原始素材，FLAC 无损）和 `视频\练唱录制\Exports`（导出文件）。日志：`%APPDATA%\VocalPracticeRecorder\log.txt`。

## 开发

- .NET 9 + WPF（Fluent 主题），NAudio 3（WASAPI 采集/回环，带 QPC 时间戳），Vortice（DXGI 桌面复制，GDI 兜底），FFmpeg（编码/导出）。
- `dotnet test`：核心逻辑与不变量测试（对齐算法、导出时间轴、选区几何、曲名清洗）。
- `VocalPracticeRecorder.exe --selftest <report.txt> [秒]`：真机端到端自检（录制→FLAC→对齐→三种导出），检查三路轨道时钟一致。
- `VocalPracticeRecorder.exe --snapshot <outDir> demo`：生成演示数据并把界面渲染成 PNG（含 2× 局部特写），用于视觉验收。
- `./publish.ps1`：单文件发布并附带 ffmpeg.exe。

### 时间轴模型

所有轨道都以录制开始时的 Stopwatch 时刻为原点记录首样本时间（音频用 WASAPI 包的 QPC 时间戳，视频按墙钟恒定帧率补帧/丢帧）。时间轴 0 点 = 视频首帧；
人声位置 = `mic.StartOffset − video.StartOffset + 人声偏移`，伴奏位置 = `backing.StartOffset − video.StartOffset`。预览播放和 FFmpeg 导出使用同一套公式（`SessionInfo.VocalPlacement` / `BackingPlacement`）。
