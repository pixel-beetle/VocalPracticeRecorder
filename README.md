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
   - 窗口本身就是透明可穿透的取景框（待机蓝框、录制红框，在暗色浏览器上清晰可见），直接拖标题栏或拉四边套住 B 站视频；展开侧栏或底部音高面板均**向外扩窗**，不会改变取景区域的位置和大小。
   - 顶栏选择**麦克风**和**伴奏输出设备**，点「● 录制」开始；录制时窗口自动置顶并从录屏画面中隐藏，唱完点「■ 停止」。
2. **实时音高监测**
   - 打开软件即在顶栏实时显示当前唱到的**音名、音分偏差与音准条**（绿 ≤±12¢ / 黄 ≤±28¢ / 红 >±28¢），不录制也能用来练声抠音准，且不会被录进导出视频。
   - 点击顶栏音高条可向下外扩底部**滚动音高曲线**（自动跟随音域，支持滚轮缩放、上下拖动平移、双击重置自动跟随）。
3. **录音库**
   - 点顶栏左上角「☰」向左外扩侧栏，按曲名自动分组，跨天/跨月练习自动显示练习跨度（如 `5 · 跨 27 天`）。
   - 侧栏顶部支持按曲名搜索、`★` 只看标星最佳，以及一键清理旧未标星废片（移入回收站，完整保留标星与拆分共用素材）。
4. **快速编辑**（录完自动进入编辑视图，点顶栏「← 录制」回到原位置的透明取景框）
   - **人声对齐与轻润色**：外放唱歌自动对齐人声；点「微调」按需展开偏移微调（`←` `→` ±10 ms）、麦克风通道选择与默认关闭的**「轻润色」**开关（80 Hz 去喷麦 + 温和动态压限 + 自然空间混响，试听与导出听感一致）。
   - **编辑器音高叠加**：点「音高」可在人声波形上叠加彩色音高曲线，随缩放、裁剪与删除同步联动。
   - **裁剪、删除中间片段与拆分**：拖黄色把手或按 `I` / `O` 裁剪头尾；拖选区按 `Delete` 删除中间片段（右键红色块恢复）；播放头处按 `S` 拆分为两个 Take（共用素材不占额外空间）；`Space` 播放，`Ctrl+Z` 撤销，`Ctrl+滚轮` 缩放。
5. **导出与扫码传手机**（`Ctrl+E`）
   - 支持 **MP4 视频**（可选勾选「**附音高线**」，在视频正下方拼接一条独立暗色音高曲线栏，不遮挡原画面歌词）、**混音 MP3 320k**、**干声 MP3 320k**。
   - 导出后可点「**复制文件**」到电脑微信 `Ctrl+V`，或点「**传手机**」生成局域网二维码，手机扫码直接下载到相册（无需外网或管理员权限）。
6. **自定义目录与设置**
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
