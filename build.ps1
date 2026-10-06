param([switch]$Test, [switch]$Package)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$output = Join-Path $taskRoot 'artifacts\WinBootVideo'
# Always update the same application directory. Never overwrite a running copy.
foreach ($path in @((Join-Path $taskRoot 'artifacts'), $output)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "构建输出不允许为链接或联接：$path"
    }
}
if (Test-Path -LiteralPath $output) {
    if (Get-ChildItem -LiteralPath $output -Recurse -Force -Attributes ReparsePoint) { throw '构建输出含链接，停止覆盖。' }
}
foreach ($process in @(Get-Process -Name 'WinBootVideo', 'WinBootVideo.Host' -ErrorAction SilentlyContinue)) {
    $processPath = $process.Path
    if (!$processPath) { throw '无法确认运行中的 WinBootVideo 位置，请关闭后再构建。' }
    if ($processPath.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '请先关闭 artifacts\WinBootVideo 中运行的程序再构建。'
    }
}
$sdk = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { throw '需要安装 x64 .NET 8 SDK。' }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBuildEnableWorkloadResolver = 'false'
$previousLocalAppData = $env:LOCALAPPDATA
$env:LOCALAPPDATA = Join-Path $taskRoot '.runtime\build-local'
New-Item -ItemType Directory -Path $env:LOCALAPPDATA -Force | Out-Null
try {
& $sdk restore (Join-Path $taskRoot 'src\WinBootVideo\WinBootVideo.csproj') --configfile (Join-Path $taskRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw '离线还原失败。' }
& $sdk publish (Join-Path $taskRoot 'src\WinBootVideo\WinBootVideo.csproj') -c Release -o $output --self-contained false --no-restore
if ($LASTEXITCODE -ne 0) { throw 'GUI 编译失败。' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$output\WinBootVideo.Host.exe" /r:System.Web.Extensions.dll /r:System.Xml.dll (Join-Path $taskRoot 'src\Host\Program.cs') (Join-Path $taskRoot 'src\Host\LoginAttemptGate.cs') (Join-Path $taskRoot 'src\Host\SupervisorPolicy.cs') (Join-Path $taskRoot 'src\Host\LogonSessionIdentity.cs') (Join-Path $taskRoot 'src\Host\DesktopLaunchCoordinator.cs') (Join-Path $taskRoot 'src\Shared\WindowsCommandLine.cs') (Join-Path $taskRoot 'src\Shared\InputDesktopState.cs') (Join-Path $taskRoot 'src\Shared\StartupLimits.cs') (Join-Path $taskRoot 'src\Shared\LoginTaskSupport.cs')
if ($LASTEXITCODE -ne 0) { throw '宿主编译失败。' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\Host\WinBootVideo.Host.exe.config') -Destination $output
foreach ($document in @('README.md', 'LICENSE', 'QA.md', 'THIRD-PARTY-NOTICES.md')) {
    $source = Join-Path $taskRoot $document
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $output }
}
$demo = Join-Path $taskRoot 'assets\demo-login.mp4'
$licenses = Join-Path $output 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'assets\licenses') -File | Copy-Item -Destination $licenses
if (Test-Path -LiteralPath $demo) { Copy-Item -LiteralPath $demo -Destination (Join-Path $output 'demo-login.mp4') }
$researchSource = Join-Path $taskRoot 'research'
$researchTarget = Join-Path $output 'research'
New-Item -ItemType Directory -Path $researchTarget -Force | Out-Null
Get-ChildItem -LiteralPath $researchSource -Filter '*.md' | Copy-Item -Destination $researchTarget
Write-Host "应用已生成：$output\WinBootVideo.exe"
if ($Test) {
    & $sdk restore (Join-Path $taskRoot 'tests\WinBootVideo.Checks.csproj') --configfile (Join-Path $taskRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw '验证项目还原失败。' }
    & $sdk run --project (Join-Path $taskRoot 'tests\WinBootVideo.Checks.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw '验证失败。' }
}
if ($Package) {
    $archive = Join-Path $taskRoot 'artifacts\WinBootVideo.zip'
    if ((Test-Path -LiteralPath $archive) -and ((Get-Item -LiteralPath $archive -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '打包文件不允许为链接。'
    }
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
    Write-Host "压缩包已更新：$archive"
}
} finally { $env:LOCALAPPDATA = $previousLocalAppData }
