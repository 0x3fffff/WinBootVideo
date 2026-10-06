using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

// Calls only the pinned official path initialization / copy routines. Every
// destination must belong to the same GUID-named temporary checks directory.
// Explicit paths either short-circuit ESP detection or throw before fallback.
internal static class OfficialLogoFiles
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            bool espMode = args.Length == 3 && (args[0] == "--check-esp" || args[0] == "--copy-esp");
            if (args.Length != 2 && !espMode) throw new InvalidDataException("stage and fake EFI folder required");
            string stage = Path.GetFullPath(args[espMode ? 1 : 0]);
            string destination = espMode ? args[2] : Path.GetFullPath(args[1]);
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
            if (!stage.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Stage must be below Temp.");
            string owner = stage.Substring(root.Length).Split('\\')[0];
            const string prefix = "WinBootVideo-checks-";
            Guid ownerId;
            if (!owner.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParse(owner.Substring(prefix.Length), out ownerId))
                throw new InvalidDataException("Stage must belong to a GUID-named checks directory.");
            string ordinaryRoot = root + owner + "\\";
            string guidRoot = GuidTempRoot(root) + owner + "\\";
            string allowedRoot = destination.StartsWith(ordinaryRoot, StringComparison.OrdinalIgnoreCase) ? ordinaryRoot :
                destination.StartsWith(guidRoot, StringComparison.OrdinalIgnoreCase) ? guidRoot : null;
            if (allowedRoot == null) throw new InvalidDataException("Destination must belong to the same temporary checks directory.");
            string relative = destination.Substring(allowedRoot.Length);
            if (relative.IndexOfAny(new[] { ':', '/' }) >= 0) throw new InvalidDataException("Invalid destination suffix.");
            foreach (string part in relative.Split('\\')) if (part == "." || part == "..") throw new InvalidDataException("Destination traversal is forbidden.");
            Directory.SetCurrentDirectory(stage);
            if (!espMode) Directory.CreateDirectory(destination);
            Assembly assembly = Assembly.LoadFrom(Path.Combine(stage, "setup.exe"));
            Type type = assembly.GetType("Setup", true); object setup = Activator.CreateInstance(type);
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            if (espMode)
            {
                type.GetField("UserEspPath", flags).SetValue(setup, destination);
                type.GetField("Batch", flags).SetValue(setup, true);
                type.GetMethod("InitEspPath", flags).Invoke(setup, null);
                Console.WriteLine("Official InitEspPath accepted the isolated EFI directory.");
                if (args[0] == "--check-esp") return 0;
                destination = Path.Combine(destination, "EFI", "HackBGRT");
            }
            type.GetField("InstallPath", flags).SetValue(setup, destination);
            type.GetField("EfiArch", flags).SetValue(setup, "x64");
            type.GetField("SkipShim", flags).SetValue(setup, false);
            type.GetMethod("InstallFiles", flags).Invoke(setup, null);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static string GuidTempRoot(string root)
    {
        var mount = new StringBuilder(512); var volume = new StringBuilder(512);
        if (!GetVolumePathName(root, mount, mount.Capacity) || !GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, volume.Capacity))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return volume + root.Substring(mount.Length);
    }
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string path, StringBuilder mount, int length);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mount, StringBuilder volume, int length);
}