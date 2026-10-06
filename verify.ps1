param([string]$FFmpegDirectory)
$ErrorActionPreference = 'Stop'
if (!$FFmpegDirectory) {
    $command = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($command) { $FFmpegDirectory = Split-Path -Parent $command.Source }
}
if (!(Test-Path -LiteralPath (Join-Path $FFmpegDirectory 'ffmpeg.exe')) -or !(Test-Path -LiteralPath (Join-Path $FFmpegDirectory 'ffprobe.exe'))) { throw '请通过 -FFmpegDirectory 指定同时包含 ffmpeg.exe、ffprobe.exe 的目录。' }
& (Join-Path $PSScriptRoot 'build.ps1')
$bundle = Join-Path $PSScriptRoot 'artifacts\WinBootVideo'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixtures = Join-Path $PSScriptRoot 'artifacts\fixtures'
New-Item -ItemType Directory -Path $fixtures -Force | Out-Null
$fixture = Join-Path $fixtures 'HungPlayer.exe'
& $compiler /nologo /target:winexe /platform:x64 "/out:$fixture" (Join-Path $PSScriptRoot 'tests\fixtures\HungPlayer.cs')
if ($LASTEXITCODE -ne 0) { throw '验证夹具编译失败。' }
$deferredFixture = Join-Path $fixtures 'DeferredReadyPlayer.exe'
& $compiler /nologo /target:winexe /platform:x64 "/out:$deferredFixture" (Join-Path $PSScriptRoot 'tests\fixtures\DeferredReadyPlayer.cs')
if ($LASTEXITCODE -ne 0) { throw '冷启动延迟就绪夹具编译失败。' }
$logoFixture = Join-Path $fixtures 'OfficialLogoFiles.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$logoFixture" (Join-Path $PSScriptRoot 'tests\fixtures\OfficialLogoFiles.cs')
if ($LASTEXITCODE -ne 0) { throw '官方 Logo 隔离文件复制夹具编译失败。' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.cli'
$env:MSBuildEnableWorkloadResolver = 'false'
$sdk = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
& $sdk restore (Join-Path $PSScriptRoot 'tests\WinBootVideo.Checks.csproj') --configfile (Join-Path $PSScriptRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw '验证项目还原失败。' }
& $sdk run --project (Join-Path $PSScriptRoot 'tests\WinBootVideo.Checks.csproj') -c Release --no-restore -- $bundle $FFmpegDirectory $fixture $deferredFixture
if ($LASTEXITCODE -ne 0) { throw '集成验证失败。' }
# WPF allows one Application per process; run the real full-screen GUI flow
# separately from the windowed GUI checks above.
& $sdk run --project (Join-Path $PSScriptRoot 'tests\WinBootVideo.Checks.csproj') -c Release --no-restore --no-build -- --gui-fullscreen-only $bundle
if ($LASTEXITCODE -ne 0) { throw '真实 GUI 全屏预览验证失败。' }
& $sdk run --project (Join-Path $PSScriptRoot 'tests\WinBootVideo.Checks.csproj') -c Release --no-restore --no-build -- --gui-recovery-only
if ($LASTEXITCODE -ne 0) { throw '备用任务不可用时的 GUI 恢复验证失败。' }
& $sdk run --project (Join-Path $PSScriptRoot 'tests\WinBootVideo.Checks.csproj') -c Release --no-restore --no-build -- --boot-logo-only $logoFixture $bundle
if ($LASTEXITCODE -ne 0) { throw 'Logo 图片、模拟固件事务、GUI 与官方隔离文件复制验证失败。' }
