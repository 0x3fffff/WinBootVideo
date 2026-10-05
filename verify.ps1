param([string]$FFmpegDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (!$FFmpegDirectory) {
    $command = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($command) { $FFmpegDirectory = Split-Path -Parent $command.Source }
}
if (!(Test-Path -LiteralPath (Join-Path $FFmpegDirectory 'ffmpeg.exe')) -or !(Test-Path -LiteralPath (Join-Path $FFmpegDirectory 'ffprobe.exe'))) { throw '请通过 -FFmpegDirectory 指定同时包含 ffmpeg.exe、ffprobe.exe 的目录。' }
& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $OutputDirectory
$bundle = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory, $PSScriptRoot) } else { Join-Path $PSScriptRoot 'artifacts\WinBootVideo' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixture = Join-Path $PSScriptRoot 'artifacts\HungPlayer.exe'
& $compiler /nologo /target:winexe /platform:x64 "/out:$fixture" (Join-Path $PSScriptRoot 'tests\fixtures\HungPlayer.cs')
if ($LASTEXITCODE -ne 0) { throw '验证夹具编译失败。' }
$deferredFixture = Join-Path $PSScriptRoot 'artifacts\DeferredReadyPlayer.exe'
& $compiler /nologo /target:winexe /platform:x64 "/out:$deferredFixture" (Join-Path $PSScriptRoot 'tests\fixtures\DeferredReadyPlayer.cs')
if ($LASTEXITCODE -ne 0) { throw '冷启动延迟就绪夹具编译失败。' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.cli'
$env:MSBuildEnableWorkloadResolver = 'false'
$sdk = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
& $sdk restore (Join-Path $PSScriptRoot 'tests\WinBootVideo.Checks.csproj') --configfile (Join-Path $PSScriptRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw '验证项目还原失败。' }
& $sdk run --project (Join-Path $PSScriptRoot 'tests\WinBootVideo.Checks.csproj') -c Release --no-restore -- $bundle $FFmpegDirectory $fixture $deferredFixture
if ($LASTEXITCODE -ne 0) { throw '集成验证失败。' }
