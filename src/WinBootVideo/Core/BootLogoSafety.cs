using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WinBootVideo.Core;

public sealed class BootLogoEnvironment
{
    public bool Administrator { get; set; }
    public bool Uefi { get; set; }
    public bool X64 { get; set; }
    public bool? SecureBoot { get; set; }
    public bool? OsFullyDecrypted { get; set; }
    public int EspCount { get; set; }
    public bool EspOnOsDisk { get; set; }
    public string EspVolume { get; set; } = "";
    public string EspPartitionGuid { get; set; } = "";
    public long? EspFreeBytes { get; set; }
    public string Model { get; set; } = "";
    public string FirmwareManufacturer { get; set; } = "";
    public string Error { get; set; } = "";
    public string FirmwareDetails { get; set; } = "";
    public DateTime CheckedUtc { get; set; }
    public List<string> Blockers()
    {
        var reasons = new List<string>();
        if (!Administrator) reasons.Add("需要管理员只读检查；普通权限下的未知状态不能放行。");
        if (!Uefi || !X64) reasons.Add("初版仅支持已经使用 UEFI 启动的 Windows x64。不要切换 CSM / Legacy。");
        if (SecureBoot == null) reasons.Add("安全启动状态未知。");
        if (OsFullyDecrypted != true) reasons.Add("系统盘已加密或加密状态未知。初版阻止安装；不要仅暂停保护后绕过检查。");
        if (EspCount != 1 || !EspOnOsDisk) reasons.Add("必须只有一个 EFI 系统分区，且与当前 Windows 位于同一磁盘。");
        if (EspFreeBytes == null || EspFreeBytes < 20L * 1024 * 1024) reasons.Add("EFI 分区可用空间未知或不足 20 MB，停止安装；请勿删除不明 EFI 文件腾空间。");
        if (!Guid.TryParse(EspPartitionGuid, out _) || !BootLogoPaths.IsVolumePath(EspVolume)) reasons.Add("无法准确定位 EFI 系统分区。");
        if (Error.Length != 0) reasons.Add(Error);
        return reasons;
    }
}

public static class BootLogoPaths
{
    public static bool IsVolumePath(string path) => path.StartsWith(@"\\?\Volume{", StringComparison.Ordinal) &&
        path.EndsWith(@"}\", StringComparison.Ordinal) && Guid.TryParse(path.Substring(11, path.Length - 13), out _);
    public static void OrdinaryDirectory(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current != null)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("目录经过链接或重解析点，拒绝操作：" + current);
            current = Path.GetDirectoryName(current);
        }
    }
}

