using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinBootVideo.Core;

public sealed class BootLogoRequest
{
    public string Action { get; set; } = "probe";
    public string ImageFile { get; set; } = "";
    public string ImageHash { get; set; } = "";
    public bool RescueReady { get; set; }
    public bool ManualTrialAccepted { get; set; }
    public bool ShimUnderstood { get; set; }
}
public sealed class BootLogoResponse
{
    public bool AlreadyInstalled { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public BootLogoEnvironment? Environment { get; set; }
    public string RecoveryDirectory { get; set; } = "";
}
public sealed class BootLogoJournal
{
    public int Schema { get; set; } = 1;
    public string Phase { get; set; } = "Prepared";
    public string EspVolume { get; set; } = "";
    public string PartitionGuid { get; set; } = "";
    public string RecoveryDirectory { get; set; } = "";
    public string WindowsLoaderHash { get; set; } = "";
    public BootLogoRegistration.Plan? Plan { get; set; }
    public Dictionary<string, string> InstalledFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Error { get; set; } = "";
}

/// <summary>File-only official installer followed by a checked, default-preserving registration.</summary>
public static class BootLogoInstaller
{
    public static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinBootVideo-BootLogo");
    public static string StateFile => Path.Combine(StateRoot, "current.json");
    private static readonly string[] ExpectedFiles = { "config.txt", "splash.bmp", "loader.efi", "grubx64.efi", "mmx64.efi", "certificate.cer" };
    public static BootLogoJournal? ReadJournal() => File.Exists(StateFile) ?
        JsonSerializer.Deserialize<BootLogoJournal>(File.ReadAllText(StateFile), Settings.JsonOptions) : null;
    public static async Task<BootLogoResponse> ExecuteAsync(BootLogoRequest request)
    {
        if (request.Action != "probe" && request.Action != "install" && request.Action != "restore") throw new InvalidDataException("操作不受支持。");
        var environment = await BootLogoProbe.ReadAsync();
        if (request.Action == "probe")
        {
            if (environment.Blockers().Count == 0)
            {
                try { return ProbeFirmware(environment, new BootLogoNativeFirmware(), ReadJournal(), journal => VerifyInstalledJournal(environment, journal)); }
                catch (Exception ex) { environment.Error = ex.Message; }
            }
            return new BootLogoResponse { Success = false, Environment = environment, Message = string.Join("\n", environment.Blockers()) };
        }
        // Restoration can be necessary even if encryption or Secure Boot was changed after installation.
        if (!environment.Administrator || !environment.Uefi || !environment.X64) throw new InvalidOperationException("恢复 / 安装需要管理员权限与 UEFI x64。");
        using var mutex = new System.Threading.Mutex(false, @"Global\WinBootVideo.BootLogo.Operation");
        bool entered = false;
        try
        {
            try { entered = mutex.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { entered = true; }
            if (!entered) throw new IOException("另一个 Logo 操作正在运行。");
            ProtectStateDirectory();
            if (request.Action == "restore") return Restore(environment);
            var blockers = environment.Blockers();
            if (blockers.Count != 0) throw new InvalidOperationException(string.Join("\n", blockers));
            if (!request.RescueReady || !request.ManualTrialAccepted || (environment.SecureBoot == true && !request.ShimUnderstood))
                throw new InvalidOperationException("请先完成恢复介质、单次试启动和 Secure Boot 引导确认。");
            return await InstallAsync(request, environment);
        }
        finally { if (entered) mutex.ReleaseMutex(); }
    }
    internal static BootLogoResponse ProbeFirmware(BootLogoEnvironment environment, IBootLogoFirmware firmware,
        BootLogoJournal? journal, Action<BootLogoJournal> verifyInstalledFiles)
    {
        if (environment.Blockers().Count != 0)
            return new BootLogoResponse { Environment = environment, Message = string.Join("\n", environment.Blockers()) };
        try
        {
            var registration = new BootLogoRegistration(firmware, environment.FirmwareManufacturer);
            environment.FirmwareDetails = registration.Inspect();
            if (journal != null && journal.Phase != "Restored")
            {
                if (journal.Phase != "Installed") throw new InvalidDataException("存在未完成的 Logo 操作记录，请先查看恢复说明；不要重复安装。");
                if (journal.Schema != 1 || journal.Plan == null || !Guid.TryParse(journal.PartitionGuid, out var partition) ||
                    partition != Guid.Parse(environment.EspPartitionGuid) || !journal.EspVolume.Equals(environment.EspVolume, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Logo 恢复记录与当前分区不一致，不能确认安装状态。");
                if (journal.InstalledFiles.Count != ExpectedFiles.Length || ExpectedFiles.Any(name => !journal.InstalledFiles.ContainsKey(name)) ||
                    journal.InstalledFiles.Values.Append(journal.WindowsLoaderHash).Any(hash => hash == null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))))
                    throw new InvalidDataException("Logo 文件校验记录不完整。");
                verifyInstalledFiles(journal);
                string message = registration.VerifyInstalled(journal.Plan, partition);
                environment.FirmwareDetails += "\n\n" + message;
                return new BootLogoResponse { Success = true, AlreadyInstalled = true, Environment = environment,
                    RecoveryDirectory = journal.RecoveryDirectory, Message = message };
            }
            registration.Prepare(Guid.Parse(environment.EspPartitionGuid));
        }
        catch (Exception ex) { environment.Error = ex.Message; }
        var blockers = environment.Blockers();
        return new BootLogoResponse { Success = blockers.Count == 0, Environment = environment,
            Message = blockers.Count == 0 ? "只读检查通过；尚未安装。正式安装时会重新检查。" : string.Join("\n", blockers) };
    }
    private static void VerifyInstalledJournal(BootLogoEnvironment environment, BootLogoJournal journal)
    {
        string root = Path.GetFullPath(StateRoot), recovery = Path.GetFullPath(journal.RecoveryDirectory);
        if (!recovery.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(recovery), "N", out _) || Path.GetDirectoryName(recovery) != root)
            throw new InvalidDataException("Logo 恢复记录路径无效。");
        string mirror = Path.Combine(recovery, "recovery.json"), backup = Path.Combine(recovery, "bootmgfw.efi.backup");
        foreach (string directory in new[] { root, recovery })
        {
            BootLogoPaths.OrdinaryDirectory(directory);
            CheckReadOnlyProtection(new DirectoryInfo(directory).GetAccessControl());
        }
        foreach (string file in new[] { StateFile, mirror, backup })
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("恢复记录不允许为链接。");
            CheckReadOnlyProtection(new FileInfo(file).GetAccessControl());
        }
        if (!File.ReadAllBytes(StateFile).SequenceEqual(File.ReadAllBytes(mirror))) throw new InvalidDataException("Logo 恢复记录副本不一致，停止检查。");
        string windows = Path.Combine(environment.EspVolume, @"EFI\Microsoft\Boot\bootmgfw.efi");
        VerifyInstalledFiles(journal, windows, Path.Combine(environment.EspVolume, @"EFI\HackBGRT"), backup);
    }
    internal static void VerifyInstalledFiles(BootLogoJournal journal, string windows, string logo, string backup)
    {
        if (!BootLogoPackage.HashFile(backup).Equals(journal.WindowsLoaderHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Windows 启动器恢复备份校验失败。");
        CheckWindows(windows, journal.WindowsLoaderHash);
        if ((File.GetAttributes(logo) & FileAttributes.ReparsePoint) != 0 || ExpectedFiles.Any(name => (File.GetAttributes(Path.Combine(logo, name)) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Logo 安装目录不允许为链接。");
        VerifyFiles(logo, journal.InstalledFiles);
    }
    internal static void CheckReadOnlyProtection(FileSystemSecurity security)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!admins.Equals(owner) && !system.Equals(owner)) throw new InvalidDataException("恢复资料不属于管理员或系统。");
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0 && !admins.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))
                throw new InvalidDataException("恢复资料允许普通用户写入，不能据此确认安装状态。");
    }
    private static void ProtectStateDirectory()
    {
        BootLogoPaths.OrdinaryDirectory(StateRoot);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (Directory.Exists(StateRoot))
        {
            var info = new DirectoryInfo(StateRoot);
            var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (!admins.Equals(owner) && !system.Equals(owner)) throw new InvalidDataException("恢复记录目录不属于管理员或系统，拒绝使用。");
        }
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(admins);
        foreach (var sid in new[] { admins, system }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(StateRoot);
        if (directory.Exists) directory.SetAccessControl(security); else directory.Create(security);
        if (File.Exists(StateFile) && (File.GetAttributes(StateFile) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("恢复记录不允许为链接。");
    }
    private static void Save(BootLogoJournal journal)
    {
        DurableWrite(StateFile, journal);
        DurableWrite(Path.Combine(journal.RecoveryDirectory, "recovery.json"), journal);
    }
    private static void DurableWrite(string target, BootLogoJournal journal)
    {
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, journal, Settings.JsonOptions); file.Flush(true); }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<BootLogoResponse> InstallAsync(BootLogoRequest request, BootLogoEnvironment env)
    {
        var previous = ReadJournal();
        if (previous != null && previous.Phase != "Restored") throw new InvalidOperationException("已有 Logo 安装或未完成的恢复记录，请先恢复原 Logo；不要重复安装。");
        byte[] bmp;
        using (var input = new FileStream(request.ImageFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length > 700000) throw new InvalidDataException("转换后的 Logo 文件过大。");
            using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); bmp = bytes.ToArray();
        }
        BootLogoImage.Validate(bmp);
        if (BootLogoPackage.Hash(bmp) != request.ImageHash) throw new IOException("图片在确认后发生变化，请重新转换。");
        var firmware = new BootLogoNativeFirmware(); var registration = new BootLogoRegistration(firmware, env.FirmwareManufacturer);
        var plan = registration.Prepare(Guid.Parse(env.EspPartitionGuid));
        string esp = env.EspVolume;
        string windows = Path.Combine(esp, @"EFI\Microsoft\Boot\bootmgfw.efi");
        string logo = Path.Combine(esp, @"EFI\HackBGRT");
        if (Directory.Exists(logo))
        {
            if (previous == null || previous.EspVolume != esp || previous.InstalledFiles.Count != ExpectedFiles.Length)
                throw new InvalidDataException("EFI 分区已有 HackBGRT 文件，无法确认归属，拒绝覆盖。");
            VerifyFiles(logo, previous.InstalledFiles);
        }
        if (!File.Exists(windows)) throw new FileNotFoundException("当前 EFI 分区没有 Windows 启动器。");
        string session = Path.Combine(StateRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session);
        string stage = BootLogoPackage.Extract(Path.Combine(session, "official"), bmp);
        File.Copy(windows, Path.Combine(session, "bootmgfw.efi.backup"), false);
        using (var backup = new FileStream(Path.Combine(session, "bootmgfw.efi.backup"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) backup.Flush(true);
        string windowsHash = BootLogoPackage.HashFile(windows);
        if (BootLogoPackage.HashFile(Path.Combine(session, "bootmgfw.efi.backup")) != windowsHash) throw new IOException("Windows 启动器备份校验失败。");
        // A readable rescue note is saved before the first EFI write. Never auto-import backups.
        File.WriteAllText(Path.Combine(session, "恢复说明.txt"), BootLogoPackage.Guide);
        var journal = new BootLogoJournal { EspVolume = esp, PartitionGuid = env.EspPartitionGuid,
            Plan = plan, RecoveryDirectory = session, WindowsLoaderHash = windowsHash };
        await BootLogoInstallTransaction.RunAsync(journal, registration, Save, async () =>
        {
            // The only official setup action exposed here is FILE INSTALL. It neither enables
            // a boot entry nor overwrites Windows; all bypass/disable/overwrite actions are absent.
            await RunOfficialFileInstall(stage, esp);
            VerifyOfficialFiles(logo, stage, bmp);
            return ExpectedFiles.ToDictionary(name => name, name => BootLogoPackage.HashFile(Path.Combine(logo, name)), StringComparer.OrdinalIgnoreCase);
        }, () => CheckWindows(windows, windowsHash));
        return new BootLogoResponse { Success = true, AlreadyInstalled = true, Environment = env, RecoveryDirectory = session,
            Message = "安装并读回核验完成。Windows Boot Manager 仍是默认；请从固件启动菜单单次选择 HackBGRT 试启动。恢复记录：" + session };
    }
    private static BootLogoResponse Restore(BootLogoEnvironment env)
    {
        var journal = ReadJournal() ?? throw new InvalidDataException("没有本软件的 Logo 恢复记录。");
        if (journal.Schema != 1 || journal.Plan == null || !BootLogoPaths.IsVolumePath(journal.EspVolume) ||
            !Guid.TryParse(journal.PartitionGuid, out var partition) ||
            BootLogoEntry.Parse(journal.Plan.NewEntry.Data).Partition != partition ||
            BootLogoEntry.Parse(journal.Plan.NewEntry.Data).Path != BootLogoEntry.LogoPath)
            throw new InvalidDataException("恢复记录无效。");
        if (!journal.RecoveryDirectory.StartsWith(StateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("恢复记录路径异常。");
        BootLogoPaths.OrdinaryDirectory(journal.RecoveryDirectory);
        string windows = Path.Combine(journal.EspVolume, @"EFI\Microsoft\Boot\bootmgfw.efi");
        CheckWindows(windows, journal.WindowsLoaderHash);
        new BootLogoRegistration(new BootLogoNativeFirmware()).Restore(journal.Plan);
        CheckWindows(windows, journal.WindowsLoaderHash);
        journal.Phase = "Restored"; Save(journal);
        return new BootLogoResponse { Success = true, Environment = env, RecoveryDirectory = journal.RecoveryDirectory,
            Message = "已移除本软件的 Logo 启动项。EFI 中的 Logo 文件和恢复记录保留；如曾调整 BIOS 默认顺序，请确认 Windows Boot Manager 第一位。" };
    }
    public static string[] FileInstallArguments(string esp)
    {
        if (!BootLogoPaths.IsVolumePath(esp)) throw new InvalidDataException("仅允许已确认的 EFI 卷 GUID 路径。");
        return new[] { "batch", "is-elevated", "install", "arch=x64", "esp=" + esp };
    }
    private static async Task RunOfficialFileInstall(string stage, string esp)
    {
        if (File.ReadAllText(Path.Combine(stage, "setup.exe.config")) != BootLogoPackage.InstallerRuntimeConfig)
            throw new IOException("官方安装器的本地路径兼容配置被修改，停止安装。请使用完整的新版本软件重新准备安装。");
        var start = new ProcessStartInfo(Path.Combine(stage, "setup.exe")) { WorkingDirectory = stage,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in FileInstallArguments(esp)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动已校验的官方安装器。");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        string log = await output + "\n" + await error;
        File.WriteAllText(Path.Combine(stage, "WinBootVideo-installer-output.txt"), log);
        if (process.ExitCode != 0)
        {
            string hint = log.Contains("The ESP path doesn't look like an EFI System Partition.", StringComparison.Ordinal)
                ? "\n官方安装器无法访问已检查的 EFI 路径。请保留操作详情；这不表示需要关闭 Secure Boot、改 BIOS、格式化分区或手动分配盘符。\n" : "";
            throw new IOException("官方文件安装失败（退出码 " + process.ExitCode + "），尚未启用 Logo。" + hint + log);
        }
    }
    private static void CheckWindows(string path, string hash)
    {
        if (BootLogoPackage.HashFile(path) != hash) throw new IOException("Windows 启动器发生变化，停止操作；不会自动覆盖 Windows 启动器。");
    }
    public static void VerifyFiles(string directory, Dictionary<string, string> files)
    {
        if (files.Keys.Any(k => !ExpectedFiles.Contains(k)) || Directory.GetDirectories(directory).Length != 0 ||
            Directory.GetFiles(directory).Any(p => !files.ContainsKey(Path.GetFileName(p)))) throw new InvalidDataException("Logo 目录含未知文件，拒绝覆盖。");
        foreach (var item in files) if (BootLogoPackage.HashFile(Path.Combine(directory, item.Key)) != item.Value) throw new IOException("Logo 安装文件被修改：" + item.Key);
    }
    public static void VerifyOfficialFiles(string installed, string stage, byte[] bmp)
    {
        string[] originals = { "config.txt", "splash.bmp", @"shim-signed\shimx64.efi", @"efi-signed\bootx64.efi", @"shim-signed\mmx64.efi", "certificate.cer" };
        if (Directory.GetDirectories(installed).Length != 0 || Directory.GetFiles(installed).Any(p => !ExpectedFiles.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)))
            throw new IOException("官方安装产生了预期之外的文件，拒绝注册启动项。");
        for (int i = 0; i < ExpectedFiles.Length; i++)
        {
            string target = Path.Combine(installed, ExpectedFiles[i]);
            if (ExpectedFiles[i] == "splash.bmp")
            {
                byte[] actual = File.ReadAllBytes(target);
                if (BootLogoImage.Validate(actual) != BootLogoImage.Validate(bmp) || actual.Length != bmp.Length ||
                    !actual.AsSpan(54).SequenceEqual(bmp.AsSpan(54))) throw new IOException("Logo 像素或尺寸校验失败。");
            }
            else if (BootLogoPackage.HashFile(target) != BootLogoPackage.HashFile(Path.Combine(stage, originals[i]))) throw new IOException("官方 EFI 文件校验失败：" + ExpectedFiles[i]);
        }
    }
}

/// <summary>Ordering and rollback seam shared by the production installer and fault injection checks.</summary>
public static class BootLogoInstallTransaction
{
    public static async Task RunAsync(BootLogoJournal journal, BootLogoRegistration registration,
        Action<BootLogoJournal> persist, Func<Task<Dictionary<string, string>>> installAndVerify, Action checkWindows)
    {
        if (journal.Plan == null) throw new InvalidDataException("缺少已核验的启动项计划。");
        // If the first durable checkpoint fails, no EFI file or variable may be changed.
        persist(journal);
        try
        {
            journal.InstalledFiles = await installAndVerify(); checkWindows(); persist(journal);
            registration.Apply(journal.Plan); checkWindows(); journal.Phase = "Installed"; persist(journal);
        }
        catch (Exception ex)
        {
            journal.Phase = "Failed"; journal.Error = ex.Message;
            try { registration.Restore(journal.Plan); journal.Phase = "Restored"; }
            catch (Exception rollback) { journal.Error += "\n自动撤销没有完成：" + rollback.Message; }
            try { checkWindows(); } catch (Exception changed) { journal.Error += "\n" + changed.Message; }
            try { persist(journal); } catch (Exception storage) { journal.Error += "\n恢复记录更新失败：" + storage.Message; }
            throw new IOException(journal.Error + "\n原始恢复记录保留在：" + journal.RecoveryDirectory, ex);
        }
    }
}
