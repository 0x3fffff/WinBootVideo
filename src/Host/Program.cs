using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using WinBootVideo.Shared;

namespace WinBootVideo.Host
{
    internal static class Program
    {
        private static string dataDirectory;
        private static bool preview;
        private static bool ownsLoginFlow = true;
        private static readonly Stopwatch lifetime = Stopwatch.StartNew();
        private static volatile bool supervisorRunning;
        private static volatile bool flowFinished;
        private static int startupLimitMilliseconds = 8000;
        private static Process explorerStarted;
        private static readonly DesktopLaunchCoordinator desktopLaunch = new DesktopLaunchCoordinator(
            delegate { return GetShellWindow() != IntPtr.Zero; },
            delegate
            {
                if (explorerStarted != null) explorerStarted.Dispose();
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                explorerStarted = Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(explorer) });
                if (explorerStarted == null) throw new IOException("Explorer launch did not return a process.");
                Log("Explorer requested in current session. pid=" + explorerStarted.Id);
            },
            delegate { return lifetime.ElapsedMilliseconds; });
        private static string Argument(string[] args, string name, string fallback)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        [STAThread]
        private static int Main(string[] args)
        {
            preview = Array.IndexOf(args, "--preview") >= 0;
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinBootVideo");
            // Covers configuration / session lookup / Process.Start before the supervision loop exists.
            var initializerGuard = new Thread(delegate()
            {
                while (!flowFinished && !supervisorRunning)
                {
                    if (lifetime.ElapsedMilliseconds >= startupLimitMilliseconds)
                    {
                        if (flowFinished || supervisorRunning) return;
                        Log("host-initialization-timeout");
                        if (!preview && ownsLoginFlow) EnsureDesktop();
                        Environment.Exit(31);
                    }
                    Thread.Sleep(100);
                }
            });
            initializerGuard.IsBackground = true;
            initializerGuard.Start();
            try
            {
                dataDirectory = Path.GetFullPath(Argument(args, "--data-dir", dataDirectory));
                if (Array.IndexOf(args, "--restore") >= 0) { RestoreShell(); return 0; }
                if (!preview)
                {
                    string session = LogonSessionIdentity.Read();
                    bool created;
                    using (var mutex = new Mutex(true, @"Local\WinBootVideo.Host." + session, out created))
                    {
                        if (!created) { ownsLoginFlow = false; return 0; }
                        try
                        {
                            string stamp = Path.Combine(dataDirectory, "last-logon.txt");
                            if (File.Exists(stamp) && File.ReadAllText(stamp) == session) { EnsureDesktop(); return 0; }
                            Directory.CreateDirectory(dataDirectory);
                            File.WriteAllText(stamp, session);
                            return Run(args);
                        }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
                return Run(args);
            }
            catch (Exception ex) { Log("host-failure: " + ex); return 30; }
            finally { flowFinished = true; if (!preview && ownsLoginFlow) EnsureDesktop(); }
        }
        private static int Run(string[] args)
        {
            string configPath = Argument(args, "--config", Path.Combine(dataDirectory, "settings.json"));
            var json = new JavaScriptSerializer();
            var config = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
            int total = Number(config, "maxDurationSeconds", 45, 5, 90);
            int startup = Number(config, "startupTimeoutSeconds", 8, 2, 15);
            startupLimitMilliseconds = startup * 1000;
            if (startup >= total) throw new InvalidDataException("Invalid time limits.");
            object video;
            if (!config.TryGetValue("videoPath", out video) || !File.Exists(Convert.ToString(video))) throw new FileNotFoundException("Video missing.");
            string player = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinBootVideo.exe");
            if (!File.Exists(player)) throw new FileNotFoundException("Player missing.");
            string token = Guid.NewGuid().ToString("N");
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\WinBootVideo.Ready." + token))
            using (var playback = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\WinBootVideo.Playback." + token))
            {
                var info = new ProcessStartInfo(player);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                info.Arguments = "--player --config " + WindowsCommandLine.Quote(configPath) + " --token " + token + " --parent " + Process.GetCurrentProcess().Id + " --data-dir " + WindowsCommandLine.Quote(dataDirectory);
                if (preview) info.Arguments += " --preview";
                string receipt = Argument(args, "--receipt", "");
                if (!string.IsNullOrEmpty(receipt)) info.Arguments += " --receipt " + WindowsCommandLine.Quote(receipt);
                if (Array.IndexOf(args, "--windowed") >= 0) info.Arguments += " --windowed";
                using (var process = Process.Start(info))
                {
                    if (process == null) throw new IOException("Cannot start player.");
                    Log("player-start: " + process.Id);
                    Log("login-flow mode=" + (preview ? "preview" : "login") + " version=0.1.4");
                    supervisorRunning = true;
                    var clock = lifetime;
                    var policy = new SupervisorPolicy(startup * 1000L, total * 1000L);
                    bool desktopStarted = false;
                    long lastDesktopAttempt = -3000;
                    string lastDesktopReason = "";
                    bool playbackSeen = false;
                    bool overlaySeen = false;
                    while (true)
                    {
                        var desktop = InputDesktopState.Read();
                        if (lastDesktopReason != desktop.Reason)
                        {
                            lastDesktopReason = desktop.Reason;
                            Log("input-desktop elapsed-ms=" + clock.ElapsedMilliseconds + " state=" + desktop.Reason);
                        }
                        bool started = playback.WaitOne(0);
                        if (started && !playbackSeen) { playbackSeen = true; Log("playback-confirmed elapsed-ms=" + clock.ElapsedMilliseconds); }
                        bool rendered = ready.WaitOne(0);
                        if (rendered && !overlaySeen) { overlaySeen = true; Log("overlay-confirmed elapsed-ms=" + clock.ElapsedMilliseconds); }
                        var action = policy.Tick(clock.ElapsedMilliseconds, rendered, started, desktop.CanPresent, process.HasExited);
                        if (action.StartDesktop && !preview) { EnsureDesktop(); desktopStarted = true; lastDesktopAttempt = clock.ElapsedMilliseconds; }
                        if (desktopStarted && !preview && GetShellWindow() == IntPtr.Zero && clock.ElapsedMilliseconds - lastDesktopAttempt >= 3000)
                        { EnsureDesktop(); lastDesktopAttempt = clock.ElapsedMilliseconds; }
                        if (action.KillPlayer)
                        {
                            Log(action.Reason);
                            try { process.Kill(); process.WaitForExit(500); } catch (InvalidOperationException) { }
                        }
                        if (action.Finished)
                        {
                            if (action.KillPlayer) return 31;
                            Log("player-exit: " + process.ExitCode);
                            return process.ExitCode;
                        }
                        Thread.Sleep(100);
                    }
                }
            }
        }
        private static int Number(Dictionary<string, object> values, string key, int fallback, int minimum, int maximum)
        {
            object value;
            int result = values.TryGetValue(key, out value) ? Convert.ToInt32(value) : fallback;
            if (result < minimum || result > maximum) throw new InvalidDataException(key);
            return result;
        }
        private static void EnsureDesktop()
        {
            try
            {
                desktopLaunch.Ensure();
            }
            catch (Exception ex) { Log("Explorer fallback failed: " + ex.Message); }
        }
        private static void RestoreShell()
        {
            string path = Path.Combine(dataDirectory, "shell-backup.json");
            var backup = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            if (Convert.ToString(backup["userSid"]) != WindowsIdentity.GetCurrent().User.Value) throw new InvalidDataException("Backup user mismatch.");
            if (Convert.ToInt32(backup["schemaVersion"]) != 1) throw new InvalidDataException("Backup version.");
            var original = (Dictionary<string, object>)backup["original"];
            const string keyPath = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                string current = Convert.ToString(key.GetValue("Shell", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                if (!string.Equals(current, Convert.ToString(backup["ownedCommand"]), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Shell has changed; recovery will not overwrite it.");
                if (Convert.ToBoolean(original["exists"]))
                {
                    int kind = Convert.ToInt32(original["kind"]);
                    if (kind != 1 && kind != 2) throw new InvalidDataException("Unsupported backup kind.");
                    key.SetValue("Shell", Convert.ToString(original["value"]), (RegistryValueKind)kind);
                }
                else key.DeleteValue("Shell", false);
            }
            Log("Original current-user Shell restored.");
        }
        private static void Log(string message)
        {
            try { string folder = Path.Combine(dataDirectory, "logs"); Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "host.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { }
        }
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    }
}