/// <summary>Strict bounded EFI_LOAD_OPTION parsing. Never edits the Windows entry.</summary>
public static class BootLogoEntry
{
    public const string WindowsPath = @"\EFI\Microsoft\Boot\bootmgfw.efi";
    public const string LogoPath = @"\EFI\HackBGRT\loader.efi";
    public sealed record Parsed(string Label, string Path, Guid Partition, uint Attributes, byte[][] Nodes);
    // Diagnostic description only. Labels and partial paths never authorize firmware changes.
    public static string Describe(byte[] raw)
    {
        try
        {
            var entry = Parse(raw);
            string kind = entry.Path.Equals(WindowsPath, StringComparison.OrdinalIgnoreCase) ? "Windows 启动器" :
                entry.Path.Equals(LogoPath, StringComparison.OrdinalIgnoreCase) ? "HackBGRT 启动器" : "其他 EFI 文件";
            return Clean(entry.Label) + " · " + kind + "\n  路径：" + Clean(entry.Path) + "\n  分区：" + entry.Partition;
        }
        catch (InvalidDataException)
        {
            // Device-only / vendor paths may be perfectly normal. Show their bounded metadata,
            // but keep the unsupported-configuration guard; never classify by the label alone.
            string label = "名称无法解析";
            if (raw.Length >= 8 && raw.Length <= 65536)
            {
                for (int p = 6; p + 1 < raw.Length && p < 1032; p += 2)
                    if (raw[p] == 0 && raw[p + 1] == 0) { label = Clean(Encoding.Unicode.GetString(raw, 6, p - 6)); break; }
            }
            return label + " · 设备 / 厂商路径或不支持的数据格式（不能仅凭名称认定安全）";
        }
    }
    private static string Clean(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    public static Parsed Parse(byte[] raw)
    {
        if (raw.Length < 12 || raw.Length > 65536) throw new InvalidDataException("EFI 启动项长度无效。");
        int p = 6, labelEnd = -1;
        for (; p + 1 < raw.Length && p < 1032; p += 2) if (raw[p] == 0 && raw[p + 1] == 0) { labelEnd = p; break; }
        if (labelEnd < 0) throw new InvalidDataException("EFI 启动项名称没有结束符。");
        string label = Encoding.Unicode.GetString(raw, 6, labelEnd - 6);
        p = labelEnd + 2;
        int end = checked(p + BitConverter.ToUInt16(raw, 4));
        if (end > raw.Length) throw new InvalidDataException("EFI 设备路径被截断。");
        var nodes = new List<byte[]>(); string file = ""; Guid partition = Guid.Empty; bool terminated = false;
        while (p < end)
        {
            if (p + 4 > end) throw new InvalidDataException("EFI 节点头被截断。");
            int len = BitConverter.ToUInt16(raw, p + 2);
            if (len < 4 || p + len > end) throw new InvalidDataException("EFI 节点长度无效。");
            byte[] node = raw.AsSpan(p, len).ToArray(); nodes.Add(node);
            if (node[0] == 4 && node[1] == 1)
            {
                if (len != 42 || node[40] != 2 || node[41] != 2 || partition != Guid.Empty)
                    throw new InvalidDataException("仅支持一个 GPT 磁盘分区节点。");
                partition = new Guid(node.AsSpan(24, 16));
            }
            if (node[0] == 4 && node[1] == 4)
            {
                if (file.Length != 0 || len < 8 || len % 2 != 0 || node[^1] != 0 || node[^2] != 0)
                    throw new InvalidDataException("EFI 文件路径不明确。");
                file = Encoding.Unicode.GetString(node, 4, len - 6);
                if (file.IndexOf('\0') >= 0) throw new InvalidDataException("EFI 文件路径含提前结束符。");
            }
            if (node[0] == 0x7f)
            {
                if (node[1] != 0xff || len != 4 || p + len != end) throw new InvalidDataException("不支持多实例 EFI 设备路径。");
                terminated = true;
            }
            p += len;
        }
        if (!terminated || file.Length == 0 || partition == Guid.Empty) throw new InvalidDataException("EFI 启动路径缺少磁盘、文件或结束节点。");
        return new Parsed(label, file, partition, BitConverter.ToUInt32(raw, 0), nodes.ToArray());
    }

    // Equivalent to HackBGRT's MakeOwnEntry: clone Windows' device path, replace filename,
    // discard Microsoft load options for shim. Registration order is managed separately.
    public static byte[] Create(byte[] windowsEntry, Guid expectedPartition)
    {
        var entry = Parse(windowsEntry);
        if (!entry.Path.Equals(WindowsPath, StringComparison.OrdinalIgnoreCase) || entry.Partition != expectedPartition || (entry.Attributes & 1) == 0)
            throw new InvalidDataException("当前 Windows 启动项与系统分区不一致。");
        byte[] file = Encoding.Unicode.GetBytes(LogoPath + "\0");
        var nodes = entry.Nodes.Select(n => n[0] == 4 && n[1] == 4 ?
            new byte[] { 4, 4, (byte)((file.Length + 4) & 255), (byte)((file.Length + 4) >> 8) }.Concat(file).ToArray() : n).ToArray();
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(1u); writer.Write(checked((ushort)nodes.Sum(n => n.Length)));
        writer.Write(Encoding.Unicode.GetBytes("HackBGRT\0"));
        foreach (var node in nodes) writer.Write(node);
        return output.ToArray();
    }
}

public sealed record FirmwareValue(uint Attributes, byte[] Data);
public interface IBootLogoFirmware
{
    FirmwareValue? Read(string name);
    void Write(string name, FirmwareValue? value);
}

/// <summary>Small transaction: new Boot#### then append last. Windows remains first.</summary>
public sealed class BootLogoRegistration
{
    private readonly IBootLogoFirmware firmware;
    private readonly string firmwareManufacturer;
    public BootLogoRegistration(IBootLogoFirmware firmware, string firmwareManufacturer = "")
    { this.firmware = firmware; this.firmwareManufacturer = firmwareManufacturer; }
    public static ushort[] Order(FirmwareValue? value)
    {
        if (value == null || value.Data.Length < 2 || value.Data.Length > 1024 || value.Data.Length % 2 != 0)
            throw new InvalidDataException("BootOrder 缺失或异常。");
        var result = Enumerable.Range(0, value.Data.Length / 2).Select(i => BitConverter.ToUInt16(value.Data, i * 2)).ToArray();
        if (result.Distinct().Count() != result.Length) throw new InvalidDataException("BootOrder 包含重复项。");
        return result;
    }
    public sealed record Plan(ushort Number, ushort WindowsNumber, FirmwareValue OriginalOrder, FirmwareValue WindowsEntry, FirmwareValue NewEntry,
        Dictionary<ushort, FirmwareValue>? PreservedEntries = null);
    // Insyde's generic removable/network choices have an End-only device path and RC options.
    // Exact bytes + fixed IDs + verified manufacturer: a matching label alone is insufficient.
    internal static string? InsydeDevice(ushort number, FirmwareValue value)
    {
        string? label = number switch { 0x2001 => "EFI USB Device", 0x2002 => "EFI DVD/CDROM", 0x2003 => "EFI Network", _ => null };
        if (label == null || value.Attributes != 7) return null;
        byte[] expected = new byte[] { 1, 0, 0, 0, 4, 0 }.Concat(Encoding.Unicode.GetBytes(label + "\0"))
            .Concat(new byte[] { 0x7f, 0xff, 4, 0, 0x52, 0x43 }).ToArray();
        return value.Data.SequenceEqual(expected) ? label : null;
    }
    private bool IsInsyde => firmwareManufacturer.Trim().Equals("INSYDE Corp.", StringComparison.OrdinalIgnoreCase);
    private bool PreserveDevice(ushort id, FirmwareValue value, Dictionary<ushort, FirmwareValue> preserved)
    {
        if (!IsInsyde || InsydeDevice(id, value) == null) return false;
        preserved[id] = new FirmwareValue(value.Attributes, value.Data.ToArray()); return true;
    }
    public string Inspect()
    {
        var order = Order(firmware.Read("BootOrder"));
        var current = firmware.Read("BootCurrent");
        ushort? active = current?.Data.Length == 2 ? BitConverter.ToUInt16(current.Data) : null;
        var numbers = order.Concat(Enumerable.Range(0, 256).Select(i => (ushort)i)).Concat(new ushort[] { 0x2001, 0x2002, 0x2003 });
        if (active != null) numbers = numbers.Append(active.Value);
        var lines = new List<string> { "只读固件启动项报告", "BootOrder：" + string.Join(" → ", order.Select(Name)),
            "BootCurrent：" + (active == null ? "未知" : Name(active.Value)), "固件厂商：" + (firmwareManufacturer.Length == 0 ? "未知" : firmwareManufacturer),
            "读取范围：BootOrder、本次 BootCurrent、Boot0000–Boot00FF 和 Boot2001–Boot2003。",
            "名称仅作说明，不作为安装或删除依据。" };
        foreach (ushort id in numbers.Distinct())
        {
            var value = firmware.Read(Name(id));
            if (value == null) { if (order.Contains(id)) lines.Add("\n" + Name(id) + "：启动顺序中引用的项缺失"); continue; }
            lines.Add("\n" + Name(id) + (order.Contains(id) ? "（在启动顺序中）" : "（未列入启动顺序）") +
                (active == id ? " · 本次启动" : "") + "\n  " +
                (IsInsyde && InsydeDevice(id, value) is string device ? device + " · 已核验 Insyde 通用设备入口（完整字节匹配；将原样保留）" : BootLogoEntry.Describe(value.Data)) +
                "\n  变量属性：0x" + value.Attributes.ToString("X") +
                "\n  数据 SHA-256：" + BootLogoPackage.Hash(value.Data));
        }
        lines.Add("\n支持当前默认的单 Windows 启动项及已核验的 Insyde 通用设备入口；其他系统、旧 Logo 或未知路径仍停止。请勿删除启动项绕过检查。");
        return string.Join("\n", lines);
    }
    public Plan Prepare(Guid partition)
    {
        if (firmware.Read("BootNext") != null) throw new InvalidDataException("已有单次启动设置，请先完成它再检查。");
        var original = firmware.Read("BootOrder")!;
        var order = Order(original);
        var current = firmware.Read("BootCurrent");
        if (current == null || current.Data.Length != 2 || BitConverter.ToUInt16(current.Data) != order[0])
            throw new InvalidDataException("当前启动项不是默认的 Windows 启动项，拒绝自动安装。");
        var windows = firmware.Read(Name(order[0])) ?? throw new InvalidDataException("默认启动项缺失。");
        byte[] logo = BootLogoEntry.Create(windows.Data, partition);
        var preserved = new Dictionary<ushort, FirmwareValue>();
        foreach (ushort id in order.Skip(1))
        {
            var value = firmware.Read(Name(id));
            if (value == null) throw new InvalidDataException("启动顺序存在缺失项。");
            if (PreserveDevice(id, value, preserved)) continue;
            // Unknown device paths / another OS are deliberately unsupported in the first release.
            throw new InvalidDataException("当前版本尚不支持额外固件启动项 " + Name(id) + "：" + BootLogoEntry.Describe(value.Data) +
                "\n请查看启动项详情。额外项不一定代表双系统，请勿删除它来绕过检查。");
        }
        // These firmware-managed slots may exist without appearing in BootOrder.
        foreach (ushort id in new ushort[] { 0x2001, 0x2002, 0x2003 })
        {
            if (order.Contains(id)) continue;
            var value = firmware.Read(Name(id));
            if (value != null && !PreserveDevice(id, value, preserved))
                throw new InvalidDataException("未引用的固件设备项 " + Name(id) + " 不符合已验证的 Insyde 格式，请查看启动项详情；不要删除它绕过检查。");
        }
        ushort? free = null;
        for (ushort id = 0; id < 256; id++)
        {
            var value = firmware.Read(Name(id));
            if (value == null) { if (!order.Contains(id) && free == null) free = id; continue; }
            if (id == order[0]) continue;
            // Unreferenced entries might belong to another installation. Fail closed.
            throw new InvalidDataException("当前版本尚不支持未引用的固件启动项 " + Name(id) + "：" + BootLogoEntry.Describe(value.Data) +
                "\n无法确认是否为厂商功能、其他系统或旧 HackBGRT；请查看详情，不要删除它来绕过检查。");
        }
        if (free == null) throw new InvalidDataException("没有可用的 EFI 启动项槽位。");
        var plan = new Plan(free.Value, order[0], original, windows, new FirmwareValue(7, logo), preserved);
        ValidatePlan(plan); return plan;
    }
    // Read-only verification of our recorded installation. Never broadens first-install rules.
    public string VerifyInstalled(Plan plan, Guid partition)
    {
        ValidatePlan(plan);
        if (plan.Number > 255 || BootLogoEntry.Parse(plan.WindowsEntry.Data).Partition != partition)
            throw new InvalidDataException("Logo 恢复记录与当前 EFI 分区不一致。");
        AssertWindows(plan);
        AssertPreserved(plan);
        if (!Equal(firmware.Read(Name(plan.Number)), plan.NewEntry))
            throw new InvalidDataException("本软件的 Logo 启动项缺失或已变化；请查看恢复记录，不要重复安装。");
        var rawOrder = firmware.Read("BootOrder");
        var order = Order(rawOrder);
        var preserved = plan.PreservedEntries ?? new Dictionary<ushort, FirmwareValue>();
        if (rawOrder!.Attributes != 7 || !order.Contains(plan.Number) || !order.Contains(plan.WindowsNumber) ||
            order.Any(id => id != plan.Number && id != plan.WindowsNumber && !preserved.ContainsKey(id)) ||
            (order[0] != plan.Number && order[0] != plan.WindowsNumber))
            throw new InvalidDataException("当前启动顺序与已记录的 Logo / Windows 项不一致，停止安装；请查看启动项详情。");
        var current = firmware.Read("BootCurrent");
        if (current?.Data.Length != 2 || (BitConverter.ToUInt16(current.Data) != plan.WindowsNumber && BitConverter.ToUInt16(current.Data) != plan.Number))
            throw new InvalidDataException("本次启动不是已记录的 Windows / Logo 项，不能确认安装状态。");
        var next = firmware.Read("BootNext");
        if (next != null && (next.Data.Length != 2 || (BitConverter.ToUInt16(next.Data) != plan.Number && BitConverter.ToUInt16(next.Data) != plan.WindowsNumber)))
            throw new InvalidDataException("存在其他单次启动设置，请先完成它再检查。");
        foreach (ushort id in Enumerable.Range(0, 256).Select(i => (ushort)i).Concat(new ushort[] { 0x2001, 0x2002, 0x2003 }))
        {
            if (id == plan.Number || id == plan.WindowsNumber) continue;
            var value = firmware.Read(Name(id));
            if (preserved.TryGetValue(id, out var expected) && Equal(value, expected)) continue;
            if (value != null) throw new InvalidDataException("检测到恢复记录之外的固件项 " + Name(id) + "，停止安装；不要删除它绕过检查。");
        }
        return "已安装并核验本软件的 " + Name(plan.Number) + "（启动项完整字节及文件校验通过）。" +
            (order[0] == plan.Number ? "HackBGRT 当前为默认启动项；无需重复安装。" :
            "Windows Boot Manager 仍是默认；请从固件启动菜单单次选择 HackBGRT 试启动，验证成功后再手动设为默认。") +
            "如需更换图片，请先恢复原 Logo 后重新安装。";
    }
    public void Apply(Plan plan)
    {
        ValidatePlan(plan);
        AssertWindows(plan);
        AssertPreserved(plan);
        if (!Equal(firmware.Read("BootOrder"), plan.OriginalOrder) || firmware.Read(Name(plan.Number)) != null)
            throw new InvalidDataException("安装前固件状态已变化，请重新检查。");
        firmware.Write(Name(plan.Number), plan.NewEntry);
        if (!Equal(firmware.Read(Name(plan.Number)), plan.NewEntry)) throw new IOException("新启动项读回校验失败。");
        AssertWindows(plan);
        AssertPreserved(plan);
        if (!Equal(firmware.Read("BootOrder"), plan.OriginalOrder)) throw new IOException("启动顺序被其他程序改变。");
        var appended = plan.OriginalOrder.Data.Concat(BitConverter.GetBytes(plan.Number)).ToArray();
        var target = new FirmwareValue(plan.OriginalOrder.Attributes, appended);
        firmware.Write("BootOrder", target);
        if (!Equal(firmware.Read("BootOrder"), target)) throw new IOException("启动顺序读回校验失败。");
        AssertWindows(plan);
        AssertPreserved(plan);
    }
    public void Restore(Plan plan)
    {
        ValidatePlan(plan);
        AssertWindows(plan);
        var next = firmware.Read("BootNext");
        if (next != null && (next.Data.Length != 2 || BitConverter.ToUInt16(next.Data) == plan.Number))
            throw new InvalidDataException("固件仍有指向 Logo 的单次启动设置，请先在固件菜单取消它再恢复。");
        var owned = firmware.Read(Name(plan.Number));
        if (owned != null && !Equal(owned, plan.NewEntry)) throw new InvalidDataException("原槽位现在属于其他启动项，拒绝删除。");
        var current = firmware.Read("BootOrder")!;
        var ids = Order(current);
        if (!ids.Contains(plan.WindowsNumber)) throw new InvalidDataException("Windows 启动项已不在启动顺序中，请用固件菜单选择 Windows Boot Manager。");
        var remaining = ids.Where(id => id != plan.Number).ToArray();
        if (remaining.Length != ids.Length)
        {
            var target = new FirmwareValue(current.Attributes, remaining.SelectMany(BitConverter.GetBytes).ToArray());
            firmware.Write("BootOrder", target);
            if (!Equal(firmware.Read("BootOrder"), target)) throw new IOException("移除启动顺序失败；Logo 文件仍保留。");
        }
        if (owned != null)
        {
            if (!Equal(firmware.Read(Name(plan.Number)), plan.NewEntry)) throw new IOException("恢复期间启动项发生变化。");
            firmware.Write(Name(plan.Number), null);
            if (firmware.Read(Name(plan.Number)) != null) throw new IOException("删除自己的启动项失败；Logo 文件仍保留。");
        }
        AssertWindows(plan);
    }
    private void AssertWindows(Plan p) { if (!Equal(firmware.Read(Name(p.WindowsNumber)), p.WindowsEntry)) throw new InvalidDataException("Windows 启动项已变化，停止修改。"); }
    private void AssertPreserved(Plan p)
    {
        if (p.PreservedEntries is not { Count: > 0 }) return;
        if (!IsInsyde) throw new InvalidDataException("无法重新确认固件厂商，停止修改。");
        foreach (var pair in p.PreservedEntries)
            if (!Equal(firmware.Read(Name(pair.Key)), pair.Value)) throw new InvalidDataException("已核验设备入口 " + Name(pair.Key) + " 已变化，请重新检查。");
    }
    private static void ValidatePlan(Plan p)
    {
        var order = Order(p.OriginalOrder);
        var windows = BootLogoEntry.Parse(p.WindowsEntry.Data);
        if (p.Number == p.WindowsNumber || order[0] != p.WindowsNumber || order.Contains(p.Number) ||
            p.OriginalOrder.Attributes != 7 || p.WindowsEntry.Attributes != 7 || p.NewEntry.Attributes != 7 ||
            !p.NewEntry.Data.SequenceEqual(BootLogoEntry.Create(p.WindowsEntry.Data, windows.Partition)))
            throw new InvalidDataException("启动项计划与原始备份不一致。");
        var preserved = p.PreservedEntries ?? new Dictionary<ushort, FirmwareValue>();
        if (preserved.Any(pair => pair.Key == p.Number || pair.Key == p.WindowsNumber || InsydeDevice(pair.Key, pair.Value) == null) ||
            order.Skip(1).Any(id => !preserved.ContainsKey(id)))
            throw new InvalidDataException("设备入口备份与已核验结构不一致。");
    }
    public static bool Equal(FirmwareValue? a, FirmwareValue? b) => a == null || b == null ? a == b : a.Attributes == b.Attributes && a.Data.SequenceEqual(b.Data);
    public static string Name(ushort id) => "Boot" + id.ToString("X4");
}
