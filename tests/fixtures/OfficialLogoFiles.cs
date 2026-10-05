using System;
using System.IO;
using System.Reflection;

// Executes only the pinned official protected file-copy routine into a regular
// temporary directory. Does not call Main, Run, ESP detection or firmware APIs.
internal static class OfficialLogoFiles
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new InvalidDataException("stage and fake EFI folder required");
            string stage = Path.GetFullPath(args[0]), destination = Path.GetFullPath(args[1]);
            string root = Path.GetFullPath(Path.GetTempPath());
            if (!stage.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !stage.Contains("WinBootVideo-checks-") || !destination.Contains("WinBootVideo-checks-"))
                throw new InvalidDataException("Only a checks-owned temporary directory is allowed");
            Directory.SetCurrentDirectory(stage); Directory.CreateDirectory(destination);
            Assembly assembly = Assembly.LoadFrom(Path.Combine(stage, "setup.exe"));
            Type type = assembly.GetType("Setup", true); object setup = Activator.CreateInstance(type);
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            type.GetField("InstallPath", flags).SetValue(setup, destination);
            type.GetField("EfiArch", flags).SetValue(setup, "x64");
            type.GetField("SkipShim", flags).SetValue(setup, false);
            type.GetMethod("InstallFiles", flags).Invoke(setup, null);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
