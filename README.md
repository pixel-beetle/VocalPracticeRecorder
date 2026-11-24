# 练唱录制 (VocalPracticeRecorder)

Windows 桌面端轻量练唱录屏工具：打开软件就是透明取景框 → 套在 B 站 ニコカラ 画面上 → 顶栏一键录制 → 自动对齐人声 / 快速剪辑 → 导出小文件直接发给声乐老师或朋友。

## 下载与使用（普通用户推荐）

无需安装任何运行环境或 FFmpeg，直接下载打包好的压缩包即可：

1. 打开本仓库右侧的 **[Releases](https://github.com/pixel-beetle/VocalPracticeRecorder/releases/latest)** 页面，下载最新的 `VocalPracticeRecorder-v*-win-x64.zip`。
2. **完整解压**整个 `练唱录制` 文件夹（请保持 `练唱录制.exe` 和 `ffmpeg.exe` 在同一个文件夹内，不要只单独拖出 exe）。
3. 双击 `练唱录制.exe` 即可使用。
   - **首次运行提示**：由于未购买商业代码签名，Windows SmartScreen 可能会弹出「Windows 已保护你的电脑」，点击**「更多信息」→「仍要运行」**即可。
   - **系统要求**：Windows 10（2004 及以上）或 Windows 11 64 位。

## 快速上手

1. **取景与录制**
   - 窗口本身就是透明可穿透的取景框，直接拖标题栏或拉四边套住 B 站视频（点击透明区域可以直接操作后面的浏览器）。
   - 顶栏选择**麦克风**和**伴奏输出设备**，点「● 录制」开始；录制时窗口自动置顶并从录屏画面中隐藏，唱完点「■ 停止」。
2. **录音库**
   - 点顶栏左上角「☰」展开 / 收起侧栏。
   - 按曲名自动分组（从透明框后方的浏览器窗口标题识别），组内按 Take 1 / 2 / 3… 排列；右键可重命名、标星、打开文件夹、删除（进回收站）。
3. **快速编辑**（录完自动进入编辑视图，点顶栏「← 录制」回到原位置的透明取景框）
   - **人声对齐**：外放唱歌时，程序从麦克风拾到的伴奏回声自动计算延迟并对齐；戴耳机时用 `←` `→`（±10 ms，`Shift` ±50 ms）微调一次，后续同设备组合自动沿用。
   - **裁剪头尾**：拖动黄色把手，或按 `I` / `O` 设定起止点；`Space` 播放/暂停，`Ctrl+Z` 撤销，`Ctrl+滚轮` 缩放时间轴。
   - **删除中间片段**：在波形上拖选一段 → 按 `Delete`（显示为红色斜线块，右键可恢复），播放与导出自动同步跳过。
   - **拆分 Take**：一次连唱两遍时，把播放头移到中间按 `S`（或点「拆分」），共用同一份素材不占额外空间。
   - **单声道声卡兼容**：像 Scarlett Solo 这类麦克风只在左声道（通道 1）的声卡会自动选对通道并居中。
4. **导出分享**（`Ctrl+E`）
   - 支持 **MP4（最高 720p H.264 + AAC 320k）**、**混音 MP3 320k**、**干声 MP3 320k**。
   - 导出后点「复制文件」，切到微信 `Ctrl+V` 直接发送。
5. **自定义目录与设置**
   - 点顶栏齿轮「⚙」可修改录音库与导出目录（支持把已有录音一键搬家到其他磁盘）及画质设置。
   - 运行日志位于 `%APPDATA%\VocalPracticeRecorder\log.txt`。

## 从源码构建（开发者）

前置环境（Windows 10 2004+ / Windows 11 x64）：

```powershell
winget install Microsoft.DotNet.SDK.9
winget install Gyan.FFmpeg.Essentials
```

重开终端后克隆并发布单文件版（首次发布需下载自包含运行时，可能需要 1–3 分钟）：

```powershell
git clone https://github.com/pixel-beetle/VocalPracticeRecorder.git
cd VocalPracticeRecorder
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

产物输出在 `publish\练唱录制.exe`（附带 `ffmpeg.exe`）。

### 测试与发布

- `dotnet test`：核心逻辑、对齐、帧精确剪辑导出、目录搬家与曲名清洗测试。
- `VocalPracticeRecorder.exe --selftest <report.txt> [秒]`：端到端录/编/导自检。
- `VocalPracticeRecorder.exe --snapshot <outDir> demo`：离线渲染全状态 UI 截图（含 2× 局部特写与透明区断言，不修改用户配置）。
- **自动发版**：推送 `v*` 格式的 tag（如 `git tag v0.3.3 && git push origin v0.3.3`）会触发 GitHub Actions 自动运行测试、打包 `VocalPracticeRecorder-<tag>-win-x64.zip`（含 `ffmpeg.exe`）并发布到 GitHub Releases。

## 开源许可

- 本项目源码采用 [MIT License](LICENSE)。
- Release 压缩包内附带的 `ffmpeg.exe` 为 FFmpeg 的 GPLv3 静态构建版本，详情见 [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)。
