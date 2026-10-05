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
    public static Task RunGui(Action<bool, string> check, string work)
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
                int probeCount = 0, installCount = 0; bool rejectedProbe = false, staleProbe = false;
                var window = new BootLogoWindow(new AppPaths(Path.Combine(work, "data")), request =>
                {
                    if (request.Action == "install") { installCount++; return Task.FromResult(new BootLogoResponse { Success = true, Message = "模拟安装完成" }); }
                    probeCount++;
                    return Task.FromResult(new BootLogoResponse { Success = probeCount > 1 && !rejectedProbe, Message = probeCount > 1 ? "模拟检查通过" : "模拟未知加密",
                        Environment = new BootLogoEnvironment { Administrator = true, Uefi = true, X64 = true, SecureBoot = true,
                            OsFullyDecrypted = probeCount > 1 ? true : null, EspCount = 1, EspOnOsDisk = true, EspVolume = Volume,
                            EspPartitionGuid = Partition.ToString(), EspFreeBytes = 64L * 1024 * 1024,
                            FirmwareDetails = "模拟 Boot0007 设备项 · 无真实固件调用", CheckedUtc = DateTime.UtcNow.AddMinutes(staleProbe ? -11 : 0) } });
                }, _ => true) { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
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
                        check(installCount == 1 && !install.IsEnabled && ((TextBlock)window.FindName("LogoStatus")).Text == "模拟安装完成", "实际 GUI 点击走确认与执行流程，完成后要求重新检查；执行器全部为模拟");
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
        File.WriteAllText(Path.Combine(fakeEsp, "foreign.txt"), "external");
        Reject(() => BootLogoInstaller.VerifyFiles(fakeEsp, hashes), check, "已有 Logo 目录含外来文件时拒绝重装覆盖");
        File.Delete(Path.Combine(fakeEsp, "foreign.txt")); File.WriteAllBytes(Path.Combine(fakeEsp, "loader.efi"), new byte[] { 0 });
        Reject(() => BootLogoInstaller.VerifyOfficialFiles(fakeEsp, stage, bmp), check, "官方 EFI 文件损坏时生产校验阻止注册");
        Reject(() => BootLogoInstaller.VerifyFiles(fakeEsp, hashes), check, "已记录文件被修改时不覆盖");
    }
    public static async Task RunTransaction(Action<bool, string> check)
    {
        foreach (string failure in new[] { "none", "initial-backup", "copy", "verify", "verified-backup", "entry-write", "order-write", "final-backup" })
        {
            var firmware = new FakeFirmware(); var registration = new BootLogoRegistration(firmware); var plan = registration.Prepare(Partition);
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
                }, () => { if (!firmware.Read("Boot0005")!.Data.SequenceEqual(WindowsEntry())) throw new Exception("Windows changed"); });
            }
            catch { rejected = true; }
            check(rejected == (failure != "none"), "真实生产事务成功 / 失败返回与阶段匹配：" + failure);
            if (failure == "none")
                check(journal.Phase == "Installed" && BootLogoRegistration.Order(firmware.Read("BootOrder"))[0] == 5, "完整事务成功且原 Windows 保持默认");
            else
                check(firmware.Read(BootLogoRegistration.Name(plan.Number)) == null && BootLogoRegistration.Order(firmware.Read("BootOrder")).SequenceEqual(new ushort[] { 5 }), "生产事务异常撤销自己的启动项，原 Windows 默认不变：" + failure);
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
