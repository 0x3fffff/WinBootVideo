using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WinBootVideo.Core;

internal static class OfficialEspChecks
{
    public static async Task Run(string fixture, string work, Action<bool, string> check)
    {
        string esp = Path.Combine(work, "esp-path", "fake-esp");
        Directory.CreateDirectory(Path.Combine(esp, "EFI"));
        byte[] bmp = BootLogoImage.EncodeBgr24(32, 32, new byte[96 * 32], 96);
        string stage = BootLogoPackage.Extract(Path.Combine(work, "esp-path", "official"), bmp);
        var mount = new StringBuilder(512); var volume = new StringBuilder(512);
        if (!GetVolumePathName(esp, mount, mount.Capacity) || !GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, volume.Capacity))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        string guidEsp = volume + Path.GetRelativePath(mount.ToString(), esp);
        check(Directory.Exists(Path.Combine(guidEsp, "EFI")), "临时 EFI 目录的卷 GUID 路径可由实际 .NET 8 文件 API 访问");
        check(await Probe(fixture, stage, esp) == 0, "固定版本官方 InitEspPath 接受普通临时目录");
        check(await Probe(fixture, stage, guidEsp) == 0, "固定版本官方 InitEspPath 接受同一临时目录的卷 GUID 路径，不误报 ESP 无效");
        string config = Path.Combine(stage, "setup.exe.config");
        check(File.ReadAllText(config) == BootLogoPackage.InstallerRuntimeConfig,
            "实际准备包携带安装器本地路径兼容配置，不依赖更改系统注册表");
        File.Delete(config);
        check(await Probe(fixture, stage, guidEsp) != 0,
            "仅移除本地兼容配置就重新出现原始 ESP 拒绝错误，证明检查能抓住本次故障");
        File.WriteAllText(config, BootLogoPackage.InstallerRuntimeConfig, new UTF8Encoding(false));
        string windows = Path.Combine(esp, "EFI", "Microsoft", "Boot", "bootmgfw.efi");
        Directory.CreateDirectory(Path.GetDirectoryName(windows)!);
        byte[] original = { 70, 71, 72 }; File.WriteAllBytes(windows, original);
        check(await Probe(fixture, stage, guidEsp, "--copy-esp") == 0,
            "官方实际 InitEspPath → InstallFiles 使用卷 GUID 路径完成临时目录安装");
        string logo = Path.Combine(esp, "EFI", "HackBGRT");
        BootLogoInstaller.VerifyOfficialFiles(logo, stage, bmp);
        check(Directory.GetFiles(logo).Length == 6 && File.ReadAllBytes(windows).SequenceEqual(original),
            "GUID 路径实际复制的六个文件通过生产校验，模拟 Windows 启动器保持原样");
        string empty = Path.Combine(work, "esp-path", "no-efi"); Directory.CreateDirectory(empty);
        check(await Probe(fixture, stage, empty) != 0,
            "兼容配置不绕过 EFI 内容检查：没有 EFI 的目录仍由官方初始化拒绝");
    }
    private static async Task<int> Probe(string fixture, string stage, string esp, string mode = "--check-esp")
    {
        string runner = Path.Combine(stage, "esp-check.exe");
        File.Copy(Path.GetFullPath(fixture), runner, true);
        string config = Path.Combine(stage, "setup.exe.config");
        File.Delete(runner + ".config");
        if (File.Exists(config)) File.Copy(config, runner + ".config", true);
        var info = new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(mode); info.ArgumentList.Add(stage); info.ArgumentList.Add(esp);
        using var process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine((await output + await error).Trim());
        return process.ExitCode;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string path, StringBuilder volumePath, int length);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder volume, int length);
}
