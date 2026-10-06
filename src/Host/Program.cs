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
        private static bool startupFallback;
        private static string entrySource;
        private static bool ownsLoginFlow = true;
        private static readonly Stopwatch lifetime = Stopwatch.StartNew();
        private static volatile bool supervisorRunning;
        private static volatile bool flowFinished;
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
            startupFallback = Array.IndexOf(args, "--startup-fallback") >= 0;
            entrySource = startupFallback ? (preview ? "task-preview" : "task") : (preview ? "preview" : "shell");
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinBootVideo");
            bool restoring = Array.IndexOf(args, "--restore") >= 0;
            if (!restoring && (!startupFallback || preview)) StartInitializerGuard();
            try
            {
                dataDirectory = Path.GetFullPath(Argument(args, "--data-dir", dataDirectory));
                Log("host-entry source=" + entrySource + " pid=" + Process.GetCurrentProcess().Id + " version=0.2.5");
                if (restoring) { RestoreShell(); return 0; }
                if (startupFallback && !preview)
                {
                    Log("strict-mode: late task playback disabled");
                    return 0;
                }
                if (!preview)
                {
                    if (GetShellWindow() != IntPtr.Zero && InputDesktopState.Read().CanPresent)
                    {
                        Log("strict-mode: desktop already visible; skip late animation");
                        ownsLoginFlow = false;
                        return 0;
                    }
                    string session = LogonSessionIdentity.Read();
                    return LoginAttemptGate.Run(session, Path.Combine(dataDirectory, "last-logon.txt"),
                        delegate { return Run(args); },
                        delegate { if (!startupFallback) EnsureDesktop(); ownsLoginFlow = false; },
                        delegate(string message) { Log(message + " source=" + entrySource); if (message == "duplicate-mutex") ownsLoginFlow = false; });
                }
                return Run(args);
            }
            catch (Exception ex) { Log("host-failure: " + ex); return 30; }
            finally { flowFinished = true; if (!preview && !startupFallback && ownsLoginFlow) EnsureDesktop(); }
        }
        private static void StartInitializerGuard()
        {
            // Covers configuration / session lookup / Process.Start before supervision exists.
            var initializerGuard = new Thread(delegate()
            {
                while (!flowFinished && !supervisorRunning)
                {
                    if (lifetime.ElapsedMilliseconds >= StartupLimits.ColdStartMilliseconds)
                    {
                        if (flowFinished || supervisorRunning) return;
                        Log("host-initialization-timeout source=" + entrySource);
                        if (!preview && !startupFallback && ownsLoginFlow) EnsureDesktop();
                        Environment.Exit(31);
                    }
                    Thread.Sleep(100);
                }
            });
            initializerGuard.IsBackground = true;
            initializerGuard.Start();
        }
        private static bool IsOwnShellEnabled()
        {
            string host = Path.Combine(dataDirectory, "app", "WinBootVideo.Host.exe");
            string command = WindowsCommandLine.Quote(host) + " --data-dir " + WindowsCommandLine.Quote(dataDirectory);
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon"))
            {
                string current = key == null ? "" : Convert.ToString(key.GetValue("Shell", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                return string.Equals(current, command, StringComparison.OrdinalIgnoreCase);
            }
        }
        private static int Run(string[] args)
        {
            string configPath = Argument(args, "--config", Path.Combine(dataDirectory, "settings.json"));
            var json = new JavaScriptSerializer();
            var config = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath));
            int total = Number(config, "maxDurationSeconds", 45, 5, 90);
            int startup = Number(config, "startupTimeoutSeconds", 8, 2, 15);
            if (startup >= total) throw new InvalidDataException("Invalid time limits.");
            object video;
            if (!config.TryGetValue("videoPath", out video) || !File.Exists(Convert.ToString(video))) throw new FileNotFoundException("Video missing.");
            string player = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinBootVideo.exe");
            if (!File.Exists(player)) throw new FileNotFoundException("Player missing.");
            string token = Guid.NewGuid().ToString("N");
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\WinBootVideo.Ready." + token))
            using (var playback = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\WinBootVideo.Playback." + token))
            using (var firstFrame = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\WinBootVideo.FirstFrame." + token))
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
                    Log("login-flow mode=" + (preview ? "preview" : "login") + " source=" + entrySource + " version=0.2.5 strict-before-desktop=true");
                    supervisorRunning = true;
                    var clock = lifetime;
                    var policy = new SupervisorPolicy(startup * 1000L, total * 1000L);
                    bool desktopStarted = false;
                    long lastDesktopAttempt = -3000;
                    string lastDesktopReason = "";
                    bool playbackSeen = false;
                    bool overlaySeen = false;
                    bool firstFrameSeen = false;
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
                        bool framePresented = firstFrame.WaitOne(0);
                        if (framePresented && !firstFrameSeen) { firstFrameSeen = true; Log("first-frame-confirmed elapsed-ms=" + clock.ElapsedMilliseconds); }
                        var action = policy.Tick(clock.ElapsedMilliseconds, rendered, started, desktop.CanPresent, process.HasExited, framePresented);
                        if (desktopStarted && !preview && !startupFallback && GetShellWindow() == IntPtr.Zero && clock.ElapsedMilliseconds - lastDesktopAttempt >= 3000)
                        { EnsureDesktop(); lastDesktopAttempt = clock.ElapsedMilliseconds; }
                        if (action.KillPlayer)
                        {
                            Log(action.Reason);
                            try { process.Kill(); process.WaitForExit(500); } catch (InvalidOperationException) { }
                        }
                        if (action.StartDesktop)
                        {
                            Log("desktop-release-decision mode=" + (preview ? "preview" : "login") + " reason=" + (action.Finished ? "animation-abandoned" : "first-frame"));
                            if (!preview && !startupFallback) { EnsureDesktop(); desktopStarted = true; lastDesktopAttempt = clock.ElapsedMilliseconds; }
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
            if (preview || startupFallback || !ownsLoginFlow) return;
            try
            {
                desktopLaunch.Ensure();
            }
            catch (Exception ex) { Log("Explorer fallback failed: " + ex.Message); }
        }
        private static void RestoreShell()
        {
            Exception shellFailure = null;
            Exception taskFailure = null;
            bool shellRestored = false;
            bool taskRestored = false;
            try { shellRestored = RestoreOwnShell(); }
            catch (Exception ex) { shellFailure = ex; Log("shell-recovery-failure: " + ex.Message); }
            try { taskRestored = RestoreOwnTask(); }
            catch (Exception ex) { taskFailure = ex; Log("task-recovery-failure: " + ex.Message); }
            if (shellFailure != null || taskFailure != null)
                throw new InvalidOperationException("Startup recovery incomplete; shell-restored=" + shellRestored + "; task-restored=" + taskRestored, shellFailure ?? taskFailure);
            Log("Startup recovery complete; shell-restored=" + shellRestored + "; task-restored=" + taskRestored);
        }
        private static bool RestoreOwnShell()
        {
            if (!IsOwnShellEnabled()) { Log("shell-disabled-or-foreign-preserved"); return false; }
            string path = Path.Combine(dataDirectory, "shell-backup.json");
            var backup = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            if (Convert.ToString(backup["userSid"]) != WindowsIdentity.GetCurrent().User.Value) throw new InvalidDataException("Backup user mismatch.");
            if (Convert.ToInt32(backup["schemaVersion"]) != 1) throw new InvalidDataException("Backup version.");
            string expected = WindowsCommandLine.Quote(Path.Combine(dataDirectory, "app", "WinBootVideo.Host.exe")) + " --data-dir " + WindowsCommandLine.Quote(dataDirectory);
            if (!string.Equals(Convert.ToString(backup["ownedCommand"]), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup application mismatch.");
            var original = (Dictionary<string, object>)backup["original"];
            int kind = Convert.ToInt32(original["kind"]);
            if (Convert.ToBoolean(original["exists"]) && kind != 1 && kind != 2) throw new InvalidDataException("Unsupported backup kind.");
            const string keyPath = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                string current = Convert.ToString(key.GetValue("Shell", "", RegistryValueOptions.DoNotExpandEnvironmentNames));
                if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Shell has changed; recovery will not overwrite it.");
                if (Convert.ToBoolean(original["exists"]))
                {
                    key.SetValue("Shell", Convert.ToString(original["value"]), (RegistryValueKind)kind);
                }
                else key.DeleteValue("Shell", false);
            }
            Log("Original current-user Shell restored.");
            return true;
        }
        private static bool RestoreOwnTask()
        {
            string path = Path.Combine(dataDirectory, "login-task-backup.json");
            if (!File.Exists(path)) return false;
            var backup = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            string sid = WindowsIdentity.GetCurrent().User.Value;
            string taskName = LoginTaskDefinition.TaskName(sid);
            string host = Path.Combine(dataDirectory, "app", "WinBootVideo.Host.exe");
            if (Convert.ToInt32(backup["schemaVersion"]) != 1 || Convert.ToString(backup["userSid"]) != sid || Convert.ToString(backup["taskName"]) != taskName)
                throw new InvalidDataException("Login task backup identity mismatch.");
            if (!LoginTaskDefinition.IsOwned(Convert.ToString(backup["ownedXml"]), host, dataDirectory, sid))
                throw new InvalidDataException("Login task backup application mismatch.");
            var original = (Dictionary<string, object>)backup["original"];
            bool originalExists = Convert.ToBoolean(original["exists"]);
            string originalXml = Convert.ToString(original["xml"]);
            if (originalExists && string.IsNullOrWhiteSpace(originalXml)) throw new InvalidDataException("Original login task backup is empty.");
            if (originalExists && !LoginTaskDefinition.IsOwned(originalXml, host, dataDirectory, sid))
                throw new InvalidDataException("Original login task backup application mismatch.");
            var store = new ComLoginTaskStore();
            var current = store.Read(taskName);
            if (!current.Exists) { Log("task-already-absent"); return false; }
            if (!LoginTaskDefinition.IsOwned(current.Xml, host, dataDirectory, sid)) { Log("task-foreign-preserved"); return false; }
            if (originalExists) store.Write(taskName, originalXml);
            else store.Delete(taskName);
            Log("Original current-user login task restored.");
            return true;
        }
        private static void Log(string message)
        {
            try { string folder = Path.Combine(dataDirectory, "logs"); Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "host.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { }
        }
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    }
}
