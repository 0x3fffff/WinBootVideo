using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinBootVideo.Core;

public sealed class BootLogoNativeFirmware : IBootLogoFirmware
{
    private const string Global = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableExW(string name, string guid, byte[] buffer, uint size, out uint attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFirmwareEnvironmentVariableExW(string name, string guid, byte[]? buffer, uint size, uint attributes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFirmwareType(out uint type);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(string path, out ulong available, out ulong total, out ulong free);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disable, ref Privilege state, uint length, IntPtr previous, IntPtr returnLength);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Privilege { public uint Count; public long Luid; public uint Attributes; }
    public static bool IsUefi => GetFirmwareType(out uint value) && value == 2;
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static long FreeBytes(string esp)
    {
        if (!BootLogoPaths.IsVolumePath(esp)) throw new InvalidDataException("EFI 卷路径不明确。");
        if (!GetDiskFreeSpaceExW(esp, out ulong available, out _, out _) || available > long.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 EFI 可用空间。");
        return (long)available;
    }
    public BootLogoNativeFirmware()
    {
        if (!IsAdministrator || !IsUefi) throw new InvalidOperationException("固件检查需要管理员权限和 UEFI 启动环境。");
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out var token)) throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out long luid)) throw new Win32Exception();
            var state = new Privilege { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { CloseHandle(token); }
    }
    public FirmwareValue? Read(string name)
    {
        var buffer = new byte[65536];
        uint length = GetFirmwareEnvironmentVariableExW(name, Global, buffer, (uint)buffer.Length, out uint attributes);
        if (length == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 203) return null;
            throw new Win32Exception(error, "读取固件变量失败：" + name);
        }
        return new FirmwareValue(attributes, buffer.AsSpan(0, (int)length).ToArray());
    }
    public void Write(string name, FirmwareValue? value)
    {
        if (name != "BootOrder" && !System.Text.RegularExpressions.Regex.IsMatch(name, "^Boot[0-9A-F]{4}$"))
            throw new InvalidDataException("不允许修改此固件变量。");
        // Deleting a variable requires its existing attributes on some firmware implementations.
        uint attributes = value?.Attributes ?? Read(name)?.Attributes ?? 7;
        if (!SetFirmwareEnvironmentVariableExW(name, Global, value?.Data, (uint)(value?.Data.Length ?? 0), attributes))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "写入固件变量失败：" + name);
    }
}

public static class BootLogoProbe
{
    // Fixed script: no user values are interpolated and no encryption keys are queried.
    private const string Script = """
$ErrorActionPreference='Stop'
$result=[ordered]@{Administrator=$true; Uefi=$true; X64=([Environment]::Is64BitOperatingSystem -and $env:PROCESSOR_ARCHITECTURE -eq 'AMD64'); SecureBoot=$null; OsFullyDecrypted=$null; EspCount=0; EspOnOsDisk=$false; EspVolume=''; EspPartitionGuid=''; Model=''; Error=''; CheckedUtc=[DateTime]::UtcNow.ToString('o')}
try {
  $result.Model=(Get-CimInstance Win32_ComputerSystem).Manufacturer+' '+(Get-CimInstance Win32_ComputerSystem).Model
  $result.SecureBoot=Confirm-SecureBootUEFI
  $osDrive=[IO.Path]::GetPathRoot($env:WINDIR).Substring(0,2)
  $os=Get-Partition -DriveLetter $osDrive.Substring(0,1)
  $all=@(Get-Partition)
  $esp=@($all | Where-Object {$_.GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'})
  $result.EspCount=$esp.Count
  if($esp.Count -eq 1) {
    $disk=Get-Disk -Number $esp[0].DiskNumber
    $result.EspOnOsDisk=($esp[0].DiskNumber -eq $os.DiskNumber -and $esp[0].IsSystem -and $disk.PartitionStyle -eq 'GPT' -and !$disk.IsOffline -and !$disk.IsReadOnly -and $disk.BusType -ne 'USB')
    $result.EspPartitionGuid=$esp[0].Guid
    $paths=@($esp[0].AccessPaths | Where-Object {$_.StartsWith('\\?\Volume{')})
    if($paths.Count -eq 1) {$result.EspVolume=$paths[0]}
  }
  $volumes=@(Get-CimInstance -Namespace 'root/CIMV2/Security/MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume | Where-Object {$_.DriveLetter -eq $osDrive})
  if($volumes.Count -eq 1) {
    $status=Invoke-CimMethod -InputObject $volumes[0] -MethodName GetConversionStatus
    if($status.ReturnValue -eq 0) {$result.OsFullyDecrypted=($status.ConversionStatus -eq 0 -and $status.EncryptionPercentage -eq 0)}
  }
} catch {$result.Error=$_.Exception.Message}
$result | ConvertTo-Json -Compress -Depth 4
""";
    public static async Task<BootLogoEnvironment> ReadAsync()
    {
        if (!BootLogoNativeFirmware.IsAdministrator) return new BootLogoEnvironment
        {
            Uefi = BootLogoNativeFirmware.IsUefi, X64 = RuntimeInformation.OSArchitecture == Architecture.X64,
            Error = "请点击管理员只读检查。", CheckedUtc = DateTime.UtcNow
        };
        if (!BootLogoNativeFirmware.IsUefi) return new BootLogoEnvironment { Administrator = true, CheckedUtc = DateTime.UtcNow };
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);\n" + Script)));
        start.StandardOutputEncoding = Encoding.UTF8; start.StandardErrorEncoding = Encoding.UTF8;
        using var process = Process.Start(start) ?? throw new IOException("无法运行系统检查。");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new IOException("只读检查失败：" + await errors);
        var result = JsonSerializer.Deserialize<BootLogoEnvironment>(await output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("只读检查没有返回有效报告。");
        result.X64 = result.X64 && RuntimeInformation.OSArchitecture == Architecture.X64;
        if (BootLogoPaths.IsVolumePath(result.EspVolume))
        {
            try { result.EspFreeBytes = BootLogoNativeFirmware.FreeBytes(result.EspVolume); }
            catch (Exception ex) { result.Error += "\n" + ex.Message; }
        }
        return result;
    }
}
