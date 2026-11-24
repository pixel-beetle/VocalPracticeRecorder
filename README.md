# 练唱录制 (VocalPracticeRecorder)

打开软件就是透明取景框 → 套在 B 站ニコカラ画面上 → 顶栏一键录制 → 自动对齐 / 剪辑 → 导出小文件发给声乐老师。

## 使用

1. **取景与录制**：窗口本身就是透明可穿透的取景框，直接拖放、拉边框套住 B 站视频（点击透明区可直接操作浏览器）。顶栏选麦克风与伴奏输出，点「● 录制」开始；录制中窗口自动置顶并从录屏画面中隐藏，唱完点「■ 停止」。
2. **录音库**：点顶栏左上角「☰」展开/收起侧栏。按曲名（自动从透明框后方的浏览器标题识别）分组，组内 Take 1/2/3…，右键可重命名、标星、打开文件夹、删除（进回收站）。
3. **编辑器**（录完自动打开，窗口切为实体；点顶栏「← 录制」回到原位置的透明取景框）
   - 外放唱歌时从麦克风里的伴奏回声**自动对齐人声**；戴耳机时用 `←` `→`（±10ms，Shift ±50ms）微调，按设备组合记住，下次自动套用；
   - 拖黄色把手或按 `I` / `O` 裁剪头尾；`Space` 播放；`Ctrl+Z` 撤销；`Ctrl+滚轮` 缩放；
   - **删除中间片段**：在波形上拖出选区 → `Delete`（红色斜线块，右键可恢复）；播放和导出同步跳过；
   - **拆分 take**：播放头移到两遍之间按 `S`（或「拆分」），共用同一份素材不占额外空间；
   - Scarlett Solo 麦克风只在通道 1（左），程序自动选对通道并居中。
4. **导出**（`Ctrl+E`）：MP4（H.264 + AAC 320k）/ 混音 MP3 320k / 干声 MP3 320k。点「复制文件」到微信 Ctrl+V。

点顶栏齿轮「⚙」可修改录音库与导出目录（支持把已有录音一键搬家到其他磁盘）及画质设置。日志：`%APPDATA%\VocalPracticeRecorder\log.txt`。

## 开发

- .NET 9 + WPF（Fluent 主题），NAudio 3（WASAPI 采集/回环，QPC 时间戳），Vortice（DXGI 桌面复制，GDI 兜底），FFmpeg（编码/导出）。
- 透明取景框通过 `AllowsTransparency="True"`（关闭 .NET 9 Fluent 默认的 Windows 11 Mica 背景）+ Win32 `WM_NCHITTEST` 原生 8 方向缩放实现，透明区自动穿透，录制时动态启用 `WDA_EXCLUDEFROMCAPTURE`。
- `dotnet test`：核心逻辑与不变量测试（对齐、帧精确剪辑导出、搬家、取景框几何、曲名清洗）。
- `VocalPracticeRecorder.exe --selftest <report.txt> [秒]`：真机端到端录/编/导自检。
- `VocalPracticeRecorder.exe --snapshot <outDir> demo`：离线渲染全状态 PNG（含 2× 局部特写与 Win32 抠洞断言，不修改用户配置）。
- `./publish.ps1`：单文件发布并附带 ffmpeg.exe。
