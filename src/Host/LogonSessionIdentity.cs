using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WinBootVideo.Host
{
    public static class LogonSessionIdentity
    {
        public static string Key(uint low, int high, long logonTime)
        {
            if (logonTime <= 0) throw new InvalidOperationException("Invalid logon time.");
            // LUID alone is unique only until reboot. Pair it with the documented session logon time.
            return logonTime.ToString("X16") + "." + high.ToString("X8") + low.ToString("X8");
        }
        public static string Read()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                int length = Marshal.SizeOf(typeof(TokenStatistics));
                IntPtr memory = Marshal.AllocHGlobal(length);
                IntPtr session = IntPtr.Zero;
                try
                {
                    int returned;
                    if (!GetTokenInformation(identity.Token, 10, memory, length, out returned)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    var token = Marshal.PtrToStructure<TokenStatistics>(memory);
                    var luid = token.AuthenticationId;
                    int status = LsaGetLogonSessionData(ref luid, out session);
                    if (status != 0 || session == IntPtr.Zero) throw new InvalidOperationException("Cannot read current logon session: " + status);
                    if (Marshal.ReadInt32(session) < Marshal.SizeOf(typeof(SessionPrefix))) throw new InvalidOperationException("Logon session data is incomplete.");
                    var data = Marshal.PtrToStructure<SessionPrefix>(session);
                    return Key(luid.LowPart, luid.HighPart, data.LogonTime);
                }
                finally { Marshal.FreeHGlobal(memory); if (session != IntPtr.Zero) LsaFreeReturnBuffer(session); }
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)] private struct TokenStatistics { public Luid TokenId; public Luid AuthenticationId; public long ExpirationTime; public int TokenType; public int ImpersonationLevel; public uint DynamicCharged; public uint DynamicAvailable; public uint GroupCount; public uint PrivilegeCount; public Luid ModifiedId; }
        [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
        [StructLayout(LayoutKind.Sequential)] private struct SessionPrefix { public uint Size; public Luid LogonId; public UnicodeString UserName; public UnicodeString LogonDomain; public UnicodeString AuthenticationPackage; public uint LogonType; public uint Session; public IntPtr Sid; public long LogonTime; }
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);
        [DllImport("secur32.dll")] private static extern int LsaGetLogonSessionData(ref Luid logonId, out IntPtr sessionData);
        [DllImport("secur32.dll")] private static extern int LsaFreeReturnBuffer(IntPtr buffer);
    }
}
