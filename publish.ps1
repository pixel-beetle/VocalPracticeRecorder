<#
.SYNOPSIS
生成单文件发布版到 .\publish（默认双击 练唱录制.exe 即可运行），可选同时打包 Release zip。
#>
param(
    [string]$Version = "",
    [string]$FfmpegPath = "",
    [string]$FfmpegLicensePath = "",
    [string]$ZipPath = "",
    [string]$OutDir = "publish"
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$publishDir = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $PSScriptRoot $OutDir }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$dotnetArgs = @(
    "publish", "src/VocalPracticeRecorder",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-o", $publishDir
)
if ($Version) {
    $cleanVer = $Version.TrimStart('v', 'V')
    $dotnetArgs += "-p:Version=$cleanVer"
}

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# 清理可能残留的调试符号文件
Get-ChildItem -Path $publishDir -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

$rawExe = Join-Path $publishDir "VocalPracticeRecorder.exe"
$finalExe = Join-Path $publishDir "练唱录制.exe"
if (-not (Test-Path $rawExe)) { throw "未找到发布输出：$rawExe" }
Move-Item $rawExe $finalExe -Force

# 复制 ffmpeg.exe
if (-not $FfmpegPath) {
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($cmd) { $FfmpegPath = $cmd.Source }
}
$targetFfmpeg = Join-Path $publishDir "ffmpeg.exe"
if ($FfmpegPath -and (Test-Path $FfmpegPath)) {
    Copy-Item $FfmpegPath $targetFfmpeg -Force
    Write-Host "已复制 ffmpeg: $FfmpegPath"
} else {
    Write-Warning "PATH 中找不到 ffmpeg.exe，请手动放到 $publishDir 目录"
}

# 复制第三方声明与 FFmpeg 许可（如提供）
$notices = Join-Path $PSScriptRoot "THIRD_PARTY_NOTICES.txt"
if (Test-Path $notices) {
    Copy-Item $notices (Join-Path $publishDir "THIRD_PARTY_NOTICES.txt") -Force
}
if ($FfmpegLicensePath -and (Test-Path $FfmpegLicensePath)) {
    Copy-Item $FfmpegLicensePath (Join-Path $publishDir "FFMPEG-LICENSE.txt") -Force
}

Write-Host "完成：$finalExe"

# 可选：打成顶层带「练唱录制/」文件夹的便携压缩包，避免解压散落导致 exe 与 ffmpeg.exe 分离
if ($ZipPath) {
    if (-not (Test-Path $targetFfmpeg)) {
        throw "打包 Release zip 需要 ffmpeg.exe，但 $targetFfmpeg 不存在"
    }
    $resolvedZip = if ([IO.Path]::IsPathRooted($ZipPath)) { $ZipPath } else { Join-Path $PSScriptRoot $ZipPath }
    $zipParent = Split-Path -Parent $resolvedZip
    if ($zipParent) { New-Item -ItemType Directory -Force -Path $zipParent | Out-Null }
    if (Test-Path $resolvedZip) { Remove-Item $resolvedZip -Force }

    $stageRoot = Join-Path ([IO.Path]::GetTempPath()) ("vpr-stage-" + [Guid]::NewGuid().ToString("N"))
    $stageApp = Join-Path $stageRoot "练唱录制"
    New-Item -ItemType Directory -Force -Path $stageApp | Out-Null
    try {
        Copy-Item $finalExe $stageApp
        Copy-Item $targetFfmpeg $stageApp
        foreach ($extra in @("THIRD_PARTY_NOTICES.txt", "FFMPEG-LICENSE.txt")) {
            $p = Join-Path $publishDir $extra
            if (Test-Path $p) { Copy-Item $p $stageApp }
        }
        # 使用 .NET ZipFile (UTF-8 条目名) 避免 PowerShell 5 Compress-Archive 对中文目录名编码不兼容
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory(
            $stageRoot,
            $resolvedZip,
            [IO.Compression.CompressionLevel]::Optimal,
            $false,
            [Text.UTF8Encoding]::new($false)
        )
        Write-Host "已打包：$resolvedZip"
    } finally {
        if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
