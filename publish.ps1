# 生成单文件发布版到 .\publish，并把 ffmpeg.exe 一起放进去（双击 练唱录制.exe 即可用）
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet publish src/VocalPracticeRecorder -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
$ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if ($ff) { Copy-Item $ff publish\ffmpeg.exe -Force; Write-Host "已复制 ffmpeg: $ff" }
else { Write-Warning "PATH 中找不到 ffmpeg.exe，请手动放到 publish 目录" }
Move-Item publish\VocalPracticeRecorder.exe publish\练唱录制.exe -Force
Write-Host "完成：$(Resolve-Path publish)\练唱录制.exe"
