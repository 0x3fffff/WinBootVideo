using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WinBootVideo.Core;
using WinBootVideo.Host;
using WinBootVideo.Shared;

internal static class ColdStartChecks
{
    // Runs the real lightweight Host in preview mode on an isolated desktop.
    // Unlike HiddenDesktopChecks (Player only), this covers the parent timeout
    // before the child has signalled Ready. No input desktop switch or Shell run.
    public static async Task Run(string bundle, string fixture, Action<bool, string> check)
    {
        bundle = Path.GetFullPath(bundle);
        fixture = Path.GetFullPath(fixture);
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-cold-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        IntPtr originalDesktop = GetThreadDesktop(GetCurrentThreadId());
        IntPtr privateDesktop = IntPtr.Zero;
        IntPtr parentHandle = IntPtr.Zero;
        IntPtr parentThread = IntPtr.Zero;
        Process? parent = null;
        Process? child = null;
        try
        {
            string desktopName = "WinBootVideoHostTest_" + Guid.NewGuid().ToString("N");
            privateDesktop = CreateDesktopW(desktopName, null, IntPtr.Zero, 0, 0x00c7, IntPtr.Zero);
            if (privateDesktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            check(!IsInputDesktop(privateDesktop), "Host 回归使用私有非输入桌面，不改变真实登录或输入桌面");
            string dummyBundle = Path.Combine(work, "bundle"); Directory.CreateDirectory(dummyBundle);
            string host = Path.Combine(dummyBundle, "WinBootVideo.Host.exe");
            File.Copy(Path.Combine(bundle, "WinBootVideo.Host.exe"), host);
            File.Copy(Path.Combine(bundle, "WinBootVideo.Host.exe.config"), host + ".config");
            File.Copy(fixture, Path.Combine(dummyBundle, "WinBootVideo.exe"));
            var paths = new AppPaths(Path.Combine(work, "data"));
            string video = Path.Combine(bundle, "demo-login.mp4");
            new SettingsStore(paths).Write(new Settings
            {
                VideoPath = video,
                VideoSha256 = await SettingsStore.HashAsync(video, CancellationToken.None),
                Volume = 0,
                StartupTimeoutSeconds = 2,
                MaxDurationSeconds = 5,
                CoverOtherScreens = false
            });
            var command = new StringBuilder(WindowsCommandLine.Quote(host) + " --preview --data-dir " + WindowsCommandLine.Quote(paths.DataDirectory));
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = ObjectName(GetProcessWindowStation()) + "\\" + desktopName
            };
            if (!CreateProcessW(host, command, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, dummyBundle, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            parentHandle = process.Process; parentThread = process.Thread;
            parent = Process.GetProcessById((int)process.ProcessId);
            var elapsed = Stopwatch.StartNew();
            string pidFile = Path.Combine(paths.DataDirectory, "deferred-player-pid.txt");
            while (!File.Exists(pidFile) && !parent.HasExited && elapsed.Elapsed.TotalSeconds < 5) await Task.Delay(100);
            check(File.Exists(pidFile), "真实 Host 启动了延迟 Ready 的独立播放器进程");
            try { child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))); } catch (ArgumentException) { }
            while (elapsed.Elapsed.TotalSeconds < 8) await Task.Delay(100);
            string log = Path.Combine(paths.LogDirectory, "host.log");
            string evidence = File.Exists(log) ? File.ReadAllText(log) : "host log missing";
            Console.WriteLine("Cold Host evidence: elapsed=" + elapsed.Elapsed.TotalSeconds.ToString("F2") + "s parent-exited=" + parent.HasExited + " child-exited=" + (child == null || child.HasExited) + Environment.NewLine + evidence.Trim());
            check(!IsInputDesktop(privateDesktop) && GetThreadDesktop(GetCurrentThreadId()) == originalDesktop,
                "测试八秒内始终使用非输入桌面，调用线程和用户输入桌面未切换");
            check(!parent.HasExited && child != null && !child.HasExited && !evidence.Contains("startup-timeout"),
                "不可见且尚未 Ready 时，真实 Host 超过初始化与播放上限仍保留自己的播放器");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await parent.WaitForExitAsync(deadline.Token);
            check(parent.ExitCode == 0 && File.Exists(Path.Combine(paths.DataDirectory, "deferred-player-ready.txt")),
                "延迟十秒发出 Ready 的真实子进程仍可正常结束宿主流程");

            RunPolicy(check);
        }
        finally
        {
            if (parent != null) { if (!parent.HasExited) { parent.Kill(true); parent.WaitForExit(3000); } parent.Dispose(); }
            if (child != null) { if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); } child.Dispose(); }
            if (parentThread != IntPtr.Zero) CloseHandle(parentThread);
            if (parentHandle != IntPtr.Zero) CloseHandle(parentHandle);
            if (privateDesktop != IntPtr.Zero) CloseDesktop(privateDesktop);
            Directory.Delete(work, true);
        }
    }

    public static void RunPolicy(Action<bool, string> check)
    {
        // Linked directly from the production class used by the actual Host loop.
        var policy = new SupervisorPolicy(8000, 45000, 120000);
        check(!policy.Tick(0, false, false, false, false).KillPlayer, "不可见且未 Ready 的启动不立即失败");
        var hidden = policy.Tick(8000, false, false, false, false);
        check(!hidden.StartDesktop && !hidden.KillPlayer && !hidden.Finished,
            "未就绪且非输入桌面满八秒仍不启动 Explorer，也不终止播放器");
        check(!policy.Tick(60000, false, false, false, false).KillPlayer,
            "未 Ready 的认证等待超过总播放上限仍不消耗初始化或播放预算");
        check(!policy.Tick(61000, false, false, true, false).KillPlayer,
            "输入桌面切入时重新获得完整冷启动预算");
        check(!policy.Tick(90999, false, false, true, false).KillPlayer,
            "可见后的独立三十秒冷启动期限内，尚未 Ready 不提前终止");
        var visibleExpired = policy.Tick(91000, false, false, true, false);
        check(visibleExpired.KillPlayer && visibleExpired.Reason == "cold-start-timeout",
            "可见后尚未 Ready 满三十秒仍清理卡死的播放器");
        var abandoned = new SupervisorPolicy(8000, 45000, 120000);
        abandoned.Tick(0, false, false, false, false);
        check(!abandoned.Tick(119999, false, false, false, false).KillPlayer,
            "非输入桌面且长期未 Ready 仍有独立认证等待期限");
        var waitExpired = abandoned.Tick(120000, false, false, false, false);
        check(waitExpired.KillPlayer && waitExpired.Reason == "input-desktop-wait-timeout",
            "即使从未 Ready，独立认证等待期限到达也清理进程");
        var lateReady = new SupervisorPolicy(2000, 5000, 120000, 10000);
        lateReady.Tick(0, false, false, true, false);
        check(!lateReady.Tick(9999, true, false, true, false).KillPlayer,
            "冷启动期限前一毫秒终于 Ready 时，不按旧初始化预算立即清理");
        check(!lateReady.Tick(11998, true, false, true, false).KillPlayer,
            "迟到的遮罩 Ready 后仍获得完整两秒播放握手预算");
        var lateHandshake = lateReady.Tick(11999, true, false, true, false);
        check(lateHandshake.KillPlayer && lateHandshake.Reason == "playback-handshake-timeout",
            "迟到 Ready 后实际握手卡死仍在独立期限内清理");
    }

    private static bool IsInputDesktop(IntPtr desktop)
    {
        if (!GetUserObjectInformationW(desktop, 6, out int value, sizeof(int), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return value != 0;
    }
    private static string ObjectName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationName(handle, 2, name, name.Capacity * sizeof(char), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktopW(string desktop, string? device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, out int value, int length, out int needed);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationName(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
