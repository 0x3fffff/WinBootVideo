using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinBootVideo.Core;
using WinBootVideo.Shared;

internal static class HiddenDesktopChecks
{
    // The child alone uses this private desktop. The test never switches the input
    // desktop, changes its own thread desktop, locks Windows, or runs the login Host.
    public static Task Run(string bundle, Action<bool, string> check)
    {
        RunCore(Path.GetFullPath(bundle), check);
        return Task.CompletedTask;
    }

    private static void RunCore(string bundle, Action<bool, string> check)
    {
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-hidden-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        IntPtr originalDesktop = GetThreadDesktop(GetCurrentThreadId());
        IntPtr privateDesktop = IntPtr.Zero;
        IntPtr childHandle = IntPtr.Zero;
        IntPtr childThread = IntPtr.Zero;
        Process? child = null;
        try
        {
            string desktopName = "WinBootVideoTest_" + Guid.NewGuid().ToString("N");
            // Read / create-window / create-menu / enumerate / write-object access;
            // deliberately omit DESKTOP_SWITCHDESKTOP.
            privateDesktop = CreateDesktopW(desktopName, null, IntPtr.Zero, 0, 0x00c7, IntPtr.Zero);
            if (privateDesktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create isolated test desktop.");
            check(!IsInputDesktop(privateDesktop), "创建本测试专用非输入桌面，确认 UOI_IO = false");
            var paths = new AppPaths(work);
            string video = Path.Combine(bundle, "demo-login.mp4");
            if (!File.Exists(video)) throw new FileNotFoundException("The bundle must contain the existing five-second demo clip.", video);
            string receipt = Path.Combine(work, "receipt.json");
            var settings = new Settings
            {
                VideoPath = video,
                VideoSha256 = SettingsStore.HashAsync(video, CancellationToken.None).GetAwaiter().GetResult(),
                Volume = 0,
                FadeOutMilliseconds = 100,
                StartupTimeoutSeconds = 3,
                MaxDurationSeconds = 5,
                CoverOtherScreens = false
            };
            new SettingsStore(paths).Write(settings);
            string executable = Path.Combine(bundle, "WinBootVideo.exe");
            string[] arguments = { "--player", "--preview", "--windowed", "--config", paths.SettingsFile, "--data-dir", work, "--receipt", receipt };
            var commandLine = new StringBuilder(WindowsCommandLine.Quote(executable) + " " + string.Join(" ", arguments.Select(WindowsCommandLine.Quote)));
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = ObjectName(GetProcessWindowStation()) + "\\" + desktopName
            };
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, bundle, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start the actual Player on its private desktop.");
            childHandle = process.Process;
            childThread = process.Thread;
            child = Process.GetProcessById((int)process.ProcessId);
            var elapsed = Stopwatch.StartNew();
            bool createdWindow = false;
            while (elapsed.Elapsed.TotalSeconds < 8)
            {
                createdWindow |= HasOwnWindow(privateDesktop, process.ProcessId);
                if (child.HasExited) break;
                Thread.Sleep(100);
            }
            check(createdWindow, "实际 WPF Player 已在私有桌面创建自己的窗口");
            check(!IsInputDesktop(privateDesktop), "整个等待期间私有桌面保持非输入桌面");
            WinBootVideo.PreviewReceipt? report = File.Exists(receipt)
                ? JsonSerializer.Deserialize<WinBootVideo.PreviewReceipt>(File.ReadAllText(receipt), Settings.JsonOptions)
                : null;
            if (!GetExitCodeProcess(childHandle, out uint exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
            string evidence = $"elapsed={elapsed.Elapsed.TotalSeconds:F2}s exited={child.HasExited} exit={exitCode} receipt={report != null} opened={report?.Opened} advanced={report?.PlaybackAdvanced} succeeded={report?.Succeeded} message={report?.Message}";
            Console.WriteLine("Hidden desktop evidence: " + evidence);
            check(GetThreadDesktop(GetCurrentThreadId()) == originalDesktop, "调用线程桌面始终保持原样，未切换用户输入桌面");
            check(!child.HasExited && report?.PlaybackAdvanced != true && report?.Succeeded != true,
                "不可见时保持等待，不推进视频；等待八秒超过五秒播放预算仍不提前退出。 " + evidence);
        }
        finally
        {
            // These handles and this PID were created by this test alone.
            if (child != null)
            {
                if (!child.HasExited) { child.Kill(); child.WaitForExit(3000); }
                child.Dispose();
            }
            if (childThread != IntPtr.Zero) CloseHandle(childThread);
            if (childHandle != IntPtr.Zero) CloseHandle(childHandle);
            if (privateDesktop != IntPtr.Zero) CloseDesktop(privateDesktop);
            Directory.Delete(work, true);
        }
    }

    private static bool IsInputDesktop(IntPtr desktop)
    {
        if (!GetUserObjectInformationW(desktop, 6, out int isInput, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify private desktop UOI_IO.");
        return isInput != 0;
    }

    private static string ObjectName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        if (!GetUserObjectInformationName(handle, 2, name, name.Capacity * sizeof(char), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read this process window station name.");
        return name.ToString();
    }

    private static bool HasOwnWindow(IntPtr desktop, uint processId)
    {
        bool found = false;
        EnumDesktopWindows(desktop, (window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            found |= owner == processId;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktopW(string desktop, string? device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, out int value, int length, out int needed);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformationName(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
}
