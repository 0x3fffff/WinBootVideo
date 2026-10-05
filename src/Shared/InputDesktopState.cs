using System;
using System.Runtime.InteropServices;

namespace WinBootVideo.Shared
{
    // Query from the window's UI thread: rendering a window on a desktop does not
    // imply that Windows has switched away from its protected sign-in desktop.
    public sealed class InputDesktopState
    {
        private const int UoiIo = 6;
        private const int WtsCurrentSession = -1;
        private const int WtsConnectState = 8;
        private const int WtsActive = 0;

        public bool CanPresent { get; private set; }
        public string Reason { get; private set; }

        private InputDesktopState(bool canPresent, string reason)
        {
            CanPresent = canPresent;
            Reason = reason;
        }

        public static InputDesktopState Read()
        {
            var input = ReadInputDesktop();
            if (!input.CanPresent) return input;
            return ReadConnectionState();
        }
        // Watchdog threads use the local USER query without a synchronous WTS RPC.
        public static InputDesktopState ReadInputDesktop()
        {
            // This borrowed handle must not be passed to CloseDesktop.
            IntPtr desktop = GetThreadDesktop(GetCurrentThreadId());
            if (desktop == IntPtr.Zero)
                return Unavailable("thread-desktop", Marshal.GetLastWin32Error());

            int receivingInput;
            uint needed;
            if (!GetUserObjectInformation(desktop, UoiIo, out receivingInput, sizeof(int), out needed))
                return Unavailable("input-desktop-query", Marshal.GetLastWin32Error());
            if (needed > sizeof(int))
                return new InputDesktopState(false, "input-desktop-invalid-buffer");
            if (receivingInput == 0)
                return new InputDesktopState(false, "own-desktop-not-input");
            return new InputDesktopState(true, "input-desktop-active");
        }
        private static InputDesktopState ReadConnectionState()
        {
            // WTS can be unavailable early in startup. UOI_IO remains the primary
            // local-console visibility check; a successful WTS result additionally
            // prevents consuming playback in a disconnected or inactive session.
            IntPtr buffer = IntPtr.Zero;
            uint length;
            try
            {
                if (!WTSQuerySessionInformation(IntPtr.Zero, WtsCurrentSession, WtsConnectState, out buffer, out length))
                    return new InputDesktopState(true, "input-desktop-active;wts-unavailable:" + Marshal.GetLastWin32Error());
                if (buffer == IntPtr.Zero || length < sizeof(int))
                    return new InputDesktopState(false, "wts-invalid-buffer");

                int state = Marshal.ReadInt32(buffer);
                if (state != WtsActive)
                    return new InputDesktopState(false, "session-not-active:" + state);
                return new InputDesktopState(true, "input-desktop-active;session-active");
            }
            finally
            {
                if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
            }
        }

        private static InputDesktopState Unavailable(string operation, int error)
        {
            return new InputDesktopState(false, operation + "-failed:" + error);
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, out int information, uint length, out uint needed);

        [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int informationClass, out IntPtr buffer, out uint length);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);
    }
}
