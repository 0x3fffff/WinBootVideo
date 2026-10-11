using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WinBootVideo.Core;

public static class BootLogoPackage
{
    public const string Version = "2.6.0";
    public const string ArchiveHash = "6204911d777ac03e514b90126568ef6faa1d477779b31c536b142dba92f4a2c0";
    public const string Config = "boot=MS\nimage= x=.5 y=.382 path=splash.bmp\nresolution=-1x-1\nlog=1\ndebug=0\n";
    // The pinned installer targets older Framework defaults. Opt in locally so
    // its actual Esp.TryPath / file copy can access an already verified volume GUID.
    // This changes only this executable's IO policy, never Windows / BIOS settings.
    public const string InstallerRuntimeConfig = """
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <runtime>
    <AppContextSwitchOverrides value="Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false" />
  </runtime>
</configuration>
""";
    public const string ReleaseUrl = "https://github.com/Metabolix/HackBGRT/releases/tag/v2.6.0";
    public static byte[] Archive()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinBootVideo.HackBGRT.zip") ??
            throw new FileNotFoundException("缺少内置的官方 HackBGRT 发布包。");
        using var output = new MemoryStream(); stream.CopyTo(output);
        byte[] zip = output.ToArray();
        if (Hash(zip) != ArchiveHash) throw new InvalidDataException("内置官方发布包 SHA-256 不匹配。");
        return zip;
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static string Extract(string newDirectory, byte[] bmp)
    {
        BootLogoImage.Validate(bmp);
        BootLogoPaths.OrdinaryDirectory(newDirectory);
        if (Directory.Exists(newDirectory) || File.Exists(newDirectory)) throw new IOException("准备目录必须是新的空目录。");
        byte[] archive = Archive();
        Directory.CreateDirectory(newDirectory);
        using var input = new MemoryStream(archive); using var zip = new ZipArchive(input);
        const string prefix = "HackBGRT-2.6.0/";
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("官方 ZIP 根目录异常。");
            string relative = entry.FullName.Substring(prefix.Length);
            if (relative.Length == 0 || relative.EndsWith('/')) continue;
            string destination = SafeChild(newDirectory, relative);
            if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("官方 ZIP 条目超出大小限制。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = entry.Open(); using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
        File.WriteAllBytes(Path.Combine(newDirectory, "splash.bmp"), bmp);
        File.WriteAllText(Path.Combine(newDirectory, "config.txt"), Config, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(newDirectory, "setup.exe.config"), InstallerRuntimeConfig, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(newDirectory, "WinBootVideo-恢复与首次启动.txt"), Guide, new UTF8Encoding(false));
        return newDirectory;
    }
    public static string SafeChild(string root, string relative)
    {
        if (relative.Contains(':') || relative.Contains('\\') || relative.Split('/').Any(p => p == ".." || p == "." || p.Length == 0) || Path.IsPathRooted(relative))
            throw new InvalidDataException("ZIP 条目路径不安全。");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP 条目逃逸准备目录。");
        return full;
    }
    public const string FirstBootGuide = """
1. 从 Boot Manager 单次选择 HackBGRT。
2. 若出现 Verification failed / 0x1A 蓝色提示，选 OK，按 Enter。
3. 随后出现 5 秒倒计时时，立即按任意键进入 MOKManager；错过就重新试启动。
4. 选择 Enroll hash from disk。
5. 选择系统盘，进入 EFI → HackBGRT → grubx64.efi。
6. 选择 Continue → Yes → Reboot，完成该文件的哈希信任确认。
7. 重启后，再从 Boot Manager 单次选择 HackBGRT，确认新 Logo 显示且 Windows 登录正常。

保持 Secure Boot 开启。只信任上述文件的哈希，不选 Enroll key from disk，不导入整张证书或清空 TPM。
若没有 MOKManager 菜单或无法继续，选择 Windows Boot Manager 返回系统；先不要将 HackBGRT 设为默认，也不用重复安装。
""";
    public const string PermanentGuide = """
Logo 文件已保存在 EFI 分区，不需要每次重新安装，也不需要让本软件一直运行。单次选择 HackBGRT 只改变这一次启动；以后每次显示，需把它设为默认入口。

1. 先确认通过 HackBGRT 试启动时，新 Logo 显示且 Windows 登录正常。
2. 进入 BIOS / UEFI，找到 Boot Priority / Boot Order（启动顺序）。
3. 把 HackBGRT 放到第一位，保留 Windows Boot Manager，并放在它后面；其他设备项原样保留。
4. 按固件界面提示保存并退出，先重启验证，再验证关机后开机。

本软件当前提供这些设置步骤；默认启动顺序仍由你在 BIOS 中手动设置，界面里的指引按钮不会执行此修改。
保持现有 Secure Boot、TPM、UEFI / CSM 和 SATA 设置。若以后固件更新或恢复默认设置导致原 Logo 回来，先检查启动顺序，不要立即重装。

安装成功且试启动验证后，可以删除原图片和导出的准备包。保留 C:\ProgramData\WinBootVideo-BootLogo 的恢复资料及另一设备上的恢复说明。
需要恢复原效果时，先把 Windows Boot Manager 调回第一位，再进入 Windows 使用本软件的“恢复原 Logo”。
""";
    public const string VerifiedFlow = """
已验证环境：Windows 11 x64、COLORFUL P15 23、Insyde UEFI、Secure Boot 开启、系统盘未加密、单 EFI 系统分区。
实机使用反馈已确认以下流程跑通：选图与转换 → 导出准备包 → 管理员只读检查 → 安装独立项 → 单次选择 HackBGRT → 完成首次文件哈希确认 → 看到自定义 Logo → 正常进入 Windows → 将 HackBGRT 设为默认入口并保存。
其他型号和固件仍需按本说明先单次试启动；这一结果不代表多系统、加密磁盘或所有反作弊软件的兼容性。
""";
    public static string Guide => $$"""
WinBootVideo 开机 Logo · HackBGRT 2.6.0

改变的是 UEFI 启动 Windows 时的静态 BGRT Logo。主板通电后最早的厂商 Logo 可能仍短暂出现。它与登录后视频是两个独立功能。
Windows 加载阶段即使显示的是七彩虹等厂商 Logo，也可以由 HackBGRT 替换。刚按电源就出现的固件图片不属于本功能；本软件不刷写或修改 BIOS 来彻底替换第一张图片。
软件不能保证所有固件兼容，更不能保证启动风险为零。请先准备 Windows 恢复 U 盘，并确认知道如何打开主板启动菜单。

已验证的安装流程：
{{VerifiedFlow}}

安装前准备：
1. 保持电脑现有 UEFI、Secure Boot、TPM、CSM 和 SATA 设置。本功能不要求预先关闭安全启动，也不要求手动挂载 EFI 分区或分配盘符。
2. 准备可启动的 Windows 恢复 U 盘，确认帐户密码可用（不要只依赖 PIN），把这份说明保存到另一设备，查好电脑的启动菜单入口。
3. 在软件中选图并导出准备包，通过管理员只读检查，再确认恢复和单次试启动步骤；加密、多 ESP 或未知配置继续阻止安装，不为改 Logo 关闭安全功能或解密磁盘。
4. Secure Boot 开启时，阅读下方 shim 步骤；哈希信任只在安装后的首次试启动中手动确认。默认启动顺序也只在单次验证成功后再调整。
5. 安装器的卷 GUID 路径兼容设置已随 setup.exe.config 提供，仅对这个程序生效，不修改 Windows 注册表或 BIOS。若出现 “The ESP path doesn't look like an EFI System Partition.”，保留操作详情并使用新版本重新检查；不要格式化分区、关闭 Secure Boot 或手动分配盘符来绕过。

本软件仅支持当前已为 UEFI x64、单 Windows 启动项、单 EFI 系统分区且系统盘完全未加密的环境。加密或未知状态阻止安装。不要为这个功能解密系统盘、清空 TPM 或删除现有启动项。
EFI 分区可用空间须至少 20 MB；空间不足或未知时停止，不删除不明文件腾空间。
按钮禁用时先查看“安装条件”；需要导出准备包、通过十分钟内的管理员检查并完成确认。安全启动开启时还需要 shim 确认。勾选不能绕过硬件检查。
额外固件启动项不一定代表双系统，也可能是 USB、网络或厂商功能。请查看“启动项详情”中的编号、名称与可解析路径；名称不能证明安全。
支持完整核验后的 Insyde 通用设备入口 Boot2001 / Boot2002 / Boot2003：固件厂商、固定编号 / 名称、属性、结束节点、RC 参数及所有字节必须一致。安装和恢复保留 USB、光驱及网络入口，不擅自加入或移出它们。名称相同但有实际文件路径或其他数据时仍阻止；不提供强制放行。安装前再次读取，已核验设备项变化时停止。
只在 EFI\HackBGRT 目录安装官方文件，并追加独立启动项到原启动顺序末尾。Windows Boot Manager 保持默认，Windows 启动器文件保持原样。
不要手动运行原版 setup.exe 的覆盖 Windows 启动器选项；本软件不提供该方式。恢复只移除本软件有完整凭据的启动项，不整体导入旧 BCD。

BIOS / UEFI 引导：
从 Windows 进入固件：先保存工作，设置 → 系统 → 恢复 → 高级启动 / 立即重新启动 → 疑难解答 → 高级选项 → UEFI 固件设置 → 重启。这里会重启电脑，需由你手动操作；本软件不自动执行。
1. 保持现在的 UEFI / CSM / Legacy / SATA 设置。不要为了改 Logo 切换启动模式或清空 Secure Boot 密钥。
2. 安装成功后，用厂商指定的启动菜单键选择一次 HackBGRT。不同型号按键和菜单名称不同，请查主板/电脑官方手册。

首次启动：安全验证确认
{{FirstBootGuide}}

让每次开机都生效
{{PermanentGuide}}
快速启动可能令“关机再开机”的效果不同。需要时在 Windows 电源选项中了解快速启动设置，本软件不自动修改它。

图片：
选择本地静态 PNG / JPEG / BMP，最多 16 MB、2000 万像素、每边最多 8192。软件保留比例缩放，透明区域铺黑，生成正向高度、24 位未压缩、54 字节头的 BMP3。
建议 320×180 或 256×256 画布，黑色背景、图案居中。最大 600×360。UEFI 分辨率可能低于桌面；桌面预览仅为示意，保留当前固件分辨率，不强制切换显示模式。
准备包是安装材料文件夹，不是恢复 U 盘，也不包含本机原启动状态备份。包含转换后的 splash.bmp、配置、官方工具、许可证和操作说明。
当前版本必须先导出才能安装；安装读取包内已校验的图片，管理员辅助进程另从内置资源校验并解包官方工具。只导出不更改启动设置，也不会启用 Logo。
导出后可删除原图；安装与试启动完成前保留准备包，删包或改图需重新导出。安装成功且试启动验证后，EFI 中已有副本，准备包可以删除。
本机恢复记录在管理员安装时写入 %ProgramData%\WinBootVideo-BootLogo，和准备包分别保存。保留这些记录，并将恢复说明另存到另一设备。

恢复：
若试启动失败或卡住，用固件启动菜单选择 Windows Boot Manager。进 Windows 后打开本软件的“恢复原 Logo”。
若曾手动调整默认顺序，先把 Windows Boot Manager 改回第一位。保留恢复 U 盘上的这份说明，不能只依赖故障电脑上的文件。
恢复时软件先从启动顺序移除自己的项，再删除自己的项；Logo 文件保留在 EFI\HackBGRT，防止固件残留引用变为悬空路径。
若恢复记录丢失、文件或固件项被其他软件修改，自动恢复会停止。请按官方说明进行人工恢复；不要对不明分区执行格式化、覆盖引导器或删除操作。
BitLocker / 设备加密会因启动链变化进入恢复，初版不支持加密系统。恢复密钥应保存到另一设备，本软件不读取或保存恢复密钥。
更换启动链还可能影响 Windows Hello / PIN 和依赖 TPM 的反作弊。安装前确认现有帐户密码可用；PIN 不可用时使用帐户密码登录并恢复 Logo。不要清空 TPM 解决此问题。

官方发布：https://github.com/Metabolix/HackBGRT/releases/tag/v2.6.0
官方 Secure Boot 指引：https://github.com/Metabolix/HackBGRT/blob/v2.6.0/shim.md
BitLocker 恢复说明：https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview
微软固件入口说明：https://support.microsoft.com/en-us/windows/security/devicesecurity/windows-11-and-secure-boot
HackBGRT MIT 许可证、GNU-EFI 说明与 shim 版权均保留在准备包中。已验证的实机流程不代替其他设备的首次试启动或恢复准备。
""";
}
