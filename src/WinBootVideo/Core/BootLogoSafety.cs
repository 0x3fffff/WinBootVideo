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
    public string Error { get; set; } = "";
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
    public BootLogoRegistration(IBootLogoFirmware firmware) => this.firmware = firmware;
    public static ushort[] Order(FirmwareValue? value)
    {
        if (value == null || value.Data.Length < 2 || value.Data.Length > 1024 || value.Data.Length % 2 != 0)
            throw new InvalidDataException("BootOrder 缺失或异常。");
        var result = Enumerable.Range(0, value.Data.Length / 2).Select(i => BitConverter.ToUInt16(value.Data, i * 2)).ToArray();
        if (result.Distinct().Count() != result.Length) throw new InvalidDataException("BootOrder 包含重复项。");
        return result;
    }
    public sealed record Plan(ushort Number, ushort WindowsNumber, FirmwareValue OriginalOrder, FirmwareValue WindowsEntry, FirmwareValue NewEntry);
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
        foreach (ushort id in order.Skip(1))
        {
            var value = firmware.Read(Name(id));
            if (value == null) throw new InvalidDataException("启动顺序存在缺失项。");
            // Unknown device paths / another OS are deliberately unsupported in the first release.
            throw new InvalidDataException("检测到额外固件启动项；初版仅支持单 Windows 启动项。请勿删除其他启动项来绕过检查。");
        }
        ushort? free = null;
        for (ushort id = 0; id < 256; id++)
        {
            var value = firmware.Read(Name(id));
            if (value == null) { if (!order.Contains(id) && free == null) free = id; continue; }
            if (id == order[0]) continue;
            // Unreferenced entries might belong to another installation. Fail closed.
            throw new InvalidDataException("检测到额外固件启动项，无法确认是否为多系统或旧 HackBGRT 安装。");
        }
        if (free == null) throw new InvalidDataException("没有可用的 EFI 启动项槽位。");
        var plan = new Plan(free.Value, order[0], original, windows, new FirmwareValue(7, logo));
        ValidatePlan(plan); return plan;
    }
    public void Apply(Plan plan)
    {
        ValidatePlan(plan);
        AssertWindows(plan);
        if (!Equal(firmware.Read("BootOrder"), plan.OriginalOrder) || firmware.Read(Name(plan.Number)) != null)
            throw new InvalidDataException("安装前固件状态已变化，请重新检查。");
        firmware.Write(Name(plan.Number), plan.NewEntry);
        if (!Equal(firmware.Read(Name(plan.Number)), plan.NewEntry)) throw new IOException("新启动项读回校验失败。");
        AssertWindows(plan);
        if (!Equal(firmware.Read("BootOrder"), plan.OriginalOrder)) throw new IOException("启动顺序被其他程序改变。");
        var appended = plan.OriginalOrder.Data.Concat(BitConverter.GetBytes(plan.Number)).ToArray();
        var target = new FirmwareValue(plan.OriginalOrder.Attributes, appended);
        firmware.Write("BootOrder", target);
        if (!Equal(firmware.Read("BootOrder"), target)) throw new IOException("启动顺序读回校验失败。");
        AssertWindows(plan);
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
    private static void ValidatePlan(Plan p)
    {
        var order = Order(p.OriginalOrder);
        var windows = BootLogoEntry.Parse(p.WindowsEntry.Data);
        if (p.Number == p.WindowsNumber || order[0] != p.WindowsNumber || order.Contains(p.Number) ||
            p.OriginalOrder.Attributes != 7 || p.WindowsEntry.Attributes != 7 || p.NewEntry.Attributes != 7 ||
            !p.NewEntry.Data.SequenceEqual(BootLogoEntry.Create(p.WindowsEntry.Data, windows.Partition)))
            throw new InvalidDataException("启动项计划与原始备份不一致。");
    }
    public static bool Equal(FirmwareValue? a, FirmwareValue? b) => a == null || b == null ? a == b : a.Attributes == b.Attributes && a.Data.SequenceEqual(b.Data);
    public static string Name(ushort id) => "Boot" + id.ToString("X4");
}
