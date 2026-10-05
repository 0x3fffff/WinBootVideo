using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinBootVideo.Core;
using WinBootVideo.Host;
using WinBootVideo.Shared;

int checks = 0;
// A red regression is reported through stderr / exit code, never a desktop dialog.
Native.SetErrorMode(0x0003);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
void Throws(Action action, string name) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, name); }
string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    if (args.Length == 2 && args[0] == "--multiscreen-only")
    {
        bool executed = await MultiscreenChecks.Run(args[1], Check);
        if (executed) Console.WriteLine($"{checks} actual multi-monitor checks passed. Private non-input desktop; no Shell or registry writes.");
        return;
    }
    if (args.Length == 1 && args[0] == "--desktop-launch-only")
    {
        DesktopLaunchChecks.Run(Check);
        Console.WriteLine($"{checks} desktop launch checks passed. Injected desktop and own-process probes only.");
        return;
    }
    if (args.Length == 3 && args[0] == "--cold-start-only")
    {
        await ColdStartChecks.Run(args[1], args[2], Check);
        Console.WriteLine($"{checks} cold start checks passed. Preview Host only; no real Shell launch or registry writes.");
        return;
    }
    if (args.Length == 2 && args[0] == "--visibility-gate-only")
    {
        await VisibilityGateChecks.Run(args[1], Check);
        Console.WriteLine($"{checks} visibility gate checks passed. No registry writes or desktop switches.");
        return;
    }
    if (args.Length == 2 && args[0] == "--hidden-desktop-only")
    {
        await HiddenDesktopChecks.Run(args[1], Check);
        Console.WriteLine($"{checks} hidden desktop checks passed. No input desktop switch or registry writes.");
        return;
    }
    if ((args.Length == 2 || args.Length == 3) && args[0] == "--gui-only")
    {
        await GuiFlowChecks.Run(args[1], Check, args.Length == 3 ? args[2] : null);
        Console.WriteLine($"{checks} GUI checks passed. No real registry writes.");
        return;
    }
    if ((args.Length == 2 || args.Length == 3) && args[0] == "--gui-fullscreen-only")
    {
        if (System.Windows.Forms.Screen.AllScreens.Length < 2)
        {
            Console.WriteLine("SKIP: full-screen GUI multi-monitor regression requires at least two real screens.");
            return;
        }
        await GuiFlowChecks.Run(args[1], Check, args.Length == 3 ? args[2] : null, fullscreen: true);
        Console.WriteLine($"{checks} actual full-screen GUI checks passed. No real registry writes.");
        return;
    }
    var selectedSettings = new Settings { VideoPath = @"D:\桌面\intro.mp4", VideoSha256 = new string('a', 64), VerifiedVideoSha256 = new string('b', 64), Volume = .71, FadeOutMilliseconds = 980, MaxDurationSeconds = 52, StartupTimeoutSeconds = 7, CoverOtherScreens = false, ScreenDevice = "DISPLAY2", FfmpegPath = @"E:\tools\ffmpeg.exe", FfprobePath = @"E:\tools\ffprobe.exe" };
    Check(JsonSerializer.Serialize(selectedSettings, Settings.JsonOptions) == JsonSerializer.Serialize(selectedSettings.Copy(), Settings.JsonOptions), "Settings.Copy 保留所选视频路径及全部非默认设置");
    var timeout = new SupervisorPolicy(8000, 45000);
    timeout.Tick(0, false, false, true, false);
    Check(!timeout.Tick(7999, false, false, true, false).StartDesktop, "未就绪时不提前启动桌面");
    var expired = timeout.Tick(8000, false, false, true, false);
    Check(expired.StartDesktop && !expired.KillPlayer && !expired.Finished, "初始化软期限放行桌面，冷启动的播放器仍保留独立预算");
    Check(!timeout.Tick(29999, false, false, true, false).KillPlayer, "可见后的独立三十秒冷启动期限到达前保留播放器");
    var coldExpired = timeout.Tick(30000, false, false, true, false);
    Check(coldExpired.KillPlayer && coldExpired.Finished && coldExpired.Reason == "cold-start-timeout", "冷启动期限到达仍未就绪时清理自己的播放器");
    var ready = new SupervisorPolicy(8000, 45000);
    Check(ready.Tick(400, true, false, true, false).StartDesktop, "遮罩就绪后才启动桌面");
    Check(!ready.Tick(500, true, true, true, false).StartDesktop, "仅请求一次启动");
    Check(!ready.Tick(45000, true, true, true, false).KillPlayer, "总等待预算从真正开始播放信号计时");
    Check(ready.Tick(45500, true, true, true, false).KillPlayer, "播放器开始后卡死仍受独立总上限约束");
    var early = new SupervisorPolicy(8000, 45000).Tick(200, false, false, true, true);
    Check(early.StartDesktop && early.Finished && !early.KillPlayer, "播放器提前失败也放行桌面");
    var hidden = new SupervisorPolicy(2000, 5000, 15000);
    Check(hidden.Tick(0, true, false, false, false).StartDesktop, "不可见桌面遮罩就绪后仍允许正常 Shell 完成初始化");
    Check(!hidden.Tick(9000, true, false, false, false).KillPlayer, "认证期间等待不消费初始化或播放预算");
    Check(!hidden.Tick(10000, true, false, true, false).KillPlayer, "输入桌面首次激活后开始等待播放握手");
    Check(!hidden.Tick(11999, true, false, true, false).KillPlayer, "可见后的播放握手保留完整初始化预算");
    var handshake = hidden.Tick(12000, true, false, true, false);
    Check(handshake.KillPlayer && handshake.Reason == "playback-handshake-timeout", "就绪后界面卡死按播放握手上限退出");
    var abandoned = new SupervisorPolicy(2000, 5000, 15000);
    abandoned.Tick(0, true, false, false, false);
    Check(!abandoned.Tick(14999, true, false, false, false).KillPlayer, "独立不可见等待期限到达前保留首次登录动画");
    Check(abandoned.Tick(15000, true, false, false, false).KillPlayer, "长期不激活桌面的播放器按独立等待上限清理");
    var interrupted = new SupervisorPolicy(2000, 5000);
    interrupted.Tick(0, true, true, true, false);
    var left = interrupted.Tick(100, true, true, false, false);
    Check(left.KillPlayer && left.Reason == "input-desktop-left", "播放后离开输入桌面立即清理，不在下一次解锁续播");
    ColdStartChecks.RunPolicy(Check);
    DesktopLaunchChecks.Run(Check);
    string sessionKey = LogonSessionIdentity.Read();
    Check(sessionKey == LogonSessionIdentity.Read(), "真实当前登录会话标识稳定，只读访问令牌与会话时间");
    Check(LogonSessionIdentity.Key(1, 0, 100) != LogonSessionIdentity.Key(1, 0, 200), "跨重启 LUID 复用时，不误跳过新的登录");

    foreach (string value in new[] { "", "C:\\has spaces\\视频.mp4", "C:\\trailing slash\\", "a\"b", "C:\\folder\\\"quoted\\\\", "plain" })
    {
        IntPtr pointer = Native.CommandLineToArgvW("program " + WindowsCommandLine.Quote(value), out int count);
        try { string? parsed = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, IntPtr.Size)); Check(count == 2 && parsed == value, "命令行往返：" + value); }
        finally { Native.LocalFree(pointer); }
    }
    var paths = new AppPaths(Path.Combine(work, "settings with spaces"));
    var registry = new FakeRegistry();
    var controller = new ShellController(paths, registry);
    registry.BeforeWrite = () => Check(File.Exists(paths.BackupFile), "必须先落盘备份，再写注册表");
    controller.Enable("test-user");
    Check(controller.IsEnabled, "启用写入带引号的宿主命令");
    string backup = File.ReadAllText(paths.BackupFile);
    controller.Enable("test-user");
    Check(File.ReadAllText(paths.BackupFile) == backup, "重复启用保留原始备份");
    Throws(() => controller.Disable("other-user"), "错误用户不能恢复");
    Check(controller.Disable("test-user") && !registry.Current.Exists, "还原原本不存在的 Shell 值");
    registry.Current = new ShellValue { Exists = true, Kind = (int)RegistryValueKind.ExpandString, Value = "%WINDIR%\\explorer.exe" };
    controller.Enable("test-user"); controller.Disable("test-user");
    Check(registry.Current.Kind == 2 && registry.Current.Value == "%WINDIR%\\explorer.exe", "恢复原值及 REG_EXPAND_SZ 类型");
    registry.Current = new ShellValue { Exists = true, Value = "another-shell.exe" };
    Throws(() => controller.Enable("test-user"), "拒绝替换其他自定义 Shell");
    Check(!controller.Disable("test-user") && registry.Current.Value == "another-shell.exe", "恢复不覆盖其他应用的新配置");
    registry.Current = new ShellValue(); registry.CustomPolicy = true;
    Throws(() => controller.Enable("test-user"), "已有自定义界面策略时拒绝启用");
    registry.CustomPolicy = false;
    registry.ThrowAfterNextWrite = true;
    Throws(() => controller.Enable("test-user"), "写入后异常触发回滚");
    Check(!registry.Current.Exists, "发生写入异常时恢复原值");

    Throws(() => new Settings { StartupTimeoutSeconds = 8, MaxDurationSeconds = 5 }.Validate(false), "初始化上限必须小于总上限");
    Throws(() => new Settings { Volume = double.NaN }.Validate(false), "拒绝非有限音量");
    Throws(() => Settings.ValidateLocalVideo(@"\\server\share\movie.mp4"), "拒绝 UNC 视频路径");
    string input = Path.Combine(work, "source movie.mp4"); File.WriteAllBytes(input, new byte[] { 1, 2, 3 });
    var store = new SettingsStore(paths);
    var imported = await store.ImportAndSaveAsync(new Settings { VideoPath = input, VerifiedVideoSha256 = "wrong" }, CancellationToken.None);
    Check(imported.VideoPath != input && File.Exists(imported.VideoPath) && imported.VideoSha256.Length == 64 && imported.VerifiedVideoSha256 == "", "保存复制到固定目录，并清除旧视频验证");
    File.Delete(input); Check(File.Exists(store.Load().VideoPath), "移走原视频后配置仍有效");

    string probe = """{"streams":[{"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p","width":1920,"height":1080,"avg_frame_rate":"30000/1001","color_transfer":"bt709"}],"format":{"duration":"5.5"}}""";
    var info = MediaTools.ParseProbe(probe);
    Check(info.Recommended && Math.Abs(info.Fps - 29.97) < .01 && info.Duration == 5.5, "解析兼容视频及有理帧率");
    Check(MediaTools.ParseProbe(probe.Replace("bt709", "smpte2084")).Hdr, "识别 PQ HDR 素材");
    Throws(() => MediaTools.ParseProbe("""{"streams":[{"codec_type":"audio","codec_name":"aac"}]}"""), "拒绝纯音频输入");
    var conversion = MediaTools.ConversionArguments("C:\\video & quote\".mp4", "C:\\new file.mp4", 15, true, true);
    Check(conversion.Contains("C:\\video & quote\".mp4") && conversion.Contains("-an") && conversion.Contains("-n") && conversion.Any(x => x.Contains("tonemap=hable") && x.Contains("format=yuv420p")), "转码路径作为单独参数、拒绝覆盖、HDR 转 SDR");
    if (args.Length == 3 || args.Length == 4)
    {
        await IntegrationChecks.Run(args[0], args[1], args[2], Check);
        await MultiscreenChecks.Run(args[0], Check);
        await HiddenDesktopChecks.Run(args[0], Check);
        await GuiFlowChecks.Run(args[0], Check);
        if (args.Length == 4) await ColdStartChecks.Run(args[0], args[3], Check);
    }
    Console.WriteLine($"{checks} checks passed. No real registry writes.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
finally
{
    try { Directory.Delete(work, true); }
    catch (Exception ex) { Console.Error.WriteLine("Test cleanup failed: " + ex); Environment.ExitCode = 1; }
}

sealed class FakeRegistry : IUserShellRegistry
{
    public ShellValue Current = new();
    public bool CustomPolicy;
    public Action? BeforeWrite;
    public bool ThrowAfterNextWrite;
    public ShellValue Read() => JsonSerializer.Deserialize<ShellValue>(JsonSerializer.Serialize(Current))!;
    public void Write(ShellValue value) { BeforeWrite?.Invoke(); Current = JsonSerializer.Deserialize<ShellValue>(JsonSerializer.Serialize(value))!; if (ThrowAfterNextWrite) { ThrowAfterNextWrite = false; throw new IOException("simulated post-write failure"); } }
    public bool HasCustomUserPolicy() => CustomPolicy;
}
static class Native
{
    [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr memory);
}
