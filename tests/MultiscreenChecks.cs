using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WinBootVideo.Core;
using WinBootVideo.Shared;

internal static class MultiscreenChecks
{
    public static Task<bool> Run(string bundle, Action<bool, string> check)
    {
        int monitorCount = 0;
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (_, _, _, _) => { monitorCount++; return true; }, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate real monitors.");
        if (monitorCount < 2)
        {
            Console.WriteLine("SKIP: actual full-screen multi-monitor regression requires at least two real screens; found " + monitorCount + ".");
            return Task.FromResult(false);
        }
        RunCore(Path.GetFullPath(bundle), monitorCount, check);
        return Task.FromResult(true);
    }

    private static void RunCore(string bundle, int monitorCount, Action<bool, string> check)
    {
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-multiscreen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        IntPtr originalDesktop = GetThreadDesktop(GetCurrentThreadId());
        IntPtr desktop = IntPtr.Zero;
        IntPtr childHandle = IntPtr.Zero;
        IntPtr childThread = IntPtr.Zero;
        Process? child = null;
        try
        {
            string name = "WinBootVideoMulti_" + Guid.NewGuid().ToString("N");
            // No DESKTOP_SWITCHDESKTOP permission: this test cannot expose its
            // full-screen windows or consume the user's keyboard input.
            desktop = CreateDesktopW(name, null, IntPtr.Zero, 0, 0x00c7, IntPtr.Zero);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create isolated test desktop.");
            check(!IsInputDesktop(desktop), "多屏回归只在私有非输入桌面执行，不遮挡用户桌面");
            var paths = new AppPaths(work);
            string video = Path.Combine(bundle, "demo-login.mp4");
            if (!File.Exists(video)) throw new FileNotFoundException("The bundle must contain the existing demo clip.", video);
            new SettingsStore(paths).Write(new Settings
            {
                VideoPath = video,
                VideoSha256 = SettingsStore.HashAsync(video, CancellationToken.None).GetAwaiter().GetResult(),
                Volume = 0,
                FadeOutMilliseconds = 0,
                StartupTimeoutSeconds = 3,
                MaxDurationSeconds = 5,
                CoverOtherScreens = true
            });
            string token = Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\WinBootVideo.Ready." + token);
            using var playback = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\WinBootVideo.Playback." + token);
            string executable = Path.Combine(bundle, "WinBootVideo.exe");
            // Run the real published production Player in full-screen mode. Only
            // --preview avoids login flow; --windowed is deliberately absent.
            string[] arguments = { "--player", "--preview", "--config", paths.SettingsFile, "--data-dir", work, "--receipt", Path.Combine(work, "receipt.json"), "--token", token };
            var commandLine = new StringBuilder(WindowsCommandLine.Quote(executable) + " " + string.Join(" ", arguments.Select(WindowsCommandLine.Quote)));
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = ObjectName(GetProcessWindowStation()) + "\\" + name
            };
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, bundle, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start actual full-screen Player on isolated desktop.");
            childHandle = process.Process;
            childThread = process.Thread;
            child = Process.GetProcessById((int)process.ProcessId);
            var elapsed = Stopwatch.StartNew();
            int maximumVisibleWindows = 0;
            bool receivedReady = false;
            while (elapsed.ElapsedMilliseconds < 5000)
            {
                maximumVisibleWindows = Math.Max(maximumVisibleWindows, CountOwnFullscreenWindows(desktop, process.ProcessId));
                receivedReady |= ready.WaitOne(0);
                if (receivedReady || child.HasExited) break;
                Thread.Sleep(50);
            }
            string timingPath = Path.Combine(paths.LogDirectory, "playback-timing.log");
            string timing = File.Exists(timingPath) ? File.ReadAllText(timingPath) : "";
            bool mainRendered = timing.Contains(" window-content-rendered");
            bool inputActive = IsInputDesktop(desktop);
            bool receivedPlayback = playback.WaitOne(0);
            string evidence = $"monitors={monitorCount} elapsed={elapsed.Elapsed.TotalSeconds:F2}s mainRendered={mainRendered} ownVisibleWindows={maximumVisibleWindows} ready={receivedReady} playback={receivedPlayback} inputActive={inputActive} exited={child.HasExited}";
            Console.WriteLine("Multiscreen evidence: " + evidence);
            Console.WriteLine(timing.TrimEnd());
            check(mainRendered, "真实主播放器已触发 ContentRendered，排除尚未启动窗口的无效复现");
            check(maximumVisibleWindows >= monitorCount, "真实副屏遮罩 HWND 已建立并显示，覆盖全部真实屏幕");
            check(!inputActive && GetThreadDesktop(GetCurrentThreadId()) == originalDesktop,
                "多屏测试从未切换输入桌面或调用线程桌面");
            check(!receivedPlayback && !timing.Contains(" playback-start "), "多屏遮罩就绪测试不在不可见桌面播放视频");
            check(receivedReady, "所有真实屏幕遮罩在五秒内完成 Ready 握手，不因空白副屏窗口卡死。 " + evidence);
        }
        finally
        {
            // Every PID and handle here belongs to this test's child only.
            if (child != null)
            {
                if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); }
                child.Dispose();
            }
            if (childThread != IntPtr.Zero) CloseHandle(childThread);
            if (childHandle != IntPtr.Zero) CloseHandle(childHandle);
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            Directory.Delete(work, true);
        }
    }

    private static bool IsInputDesktop(IntPtr desktop)
    {
        if (!GetUserObjectInformationW(desktop, 6, out int input, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify private desktop input status.");
        return input != 0;
    }

    private static string ObjectName(IntPtr handle)
    {
        var text = new StringBuilder(256);
        if (!GetUserObjectInformationName(handle, 2, text, text.Capacity * sizeof(char), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read own window station name.");
        return text.ToString();
    }

    private static int CountOwnFullscreenWindows(IntPtr desktop, uint processId)
    {
        int count = 0;
        bool enumerated = EnumDesktopWindows(desktop, (window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == processId && IsWindowVisible(window) && GetWindowRect(window, out var bounds)
                && bounds.Right - bounds.Left >= 300 && bounds.Bottom - bounds.Top >= 300) count++;
            return true;
        }, IntPtr.Zero);
        // A newly created desktop can have no windows yet; the native API may
        // return false with no error during that expected startup interval.
        if (!enumerated && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enumerate own private desktop windows.");
        return count;
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
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr state);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktopW(string desktop, string? device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, out int value, int length, out int needed);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationName(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
