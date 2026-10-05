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
    public static string Guide => """
WinBootVideo 开机 Logo · HackBGRT 2.6.0

改变的是 UEFI 启动 Windows 时的静态 BGRT Logo。主板通电后最早的厂商 Logo 可能仍短暂出现。它与登录后视频是两个独立功能。
Windows 加载阶段即使显示的是七彩虹等厂商 Logo，也可以由 HackBGRT 替换。刚按电源就出现的固件图片不属于本功能；本软件不刷写或修改 BIOS 来彻底替换第一张图片。
软件不能保证所有固件兼容，更不能保证启动风险为零。请先准备 Windows 恢复 U 盘，并确认知道如何打开主板启动菜单。

本软件仅支持当前已为 UEFI x64、单 Windows 启动项、单 EFI 系统分区且系统盘完全未加密的环境。加密或未知状态阻止安装。不要为这个功能解密系统盘、清空 TPM 或删除现有启动项。
EFI 分区可用空间须至少 20 MB；空间不足或未知时停止，不删除不明文件腾空间。
按钮禁用时先查看“安装条件”；需要导出准备包、通过十分钟内的管理员检查并完成确认。安全启动开启时还需要 shim 确认。勾选不能绕过硬件检查。
额外固件启动项不一定代表双系统，也可能是 USB、网络或厂商功能。请查看“启动项详情”中的编号、名称与可解析路径；名称不能证明安全。当前版本尚不支持这些配置，保留阻止规则，不删除启动项或提供强制放行。
只在 EFI\HackBGRT 目录安装官方文件，并追加独立启动项到原启动顺序末尾。Windows Boot Manager 保持默认，Windows 启动器文件保持原样。
不要手动运行原版 setup.exe 的覆盖 Windows 启动器选项；本软件不提供该方式。恢复只移除本软件有完整凭据的启动项，不整体导入旧 BCD。

BIOS / UEFI 引导：
从 Windows 进入固件：先保存工作，设置 → 系统 → 恢复 → 高级启动 / 立即重新启动 → 疑难解答 → 高级选项 → UEFI 固件设置 → 重启。这里会重启电脑，需由你手动操作；本软件不自动执行。
1. 保持现在的 UEFI / CSM / Legacy / SATA 设置。不要为了改 Logo 切换启动模式或清空 Secure Boot 密钥。
2. 安装成功后，用厂商指定的启动菜单键选择一次 HackBGRT。不同型号按键和菜单名称不同，请查主板/电脑官方手册。
3. 如果 Secure Boot 已启用，保留它。第一次选择 HackBGRT 可能进入 shim 的 Verification failed / MOKManager。
   按官方 shim.md：Enroll hash from disk → 系统盘 → EFI → HackBGRT → grubx64.efi → Continue → Yes → Reboot。
   只信任该文件的哈希，不选 Enroll key from disk，不把 certificate.cer 导入固件证书库，不关闭 TPM。MOK 重启后默认仍为 Windows；再次用启动菜单选 HackBGRT，验证实际 Logo 与登录。
   若固件拒绝运行 shim 或没有相应菜单，停止并选择 Windows Boot Manager。不要关闭安全功能强行绕过。
4. 确认 Logo 和 Windows 登录都正常后，才在固件 Boot Priority / Boot Order 中把 HackBGRT 放到 Windows Boot Manager 前面。默认顺序不由本软件自动接管。
5. 先使用“重启”验证；快速启动可能令“关机再开机”的效果不同。需要时在 Windows 电源选项中了解快速启动设置，本软件不自动修改它。

图片：
选择本地静态 PNG / JPEG / BMP，最多 16 MB、2000 万像素、每边最多 8192。软件保留比例缩放，透明区域铺黑，生成正向高度、24 位未压缩、54 字节头的 BMP3。
建议 320×180 或 256×256 画布，黑色背景、图案居中。最大 600×360。UEFI 分辨率可能低于桌面；桌面预览仅为示意，保留当前固件分辨率，不强制切换显示模式。
准备包中的 splash.bmp 和 config.txt 是独立副本。删除原始图片不影响它们；安装后图片还会复制到 EFI 分区。不要删除正在准备的安装包或恢复记录。

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
HackBGRT MIT 许可证、GNU-EFI 说明与 shim 版权均保留在准备包中。此版本无虚拟机/真实固件首次启动验收记录，第一次必须用启动菜单单次试启动。
""";
}
