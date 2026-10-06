using System.Text;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinBootVideo;
using WinBootVideo.Core;

internal static class BootLogoChecks
{
    internal static readonly Guid Partition = Guid.Parse("47e0bbda-3d49-4867-a77f-cfded94a5a27");
    internal const string Volume = @"\\?\Volume{798e970d-122d-4383-9f97-3fd683c6acd9}\";
    public static byte[] WindowsEntry()
    {
        byte[] hd = new byte[42]; hd[0] = 4; hd[1] = 1; hd[2] = 42; hd[4] = 1;
        Partition.ToByteArray().CopyTo(hd, 24); hd[40] = hd[41] = 2;
        byte[] path = Encoding.Unicode.GetBytes(BootLogoEntry.WindowsPath + "\0");
        byte[] file = new byte[] { 4, 4, (byte)(path.Length + 4), 0 }.Concat(path).ToArray();
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(1u); writer.Write((ushort)(hd.Length + file.Length + 4)); writer.Write(Encoding.Unicode.GetBytes("Windows Boot Manager\0"));
        writer.Write(hd); writer.Write(file); writer.Write(new byte[] { 0x7f, 0xff, 4, 0 }); writer.Write(Encoding.ASCII.GetBytes("WINDOWS\0\x01\0\0\0"));
        return output.ToArray();
    }
    public sealed class FakeFirmware : IBootLogoFirmware
    {
        public readonly Dictionary<string, FirmwareValue> Values = new();
        public readonly List<string> Writes = new();
        public int FailAt, CorruptAt;
        public FakeFirmware()
        {
            Values["BootOrder"] = new(7, BitConverter.GetBytes((ushort)5));
            Values["BootCurrent"] = new(6, BitConverter.GetBytes((ushort)5));
            Values["Boot0005"] = new(7, WindowsEntry());
        }
        public FirmwareValue? Read(string name) => Values.TryGetValue(name, out var value) ? new(value.Attributes, value.Data.ToArray()) : null;
        public void Write(string name, FirmwareValue? value)
        {
            Writes.Add(name);
            if (FailAt == Writes.Count) throw new IOException("injected write failure");
            if (value == null) Values.Remove(name); else Values[name] = new(value.Attributes, value.Data.ToArray());
            if (CorruptAt == Writes.Count && value != null) Values[name] = new(value.Attributes, new byte[] { 9 });
        }
    }
    private static byte[] DeviceOnlyEntry(string label)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(1u); writer.Write((ushort)15); writer.Write(Encoding.Unicode.GetBytes(label + "\0"));
        writer.Write(new byte[] { 3, 15, 11, 0, 255, 255, 255, 255, 255, 255, 255 });
        writer.Write(new byte[] { 0x7f, 0xff, 4, 0 }); return output.ToArray();
    }
    private static void Reject(Action action, Action<bool, string> check, string name)
    {
        bool rejected = false; try { action(); } catch { rejected = true; } check(rejected, name);
    }
    private static readonly (ushort Number, string Raw, string Hash)[] InsydeEntries = {
        (0x2001, "AQAAAAQARQBGAEkAIABVAFMAQgAgAEQAZQB2AGkAYwBlAAAAf/8EAFJD", "0c7c99b95a506ed44fb0816181e8e3a3a02d28c14df69d7e440b1ca64fef8c74"),
        (0x2002, "AQAAAAQARQBGAEkAIABEAFYARAAvAEMARABSAE8ATQAAAH//BABSQw==", "2acfbd80332f202797d5cef9f3be599db9ed7fa826c7c8dbbb216f813a1d74b1"),
        (0x2003, "AQAAAAQARQBGAEkAIABOAGUAdAB3AG8AcgBrAAAAf/8EAFJD", "fb1bc2fbbbf7e3cc3ab77af1eb4f0fe772dace6b26c7bad60b66fc1f9d54babe")
    };
    private static FakeFirmware InsydeFirmware()
    {
        var firmware = new FakeFirmware(); firmware.Values["Boot0002"] = firmware.Values["Boot0005"]; firmware.Values.Remove("Boot0005");
        firmware.Values["BootCurrent"] = new(6, BitConverter.GetBytes((ushort)2));
        firmware.Values["BootOrder"] = new(7, new ushort[] { 2, 0x2001, 0x2002, 0x2003 }.SelectMany(BitConverter.GetBytes).ToArray());
        foreach (var entry in InsydeEntries) firmware.Values[BootLogoRegistration.Name(entry.Number)] = new(7, Convert.FromBase64String(entry.Raw));
        return firmware;
    }
    public static void RunInstalledProbe(Action<bool, string> check)
    {
        var firmware = InsydeFirmware();
        var registration = new BootLogoRegistration(firmware, "INSYDE Corp.");
        var plan = registration.Prepare(Partition);
        registration.Apply(plan);
        var journal = new BootLogoJournal { Phase = "Installed", Plan = plan, EspVolume = Volume, PartitionGuid = Partition.ToString(),
            WindowsLoaderHash = new string('a', 64), InstalledFiles = new[] { "config.txt", "splash.bmp", "loader.efi", "grubx64.efi", "mmx64.efi", "certificate.cer" }.ToDictionary(name => name, _ => new string('b', 64)) };
        journal = System.Text.Json.JsonSerializer.Deserialize<BootLogoJournal>(System.Text.Json.JsonSerializer.Serialize(journal, Settings.JsonOptions), Settings.JsonOptions)!;
        firmware.Writes.Clear();
        BootLogoEnvironment Environment() => new() { Administrator = true, Uefi = true, X64 = true, SecureBoot = true,
            OsFullyDecrypted = true, EspCount = 1, EspOnOsDisk = true, EspVolume = Volume, EspPartitionGuid = Partition.ToString(),
            EspFreeBytes = 64L * 1024 * 1024, FirmwareManufacturer = "INSYDE Corp.", CheckedUtc = DateTime.UtcNow };
        int fileChecks = 0;
        var response = BootLogoInstaller.ProbeFirmware(Environment(), firmware, journal, _ => fileChecks++);
        check(response.Success && response.AlreadyInstalled && response.Message.Contains("已安装") && response.Message.Contains("Windows Boot Manager 仍是默认"), "安装成功后的 Boot0000 不应再次被只读检查报为额外固件项：" + response.Message);
        check(fileChecks == 1 && response.Environment!.FirmwareDetails.Contains("已安装并核验"), "已有安装只读检查确实执行文件验证并在详情标明记录归属");
        check(firmware.Writes.Count == 0, "安装后状态检查不能写入固件");
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<BootLogoResponse>(System.Text.Json.JsonSerializer.Serialize(response, Settings.JsonOptions), Settings.JsonOptions)!;
        check(roundTrip.Success && roundTrip.AlreadyInstalled, "辅助报告 JSON 往返保留已安装状态");
        var savedOrder = firmware.Read("BootOrder")!;
        firmware.Values["BootCurrent"] = new(6, BitConverter.GetBytes(plan.Number));
        response = BootLogoInstaller.ProbeFirmware(Environment(), firmware, journal, _ => { });
        check(response.Success && response.AlreadyInstalled, "通过 Logo 单次试启动进入 Windows 后可以检查已有安装");
        firmware.Values["BootOrder"] = new(7, new ushort[] { plan.Number, 2, 0x2001, 0x2002, 0x2003 }.SelectMany(BitConverter.GetBytes).ToArray());
        response = BootLogoInstaller.ProbeFirmware(Environment(), firmware, journal, _ => { });
        check(response.Success && response.Message.Contains("HackBGRT 当前为默认"), "用户手动设置 Logo 为默认后仍准确显示已安装，而非要求默认 Windows");
        firmware.Values["BootOrder"] = savedOrder;
        firmware.Values["BootCurrent"] = new(6, BitConverter.GetBytes((ushort)2));
        firmware.Values["BootNext"] = new(7, BitConverter.GetBytes(plan.Number));
        check(BootLogoInstaller.ProbeFirmware(Environment(), firmware, journal, _ => { }).AlreadyInstalled, "已设置本软件单次试启动时只读识别，不改 BootNext");
        firmware.Values.Remove("BootNext");
        void MustReject(string reason, Action change, Action reset)
        {
            change();
            try { var result = BootLogoInstaller.ProbeFirmware(Environment(), firmware, journal, _ => { }); check(!result.Success && !result.AlreadyInstalled, reason); }
            finally { reset(); }
        }
        var own = firmware.Read("Boot0000")!;
        MustReject("自身启动项属性发生变化仍停止，不仅凭 HackBGRT 名称放行", () => firmware.Values["Boot0000"] = new(3, own.Data), () => firmware.Values["Boot0000"] = own);
        MustReject("自身启动项完整字节变化仍停止", () => firmware.Values["Boot0000"] = new(7, own.Data.Concat(new byte[] { 1 }).ToArray()), () => firmware.Values["Boot0000"] = own);
        MustReject("自身项缺失不会伪报已安装", () => firmware.Values.Remove("Boot0000"), () => firmware.Values["Boot0000"] = own);
        MustReject("Windows 项变化仍停止", () => firmware.Values["Boot0002"] = new(3, plan.WindowsEntry.Data), () => firmware.Values["Boot0002"] = plan.WindowsEntry);
        MustReject("记录之外新增固件项仍停止", () => firmware.Values["Boot0008"] = own, () => firmware.Values.Remove("Boot0008"));
        var device = firmware.Read("Boot2001")!;
        MustReject("记录中的 Insyde 设备项变化仍停止", () => firmware.Values["Boot2001"] = new(3, device.Data), () => firmware.Values["Boot2001"] = device);
        MustReject("启动顺序缺少自身项仍停止", () => firmware.Values["BootOrder"] = plan.OriginalOrder, () => firmware.Values["BootOrder"] = savedOrder);
        MustReject("本次从未知启动项进入时不误报已核验", () => firmware.Values["BootCurrent"] = new(6, BitConverter.GetBytes((ushort)8)), () => firmware.Values["BootCurrent"] = new(6, BitConverter.GetBytes((ushort)2)));
        MustReject("未知 BootNext 不因已有 Logo 而放行", () => firmware.Values["BootNext"] = new(7, BitConverter.GetBytes((ushort)8)), () => firmware.Values.Remove("BootNext"));
        MustReject("错误分区恢复记录不能授权同名 Logo", () => journal.PartitionGuid = Guid.NewGuid().ToString(), () => journal.PartitionGuid = Partition.ToString());
        MustReject("失败或未完成的恢复记录不能伪报已安装", () => journal.Phase = "Failed", () => journal.Phase = "Installed");
        MustReject("Restored 记录不能授权仍存在的旧 Logo", () => journal.Phase = "Restored", () => journal.Phase = "Installed");
        MustReject("损坏恢复计划仍停止", () => journal.Plan = plan with { Number = 8 }, () => journal.Plan = plan);
        MustReject("缺少文件哈希不能据此识别已有安装", () => journal.InstalledFiles.Remove("loader.efi"), () => journal.InstalledFiles["loader.efi"] = new string('b', 64));
        var noJournal = BootLogoInstaller.ProbeFirmware(Environment(), firmware, null, _ => { });
        check(!noJournal.Success && !noJournal.AlreadyInstalled, "无本软件恢复记录的同名 HackBGRT 仍作为未知额外项拒绝");
        var badFiles = BootLogoInstaller.ProbeFirmware(Environment(), firmware, journal, _ => throw new IOException("文件哈希不一致"));
        check(!badFiles.Success && !badFiles.AlreadyInstalled && badFiles.Message.Contains("文件哈希不一致"), "文件验证失败不能仅凭固件记录伪报已安装");
        check(firmware.Writes.Count == 0, "成功与全部失败的安装后检查均不修改固件");
        var security = new System.Security.AccessControl.DirectorySecurity();
        var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
        security.SetOwner(admins);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(admins, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users, System.Security.AccessControl.FileSystemRights.ReadAndExecute, System.Security.AccessControl.AccessControlType.Allow));
        BootLogoInstaller.CheckReadOnlyProtection(security);
        check(true, "管理员拥有且普通用户只读的恢复权限可核验；不写真实 ACL");
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(users, System.Security.AccessControl.FileSystemRights.Write, System.Security.AccessControl.AccessControlType.Allow));
        Reject(() => BootLogoInstaller.CheckReadOnlyProtection(security), check, "普通用户可写的恢复资料不能用于授权归属");
    }
    public static void RunInsydeEntries(Action<bool, string> check)
    {
        foreach (var entry in InsydeEntries)
            check(BootLogoPackage.Hash(Convert.FromBase64String(entry.Raw)) == entry.Hash, "夹具完整字节与用户只读报告 SHA-256 相同：" + BootLogoRegistration.Name(entry.Number));
        var firmware = InsydeFirmware(); var registration = new BootLogoRegistration(firmware, "INSYDE Corp.");
        BootLogoRegistration.Plan? plan = null; string error = "";
        try { plan = registration.Prepare(Partition); } catch (InvalidDataException ex) { error = ex.Message; }
        check(plan != null, "当前 Windows 加三个固件通用设备入口不应误拦安装计划：" + error);
        check(firmware.Writes.Count == 0, "通用设备入口支持的计划检查仍为零写入");
        var originalOrder = firmware.Read("BootOrder")!; var originals = InsydeEntries.ToDictionary(e => e.Number, e => firmware.Read(BootLogoRegistration.Name(e.Number))!);
        registration.Apply(plan!);
        check(BootLogoRegistration.Order(firmware.Read("BootOrder")).SequenceEqual(new ushort[] { 2, 0x2001, 0x2002, 0x2003, plan!.Number }),
            "保留 Windows 默认和 USB / 光驱 / 网络相对顺序，只追加 Logo 项");
        check(InsydeEntries.All(e => BootLogoRegistration.Equal(firmware.Read(BootLogoRegistration.Name(e.Number)), originals[e.Number])) &&
            firmware.Writes.SequenceEqual(new[] { BootLogoRegistration.Name(plan!.Number), "BootOrder" }), "不写入、不删除、不覆盖三个通用设备启动项");
        registration.Restore(plan!);
        check(BootLogoRegistration.Equal(firmware.Read("BootOrder"), originalOrder) && InsydeEntries.All(e => BootLogoRegistration.Equal(firmware.Read(BootLogoRegistration.Name(e.Number)), originals[e.Number])),
            "恢复只移除自己的 Logo，通用设备入口字节与原顺序保留");
        Reject(() => new BootLogoRegistration(InsydeFirmware()).Prepare(Partition), check, "固件厂商未知时不套用 Insyde 设备入口规则");
        Reject(() => new BootLogoRegistration(InsydeFirmware(), "Other firmware").Prepare(Partition), check, "非 Insyde 厂商不套用专用设备入口规则");
        check(registration.Inspect().Contains("已核验 Insyde 通用设备入口"), "只读详情明确区分已核验的设备入口与未知路径");
        var serialized = System.Text.Json.JsonSerializer.Serialize(plan, Settings.JsonOptions);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<BootLogoRegistration.Plan>(serialized, Settings.JsonOptions)!;
        check(roundTrip.PreservedEntries?.Count == 3 && InsydeEntries.All(e => BootLogoRegistration.Equal(roundTrip.PreservedEntries[e.Number], originals[e.Number])),
            "恢复记录 JSON 保留三个完整设备入口快照，数字键往返正确");
        var malformedPlan = InsydeFirmware();
        Reject(() => new BootLogoRegistration(malformedPlan, "INSYDE Corp.").Apply(roundTrip with { PreservedEntries = null }), check, "多入口计划丢失设备备份时不写入固件");
        check(malformedPlan.Writes.Count == 0, "损坏设备备份计划拒绝发生在所有写入之前");
        foreach (var entry in InsydeEntries)
        {
            foreach (string mutation in new[] { "inactive", "hidden", "path-size", "end-type", "end-subtype", "end-size", "arguments", "extra-data", "variable-attributes" })
            {
                var bad = InsydeFirmware(); byte[] raw = Convert.FromBase64String(entry.Raw);
                switch (mutation)
                {
                    case "inactive": raw[0] = 0; break;
                    case "hidden": raw[0] = 9; break;
                    case "path-size": raw[4] = 0; break;
                    case "end-type": raw[^6] = 4; break;
                    case "end-subtype": raw[^5] = 1; break;
                    case "end-size": raw[^4] = 6; break;
                    case "arguments": raw[^1] ^= 1; break;
                    case "extra-data": raw = raw.Concat(new byte[] { 0 }).ToArray(); break;
                }
                bad.Values[BootLogoRegistration.Name(entry.Number)] = new(mutation == "variable-attributes" ? 6u : 7u, raw);
                Reject(() => new BootLogoRegistration(bad, "INSYDE Corp.").Prepare(Partition), check,
                    "同名固件入口数据不符合已核验结构时阻止：" + BootLogoRegistration.Name(entry.Number) + " / " + mutation);
                check(bad.Writes.Count == 0, "不明同名项没有写入：" + BootLogoRegistration.Name(entry.Number) + " / " + mutation);
            }
        }
        var swapped = InsydeFirmware(); swapped.Values["Boot2001"] = swapped.Values["Boot2002"];
        Reject(() => new BootLogoRegistration(swapped, "INSYDE Corp.").Prepare(Partition), check, "通用设备完整数据与固定编号不一致也不能放行");
        var unknownSlot = InsydeFirmware(); unknownSlot.Values["Boot2004"] = unknownSlot.Values["Boot2001"];
        unknownSlot.Values["BootOrder"] = new(7, unknownSlot.Read("BootOrder")!.Data.Concat(BitConverter.GetBytes((ushort)0x2004)).ToArray());
        Reject(() => new BootLogoRegistration(unknownSlot, "INSYDE Corp.").Prepare(Partition), check, "相同通用数据出现在未知编号时仍阻止，不扩大到所有 Boot200x");
        var namedLoader = InsydeFirmware();
        using (var output = new MemoryStream())
        using (var writer = new BinaryWriter(output))
        {
            var nodes = BootLogoEntry.Parse(WindowsEntry()).Nodes;
            writer.Write(1u); writer.Write((ushort)nodes.Sum(n => n.Length)); writer.Write(Encoding.Unicode.GetBytes("EFI USB Device\0"));
            foreach (var node in nodes) writer.Write(node);
            namedLoader.Values["Boot2001"] = new(7, output.ToArray());
        }
        Reject(() => new BootLogoRegistration(namedLoader, "INSYDE Corp.").Prepare(Partition), check, "同名 USB 项包含实际磁盘和 EFI 文件路径时仍阻止，标签不能授权");
        var orphaned = InsydeFirmware(); orphaned.Values["BootOrder"] = new(7, BitConverter.GetBytes((ushort)2));
        var orphanReg = new BootLogoRegistration(orphaned, "INSYDE Corp."); var orphanPlan = orphanReg.Prepare(Partition); orphanReg.Apply(orphanPlan);
        check(orphanPlan.PreservedEntries?.Count == 3 && BootLogoRegistration.Order(orphaned.Read("BootOrder")).SequenceEqual(new ushort[] { 2, orphanPlan.Number }),
            "未列入 BootOrder 的已知设备入口也保留，不擅自加入启动顺序"); orphanReg.Restore(orphanPlan);
        var changedDevice = InsydeFirmware(); var changedReg = new BootLogoRegistration(changedDevice, "INSYDE Corp."); var changedPlan = changedReg.Prepare(Partition);
        changedDevice.Values["Boot2001"] = new(7, WindowsEntry());
        Reject(() => changedReg.Apply(changedPlan), check, "检查后设备入口被其他工具修改时阻止安装");
        check(changedDevice.Writes.Count == 0, "设备入口变化在首次固件写入前被发现");
        foreach (int fail in new[] { 1, 2 })
        {
            var failed = InsydeFirmware(); var failedReg = new BootLogoRegistration(failed, "INSYDE Corp."); var failedPlan = failedReg.Prepare(Partition); failed.FailAt = fail;
            Reject(() => failedReg.Apply(failedPlan), check, "带通用设备入口的注册失败明确报错：" + fail); failed.FailAt = 0; failedReg.Restore(failedPlan);
            check(BootLogoRegistration.Equal(failed.Read("BootOrder"), originalOrder) && InsydeEntries.All(e => BootLogoRegistration.Equal(failed.Read(BootLogoRegistration.Name(e.Number)), originals[e.Number])),
                "带通用设备入口的注册失败撤销后保留原入口与顺序：" + fail);
        }
        var later = InsydeFirmware(); var laterReg = new BootLogoRegistration(later, "INSYDE Corp."); var laterPlan = laterReg.Prepare(Partition); laterReg.Apply(laterPlan);
        later.Values["Boot2001"] = new(7, WindowsEntry());
        new BootLogoRegistration(later).Restore(laterPlan);
        check(later.Read("Boot2001")!.Data.SequenceEqual(WindowsEntry()) && BootLogoRegistration.Equal(later.Read("BootOrder"), originalOrder),
            "恢复不覆盖后来被其他工具修改的设备项，不因厂商未知而阻止移除自身 Logo");
    }
    public static void RunSafety(Action<bool, string> check)
    {
        var env = new BootLogoEnvironment { Administrator = true, Uefi = true, X64 = true, SecureBoot = true, OsFullyDecrypted = true,
            EspCount = 1, EspOnOsDisk = true, EspVolume = Volume, EspPartitionGuid = Partition.ToString(), EspFreeBytes = 64L * 1024 * 1024 };
        check(env.Blockers().Count == 0, "已知 UEFI x64 未加密单 ESP 可使用 shim 路径");
        env.EspFreeBytes = 19L * 1024 * 1024; check(env.Blockers().Count > 0, "EFI 空间不足时不复制文件，保留 Windows 更新余量");
        env.EspFreeBytes = null; check(env.Blockers().Count > 0, "EFI 可用空间未知时停止，不删不明文件腾空间"); env.EspFreeBytes = 64L * 1024 * 1024;
        env.OsFullyDecrypted = null; check(env.Blockers().Count > 0, "加密状态未知阻止安装");
        env.OsFullyDecrypted = false; check(env.Blockers().Count > 0, "加密中或保护已暂停仍阻止安装");
        env.OsFullyDecrypted = true; env.SecureBoot = null; check(env.Blockers().Count > 0, "Secure Boot 未知阻止安装");
        env.SecureBoot = true; env.EspCount = 2; check(env.Blockers().Count > 0, "多 ESP 阻止安装");
        env.EspCount = 1; env.EspOnOsDisk = false; check(env.Blockers().Count > 0, "跨磁盘 ESP 阻止安装");
        env.EspOnOsDisk = true; env.Administrator = false; check(env.Blockers().Count > 0, "非管理员的初步信息不能放行");
        string[] args = BootLogoInstaller.FileInstallArguments(Volume);
        check(args.SequenceEqual(new[] { "batch", "is-elevated", "install", "arch=x64", "esp=" + Volume }), "官方调用只有文件安装，不含 enable / overwrite / allow / disable / reboot");
        foreach (string unsafePath in new[] { @"C:\", @"Z:\", @"\\server\esp\", Volume + " install enable-overwrite", @"\\?\Volume{bad}\" })
            Reject(() => BootLogoInstaller.FileInstallArguments(unsafePath), check, "拒绝未确定的 ESP 或参数注入：" + unsafePath);
        foreach (string relative in new[] { "../escape", "a/../../b", "C:/x", "a\\b", "/a", "a//b", "a/./b", "file:stream" })
            Reject(() => BootLogoPackage.SafeChild(Path.GetTempPath(), relative), check, "拒绝 ZIP 路径逃逸：" + relative);
        check(BootLogoPackage.Hash(BootLogoPackage.Archive()) == BootLogoPackage.ArchiveHash, "内置官方 ZIP 与 GitHub v2.6.0 SHA-256 一致");
        byte[] original = WindowsEntry(); var parsed = BootLogoEntry.Parse(original);
        check(parsed.Path == BootLogoEntry.WindowsPath && parsed.Partition == Partition, "解析 Windows GPT 设备路径");
        byte[] newEntry = BootLogoEntry.Create(original, Partition);
        var logo = BootLogoEntry.Parse(newEntry);
        check(logo.Path == BootLogoEntry.LogoPath && logo.Partition == Partition && logo.Label == "HackBGRT" && newEntry.Length < original.Length, "新项克隆同一分区，只替换文件并清除 shim 不接受的 MS 加载参数");
        check(original.SequenceEqual(WindowsEntry()), "构建新项没有修改原 Windows 字节");
        Reject(() => BootLogoEntry.Create(original, Guid.NewGuid()), check, "错误 ESP 分区 GUID 阻止注册");
        for (int length = 0; length < original.Length - 12; length++)
            Reject(() => BootLogoEntry.Parse(original.AsSpan(0, length).ToArray()), check, "截断 EFI 启动项被拒绝：" + length);
        var bad = original.ToArray(); bad[4] = 255; bad[5] = 255;
        Reject(() => BootLogoEntry.Parse(bad), check, "越界设备路径长度被拒绝");
        var firmware = new FakeFirmware(); var registration = new BootLogoRegistration(firmware);
        var plan = registration.Prepare(Partition);
        check(firmware.Writes.Count == 0, "准备注册计划只读固件");
        registration.Apply(plan);
        check(BootLogoRegistration.Order(firmware.Read("BootOrder")).SequenceEqual(new ushort[] { 5, plan.Number }), "仅追加到末尾，Windows 保持默认");
        check(firmware.Read("Boot0005")!.Data.SequenceEqual(original) && firmware.Writes.SequenceEqual(new[] { BootLogoRegistration.Name(plan.Number), "BootOrder" }), "安装从未写入 Windows Boot####、BootNext 或 BCD");
        registration.Restore(plan);
        check(firmware.Read(BootLogoRegistration.Name(plan.Number)) == null && BootLogoRegistration.Order(firmware.Read("BootOrder")).SequenceEqual(new ushort[] { 5 }), "恢复先移除启动顺序再删除自己项");
        int written = firmware.Writes.Count; registration.Restore(plan); check(firmware.Writes.Count == written, "重复恢复不写固件");
        foreach (int fail in new[] { 1, 2 })
        {
            var f = new FakeFirmware { FailAt = fail }; var r = new BootLogoRegistration(f); var p = r.Prepare(Partition);
            Reject(() => r.Apply(p), check, "第 " + fail + " 次注册写入失败不会伪报成功"); f.FailAt = 0; r.Restore(p);
            check(BootLogoRegistration.Order(f.Read("BootOrder")).SequenceEqual(new ushort[] { 5 }) && f.Read("Boot0005")!.Data.SequenceEqual(original), "注册失败后撤销，Windows 路径及默认保持原样：" + fail);
        }
        var corrupt = new FakeFirmware { CorruptAt = 1 }; var corruptReg = new BootLogoRegistration(corrupt); var corruptPlan = corruptReg.Prepare(Partition);
        Reject(() => corruptReg.Apply(corruptPlan), check, "固件写入返回成功但读回数据错误也阻止启用");
        check(corrupt.Writes.Count == 1 && BootLogoRegistration.Order(corrupt.Read("BootOrder"))[0] == 5, "未通过读回核验绝不改 BootOrder");
        Reject(() => corruptReg.Restore(corruptPlan), check, "自己槽位内容异常时停止，不能误删未知项");
        var race = new FakeFirmware(); var raceReg = new BootLogoRegistration(race); var racePlan = raceReg.Prepare(Partition);
        race.Values[BootLogoRegistration.Name(racePlan.Number)] = new(7, original);
        Reject(() => raceReg.Apply(racePlan), check, "检查后空槽被占用时不覆盖"); check(race.Writes.Count == 0, "槽位竞争失败保持零写入");
        Reject(() => raceReg.Restore(racePlan), check, "恢复不删除占用原槽位的外来启动项");
        var changed = new FakeFirmware(); var changedReg = new BootLogoRegistration(changed); var changedPlan = changedReg.Prepare(Partition);
        changed.Values["Boot0005"] = new(7, newEntry);
        Reject(() => changedReg.Apply(changedPlan), check, "Windows 启动项被外部修改时阻止安装");
        check(changed.Writes.Count == 0, "Windows 变化时不自动覆盖或恢复旧启动器");
        var future = new FakeFirmware(); var futureReg = new BootLogoRegistration(future); var futurePlan = futureReg.Prepare(Partition); futureReg.Apply(futurePlan);
        future.Values["Boot0008"] = new(7, original); future.Values["BootOrder"] = new(7, new ushort[] { futurePlan.Number, 5, 8 }.SelectMany(BitConverter.GetBytes).ToArray());
        futureReg.Restore(futurePlan);
        check(BootLogoRegistration.Order(future.Read("BootOrder")).SequenceEqual(new ushort[] { 5, 8 }) && future.Read("Boot0008") != null, "恢复保留后来添加的外来项与相对顺序，不整体还原旧 BootOrder");
        var next = new FakeFirmware(); next.Values["BootNext"] = new(7, BitConverter.GetBytes((ushort)5));
        Reject(() => new BootLogoRegistration(next).Prepare(Partition), check, "已有 BootNext 时拒绝首次安装");
        var malformed = new FakeFirmware(); var mr = new BootLogoRegistration(malformed); var mp = mr.Prepare(Partition);
        Reject(() => mr.Apply(mp with { Number = mp.WindowsNumber }), check, "损坏计划不能覆盖 Windows 自身槽位");
        Reject(() => mr.Restore(mp with { Number = mp.WindowsNumber }), check, "损坏恢复计划不能删除 Windows 自身槽位");
        check(malformed.Writes.Count == 0, "损坏计划恢复与安装均零写入");
        var pending = new FakeFirmware(); var pendingReg = new BootLogoRegistration(pending); var pendingPlan = pendingReg.Prepare(Partition); pendingReg.Apply(pendingPlan);
        pending.Values["BootNext"] = new(7, BitConverter.GetBytes(pendingPlan.Number));
        int writesBefore = pending.Writes.Count;
        Reject(() => pendingReg.Restore(pendingPlan), check, "BootNext 仍引用自己的 Logo 时恢复停止，避免悬空启动项");
        check(pending.Writes.Count == writesBefore, "BootNext 恢复拒绝在任何固件写入之前");
        pending.Values["BootNext"] = new(7, BitConverter.GetBytes((ushort)5)); pendingReg.Restore(pendingPlan);
        check(BitConverter.ToUInt16(pending.Read("BootNext")!.Data) == 5, "恢复保留指向其他项的 BootNext，不覆盖外来设置");
        var extra = new FakeFirmware(); extra.Values["Boot0007"] = new(7, original);
        Reject(() => new BootLogoRegistration(extra).Prepare(Partition), check, "存在其他未引用固件项也停止，避免覆盖多系统 / 旧安装");
        check(extra.Writes.Count == 0, "不支持的多启动环境只读检查，不删其他项来腾槽位");
        var devices = new FakeFirmware(); devices.Values["Boot0007"] = new(7, DeviceOnlyEntry("UEFI USB Device"));
        devices.Values["BootOrder"] = new(7, new ushort[] { 5, 7 }.SelectMany(BitConverter.GetBytes).ToArray());
        var inspector = new BootLogoRegistration(devices); string report = inspector.Inspect();
        check(report.Contains("Boot0007") && report.Contains("UEFI USB Device") && report.Contains(BootLogoEntry.WindowsPath) && report.Contains("不作为安装或删除依据"),
            "只读诊断列出实际编号、设备名称、Windows 路径与名称判断边界");
        check(devices.Writes.Count == 0, "启动项详情仅执行读取，不注册、不删除、不调整顺序");
        string blockedMessage = ""; try { inspector.Prepare(Partition); } catch (InvalidDataException ex) { blockedMessage = ex.Message; }
        check(blockedMessage.Contains("Boot0007") && blockedMessage.Contains("UEFI USB Device") && blockedMessage.Contains("不一定代表双系统"),
            "额外项被阻止时显示具体项，不将 USB 名称误当作已验证的安全项或双系统");
        var mislabeled = new FakeFirmware(); mislabeled.Values["Boot0007"] = new(7, DeviceOnlyEntry("Windows Boot Manager"));
        Reject(() => new BootLogoRegistration(mislabeled).Prepare(Partition), check, "名称伪装成 Windows 也不能绕过未知设备路径保护");
        check(!BootLogoEntry.Describe(DeviceOnlyEntry("设备\n伪造第二行")).Contains("设备\n"), "诊断名称控制字符清理，不能伪造报告行");
        check(BootLogoEntry.Describe(new byte[] { 1 }).Contains("无法解析"), "损坏设备项仍可报告，诊断不读取越界");
        Type privilege = typeof(BootLogoNativeFirmware).GetNestedType("Privilege", System.Reflection.BindingFlags.NonPublic)!;
        check(System.Runtime.InteropServices.Marshal.SizeOf(privilege) == 16 && System.Runtime.InteropServices.Marshal.OffsetOf(privilege, "Luid").ToInt32() == 4,
            "TOKEN_PRIVILEGES x64 ABI 大小与 LUID 偏移符合 Win32 结构，非托管 long 自然对齐");
        foreach (int fail in new[] { 3, 4 })
        {
            var f = new FakeFirmware(); var r = new BootLogoRegistration(f); var p = r.Prepare(Partition); r.Apply(p); f.FailAt = fail;
            Reject(() => r.Restore(p), check, "恢复第 " + (fail - 2) + " 步失败明确报错");
            check(f.Read(BootLogoRegistration.Name(p.Number)) != null && f.Read("Boot0005")!.Data.SequenceEqual(original), "恢复失败保留独立项和 Windows；不删除仍可能被引用的 EFI 文件");
            f.FailAt = 0; r.Restore(p); check(f.Read(BootLogoRegistration.Name(p.Number)) == null, "恢复中断后可以再次恢复");
        }
    }
    public static Task RunGui(Action<bool, string> check, string work, string? installedScreenshot = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            App? app = null;
            try
            {
                app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                byte[] rgba = new byte[80 * 40 * 4];
                for (int i = 0; i < rgba.Length; i += 4) { rgba[i + 2] = 255; rgba[i + 3] = 128; }
                var source = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Bgra32, null, rgba, 80 * 4);
                string input = Path.Combine(work, "半透明 Logo.png"); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(source));
                using (var file = File.Create(input)) png.Save(file);
                int probeCount = 0, installCount = 0, restoreCount = 0; bool rejectedProbe = false, staleProbe = false, installedRecord = false;
                var window = new BootLogoWindow(new AppPaths(Path.Combine(work, "data")), request =>
                {
                    if (request.Action == "install") { installCount++; installedRecord = true; return Task.FromResult(new BootLogoResponse { Success = true, AlreadyInstalled = true, Message = "模拟安装完成" }); }
                    if (request.Action == "restore") { restoreCount++; installedRecord = false; return Task.FromResult(new BootLogoResponse { Success = true, Message = "模拟恢复完成" }); }
                    probeCount++;
                    return Task.FromResult(new BootLogoResponse { Success = probeCount > 1 && !rejectedProbe, AlreadyInstalled = installedRecord,
                        Message = installedRecord ? "已安装并核验本软件的 Boot0000。Windows Boot Manager 仍是默认；请单次选择 HackBGRT 试启动。" : probeCount > 1 ? "模拟检查通过" : "模拟未知加密",
                        Environment = new BootLogoEnvironment { Administrator = true, Uefi = true, X64 = true, SecureBoot = true,
                            OsFullyDecrypted = probeCount > 1 ? true : null, EspCount = 1, EspOnOsDisk = true, EspVolume = Volume,
                            EspPartitionGuid = Partition.ToString(), EspFreeBytes = 64L * 1024 * 1024,
                            FirmwareDetails = "模拟 Boot0007 设备项 · 无真实固件调用", CheckedUtc = DateTime.UtcNow.AddMinutes(staleProbe ? -11 : 0) } });
                }, _ => true, () => installedRecord ? new BootLogoJournal { Phase = "Installed" } : null)
                { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                var install = (Button)window.FindName("InstallLogoButton"); var export = (Button)window.FindName("ExportButton");
                var probe = (Button)window.FindName("ProbeButton");
                var rescue = (CheckBox)window.FindName("RescueCheck"); var trial = (CheckBox)window.FindName("TrialCheck"); var shim = (CheckBox)window.FindName("ShimCheck");
                bool started = false;
                window.ContentRendered += (_, _) =>
                {
                    if (started) return; started = true;
                    try
                    {
                        check(!install.IsEnabled && !export.IsEnabled, "GUI 初次打开不启用安装与导出");
                        var hint = window.FindName("InstallHint") as TextBlock;
                        check(hint != null && hint.Text.Contains("选择图片") && hint.Text.Contains("只读检查"), "GUI 安装按钮禁用时直接列出缺少的步骤，不让用户猜原因");
                        window.SelectImage(input); check(export.IsEnabled && !install.IsEnabled, "GUI 选图后可导出，仍不能安装");
                        byte[] bmp = BootLogoImage.Convert(input); var size = BootLogoImage.Validate(bmp);
                        check(size == (320, 180) && bmp[54] == 0, "实际 WPF 转换生成推荐画布、黑边与 BMP3");
                        int center = 54 + (90 * 960) + 160 * 3;
                        check(bmp[center + 2] is >= 126 and <= 129 && bmp[center] == 0, "PNG 半透明红色正确合成到黑底，输出无透明通道");
                        byte[] rows = new byte[100 * 32]; rows[2] = 255; rows[31 * 100] = 255;
                        byte[] padded = BootLogoImage.EncodeBgr24(33, 32, rows, 100);
                        check(padded[54] == 255 && padded[54 + 31 * 100 + 2] == 255 && padded[153] == 0, "非四字节整齐宽度正确补零，BMP 行按自下而上存储");
                        for (int offset = 0; offset < 54; offset += 2)
                        {
                            if (offset is >= 38 and <= 44) continue;
                            var malformed = bmp.ToArray(); malformed[offset] ^= 0xff;
                            Reject(() => BootLogoImage.Validate(malformed), check, "损坏 BMP 头字段被拒绝：" + offset);
                        }
                        Reject(() => BootLogoImage.Validate(bmp.AsSpan(0, bmp.Length - 1).ToArray()), check, "截断 BMP 像素数据被拒绝");
                        string prepare = Path.Combine(work, "prepared"); window.ExportTo(prepare);
                        check(File.Exists(Path.Combine(prepare, "splash.bmp")) && File.Exists(Path.Combine(prepare, "shim-signed", "COPYRIGHT")) &&
                            File.ReadAllText(Path.Combine(prepare, "config.txt")) == BootLogoPackage.Config, "实际 GUI 导出内置官方包、自定义图片、固定配置与 shim 版权");
                        File.Delete(input); check(File.Exists(Path.Combine(prepare, "splash.bmp")), "删除原始图片不影响准备包");
                        rescue.IsChecked = trial.IsChecked = shim.IsChecked = true;
                        probe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); check(!install.IsEnabled && probeCount == 1, "实际检查按钮收到未知加密时阻止安装");
                        check(hint!.Text.Contains("加密状态未知"), "GUI 安装按钮旁显示硬件检查的具体阻止原因");
                        probe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); check(install.IsEnabled && probeCount == 2, "实际检查与确认全部通过后安装按钮可点");
                        check(((Button)window.FindName("FirmwareDetailsButton")).IsEnabled, "收到启动项只读报告后详情按钮可点");
                        shim.IsChecked = false; check(!install.IsEnabled && hint.Text.Contains("第三项确认"), "Secure Boot 未确认 shim 步骤时提示缺少第三项"); shim.IsChecked = true;
                        rescue.IsChecked = false; check(!install.IsEnabled && hint.Text.Contains("第一项确认"), "撤销恢复介质确认后提示缺少第一项"); rescue.IsChecked = true;
                        trial.IsChecked = false; check(!install.IsEnabled && hint.Text.Contains("第二项确认"), "撤销试启动确认后提示缺少第二项"); trial.IsChecked = true;
                        rejectedProbe = true; probe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check(!install.IsEnabled && hint.Text.Contains("只读检查未通过"), "辅助报告返回失败时，即使硬件字段完整也不放行"); rejectedProbe = false;
                        staleProbe = true; probe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check(!install.IsEnabled && hint.Text.Contains("过期"), "检查过期时禁用安装并明确提示重新检查"); staleProbe = false;
                        probe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); check(install.IsEnabled, "重新通过检查后恢复可安装状态");
                        install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check(installCount == 1 && !install.IsEnabled && ((TextBlock)window.FindName("LogoStatus")).Text == "模拟安装完成" && hint.Text.Contains("已安装") && !hint.Text.Contains("暂不能安装"), "实际 GUI 安装成功后明确已安装，不再提示缺少只读检查；执行器全部为模拟");
                        probe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var restore = (Button)window.FindName("RestoreLogoButton");
                        check(!install.IsEnabled && restore.IsEnabled && hint.Text.Contains("无需重复安装"), "安装后重新检查显示已安装并保留恢复入口，不鼓励重复安装");
                        install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check(installCount == 1, "已安装状态即使触发安装事件也不会再次执行安装");
                        rejectedProbe = true; probe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check(!install.IsEnabled && hint.Text.Contains("只读检查未通过"), "失败报告即使包含 AlreadyInstalled 也不能被当成核验成功");
                        rejectedProbe = false; probe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (installedScreenshot != null)
                        {
                            ((ScrollViewer)window.FindName("LogoScroll")).ScrollToBottom();
                            window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                            var root = (FrameworkElement)window.Content;
                            root.Measure(new Size(1000, 740)); root.Arrange(new Rect(0, 0, 1000, 740)); root.UpdateLayout();
                            var capture = new RenderTargetBitmap(1000, 740, 96, 96, PixelFormats.Pbgra32);
                            var background = new DrawingVisual();
                            using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, 1000, 740));
                            capture.Render(background);
                            capture.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(installedScreenshot))!);
                            using var output = File.Create(installedScreenshot); encoder.Save(output);
                        }
                        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check(restoreCount == 1 && !restore.IsEnabled && !install.IsEnabled && hint.Text.Contains("只读检查"), "模拟恢复完成后清除已安装状态，要求重新检查");
                        probe.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check(install.IsEnabled, "恢复后重新检查通过才重新允许安装");
                        done.TrySetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { window.Close(); app.Shutdown(); }
                };
                window.Show(); Dispatcher.Run();
            }
            catch (Exception ex) { done.TrySetException(ex); app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    public static async Task RunOfficialCopy(string fixture, Action<bool, string> check, string work)
    {
        byte[] bmp = BootLogoImage.EncodeBgr24(33, 32, new byte[100 * 32], 100);
        string stage = BootLogoPackage.Extract(Path.Combine(work, "official-copy-source"), bmp);
        string fakeEsp = Path.Combine(work, "fake-esp", "EFI", "HackBGRT");
        string windows = Path.Combine(work, "fake-esp", "EFI", "Microsoft", "Boot", "bootmgfw.efi");
        Directory.CreateDirectory(Path.GetDirectoryName(windows)!); File.WriteAllBytes(windows, new byte[] { 70, 71, 72 });
        var start = new ProcessStartInfo(Path.GetFullPath(fixture)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(stage); start.ArgumentList.Add(fakeEsp);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        check(process.ExitCode == 0, "官方 2.6.0 实际 InstallFiles 方法仅复制到临时模拟 ESP：" + await errors);
        BootLogoInstaller.VerifyOfficialFiles(fakeEsp, stage, bmp);
        check(Directory.GetFiles(fakeEsp).Length == 6, "实际官方输出精确为预期六文件，shim / EFI / config / BMP 通过生产校验");
        check(File.ReadAllBytes(windows).SequenceEqual(new byte[] { 70, 71, 72 }), "实际官方文件复制方法保留模拟 Windows 启动器字节");
        var hashes = Directory.GetFiles(fakeEsp).ToDictionary(p => Path.GetFileName(p)!, BootLogoPackage.HashFile);
        BootLogoInstaller.VerifyFiles(fakeEsp, hashes);
        string backup = Path.Combine(work, "fake-windows.backup"); File.Copy(windows, backup);
        var journal = new BootLogoJournal { WindowsLoaderHash = BootLogoPackage.HashFile(windows), InstalledFiles = hashes };
        BootLogoInstaller.VerifyInstalledFiles(journal, windows, fakeEsp, backup);
        check(true, "安装后文件检查使用生产路径校验实际官方六文件及模拟 Windows / 备份哈希");
        File.WriteAllBytes(backup, new byte[] { 0 });
        Reject(() => BootLogoInstaller.VerifyInstalledFiles(journal, windows, fakeEsp, backup), check, "安装后检查拒绝损坏的 Windows 恢复备份");
        File.Copy(windows, backup, true); File.WriteAllBytes(windows, new byte[] { 0 });
        Reject(() => BootLogoInstaller.VerifyInstalledFiles(journal, windows, fakeEsp, backup), check, "安装后检查拒绝变化的 Windows 启动器文件");
        File.Copy(backup, windows, true);
        string loader = Path.Combine(fakeEsp, "loader.efi"); byte[] loaderBytes = File.ReadAllBytes(loader); File.Delete(loader);
        Reject(() => BootLogoInstaller.VerifyInstalledFiles(journal, windows, fakeEsp, backup), check, "安装后检查拒绝缺失的 Logo 文件");
        File.WriteAllBytes(loader, loaderBytes);
        File.WriteAllText(Path.Combine(fakeEsp, "foreign.txt"), "external");
        Reject(() => BootLogoInstaller.VerifyInstalledFiles(journal, windows, fakeEsp, backup), check, "安装后检查拒绝未知外来文件");
        Reject(() => BootLogoInstaller.VerifyFiles(fakeEsp, hashes), check, "已有 Logo 目录含外来文件时拒绝重装覆盖");
        File.Delete(Path.Combine(fakeEsp, "foreign.txt")); File.WriteAllBytes(Path.Combine(fakeEsp, "loader.efi"), new byte[] { 0 });
        Reject(() => BootLogoInstaller.VerifyOfficialFiles(fakeEsp, stage, bmp), check, "官方 EFI 文件损坏时生产校验阻止注册");
        Reject(() => BootLogoInstaller.VerifyFiles(fakeEsp, hashes), check, "已记录文件被修改时不覆盖");
    }
    public static async Task RunTransaction(Action<bool, string> check)
    {
        foreach (bool withDevices in new[] { false, true })
        foreach (string failure in new[] { "none", "initial-backup", "copy", "verify", "verified-backup", "entry-write", "order-write", "final-backup" })
        {
            var firmware = withDevices ? InsydeFirmware() : new FakeFirmware();
            var registration = new BootLogoRegistration(firmware, withDevices ? "INSYDE Corp." : ""); var plan = registration.Prepare(Partition);
            var originalOrder = firmware.Read("BootOrder");
            var journal = new BootLogoJournal { Plan = plan };
            int saved = 0, copyCalls = 0; bool rejected = false;
            if (failure == "entry-write") firmware.FailAt = 1;
            if (failure == "order-write") firmware.FailAt = 2;
            try
            {
                await BootLogoInstallTransaction.RunAsync(journal, registration, record =>
                {
                    saved++;
                    if ((failure == "initial-backup" && saved == 1) || (failure == "verified-backup" && saved == 2) || (failure == "final-backup" && saved == 3))
                        throw new IOException("injected backup failure");
                }, () =>
                {
                    check(saved >= 1 && firmware.Writes.Count == 0, "官方文件复制前必须已有原始恢复记录，且未写固件：" + failure);
                    copyCalls++;
                    if (failure == "copy" || failure == "verify") throw new IOException("injected " + failure + " failure");
                    return Task.FromResult(new Dictionary<string, string> { ["splash.bmp"] = "fake-verified" });
                }, () => { if (!firmware.Read(BootLogoRegistration.Name(plan.WindowsNumber))!.Data.SequenceEqual(WindowsEntry())) throw new Exception("Windows changed"); });
            }
            catch { rejected = true; }
            check(rejected == (failure != "none"), "真实生产事务成功 / 失败返回与阶段匹配：" + failure + " / Insyde=" + withDevices);
            if (failure == "none")
                check(journal.Phase == "Installed" && BootLogoRegistration.Order(firmware.Read("BootOrder"))[0] == plan.WindowsNumber, "完整事务成功且原 Windows 保持默认");
            else
                check(firmware.Read(BootLogoRegistration.Name(plan.Number)) == null && BootLogoRegistration.Equal(firmware.Read("BootOrder"), originalOrder), "生产事务异常撤销自己的启动项，原 Windows 默认不变：" + failure);
            if (withDevices) check(InsydeEntries.All(e => firmware.Read(BootLogoRegistration.Name(e.Number))!.Data.SequenceEqual(Convert.FromBase64String(e.Raw))),
                "生产事务所有成功 / 异常路径保留 Insyde 设备入口字节：" + failure);
            if (failure == "initial-backup") check(copyCalls == 0 && firmware.Writes.Count == 0, "原始备份无法落盘时不复制 EFI 文件，也不注册启动项");
            if (failure is "copy" or "verify" or "verified-backup") check(firmware.Writes.Count == 0, "文件复制 / 校验 / 二次备份失败时从未写入固件：" + failure);
        }
        var broken = new FakeFirmware { CorruptAt = 1 }; var brokenReg = new BootLogoRegistration(broken); var brokenJournal = new BootLogoJournal { Plan = brokenReg.Prepare(Partition) };
        bool failed = false;
        try { await BootLogoInstallTransaction.RunAsync(brokenJournal, brokenReg, _ => { }, () => Task.FromResult(new Dictionary<string, string>()), () => { }); }
        catch (IOException ex) { failed = ex.Message.Contains("自动撤销没有完成", StringComparison.Ordinal); }
        check(failed && brokenJournal.Phase == "Failed", "无法核实自身项且撤销失败时保留 Failed 记录，明确要求人工恢复");
        check(BootLogoRegistration.Order(broken.Read("BootOrder"))[0] == 5, "损坏新项尚未加入默认启动顺序，Windows 默认仍在");
    }
    public static async Task RunHelper(string bundle, Action<bool, string> check, string work)
    {
        string executable = Path.Combine(Path.GetFullPath(bundle), "WinBootVideo.exe");
        foreach (string action in BootLogoNativeFirmware.IsAdministrator ? new[] { "unsupported" } : new[] { "unsupported", "probe", "install" })
        {
            string directory = Path.Combine(work, "helper-" + action); Directory.CreateDirectory(directory);
            string input = Path.Combine(directory, "request.json"), output = Path.Combine(directory, "response.json");
            File.WriteAllText(input, System.Text.Json.JsonSerializer.Serialize(new BootLogoRequest { Action = action, RescueReady = true, ManualTrialAccepted = true, ShimUnderstood = true }, Settings.JsonOptions));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string value in new[] { "--boot-logo-helper", input, output }) start.ArgumentList.Add(value);
            using var process = Process.Start(start)!; await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var result = System.Text.Json.JsonSerializer.Deserialize<BootLogoResponse>(File.ReadAllText(output), Settings.JsonOptions)!;
            check(process.ExitCode == 30 && !result.Success, "实际管理员辅助 CLI 在未授权 / 不支持操作下返回明确失败：" + action);
            if (action == "probe") check(result.Environment is { Administrator: false, OsFullyDecrypted: null }, "普通权限实际只读报告把加密标为未知，不误报可安装");
            File.WriteAllText(output, "do not overwrite");
            using var again = Process.Start(start)!; await again.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            check(again.ExitCode == 30 && File.ReadAllText(output) == "do not overwrite", "辅助 CLI 不覆盖已有报告文件，也不重新执行操作：" + action);
        }
    }
}
