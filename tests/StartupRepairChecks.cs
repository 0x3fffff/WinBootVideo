using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using WinBootVideo.Core;
using WinBootVideo.Shared;

internal static class StartupRepairChecks
{
    public static async Task Run(string bundle, Action<bool, string> check)
    {
        bundle = Path.GetFullPath(bundle);
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-repair-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var paths = new AppPaths(work);
        Process? child = null;
        try
        {
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            var registry = new UserShellRegistry();
            string shellBefore = JsonSerializer.Serialize(registry.Read(), Settings.JsonOptions);
            var tasks = new ComLoginTaskStore();
            string taskName = LoginTaskDefinition.TaskName(sid);
            var taskBefore = tasks.Read(taskName);
            string video = Path.Combine(work, "large-hash-only.mp4");
            File.Copy(Path.Combine(bundle, "demo-login.mp4"), video);
            // This is only a hash fixture. Padding forces asynchronous file I/O;
            // the repair command must reject unowned startup before any playback.
            using (var append = new FileStream(video, FileMode.Append, FileAccess.Write))
                append.Write(new byte[2 * 1024 * 1024]);
            string hash = await SettingsStore.HashAsync(video, CancellationToken.None);
            new SettingsStore(paths).Write(new Settings
            {
                VideoPath = video, VideoSha256 = hash, VerifiedVideoSha256 = hash,
                Volume = 0, CoverOtherScreens = false
            });
            string settingsBefore = File.ReadAllText(paths.SettingsFile);
            string report = Path.Combine(work, "startup-report.json");
            var start = new ProcessStartInfo(Path.Combine(bundle, "WinBootVideo.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = bundle
            };
            foreach (string argument in new[] { "--repair-startup", "--data-dir", work, "--startup-report", report })
                start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new IOException("The actual repair CLI did not start.");
            var elapsed = Stopwatch.StartNew();
            bool exited = true;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            {
                try { await child.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { exited = false; }
            }
            string logPath = Path.Combine(paths.LogDirectory, "player.log");
            string trace = File.Exists(logPath) ? File.ReadAllText(logPath) : "<no repair log>";
            Console.WriteLine($"Startup repair CLI evidence: bytes={new FileInfo(video).Length} elapsed={elapsed.Elapsed.TotalSeconds:F2}s exited={exited} exit={(exited ? child.ExitCode.ToString() : "<running>")} report={File.Exists(report)}" + Environment.NewLine + trace.TrimEnd());
            check(File.ReadAllText(paths.SettingsFile) == settingsBefore && !File.Exists(report)
                && !File.Exists(paths.BackupFile) && !File.Exists(Path.Combine(work, "login-task-backup.json"))
                && !File.Exists(Path.Combine(work, "last-logon.txt")),
                "真实修复 CLI 对未拥有启动入口的独立配置不写设置、成功报告、备份或登录标记");
            var taskAfter = tasks.Read(taskName);
            check(JsonSerializer.Serialize(registry.Read(), Settings.JsonOptions) == shellBefore
                && taskAfter.Exists == taskBefore.Exists && taskAfter.Xml == taskBefore.Xml,
                "真实修复 CLI 没有改变当前用户 Shell 或真实登录任务");
            check(exited && child.ExitCode == 30 && elapsed.Elapsed.TotalSeconds < 8.5
                && trace.Contains("此命令仅修复已有登录动画", StringComparison.Ordinal),
                "真实 WPF 修复 CLI 完成大文件异步 SHA 校验后八秒内明确拒绝首次启用，不死锁界面线程");
        }
        finally
        {
            if (child != null)
            {
                if (!child.HasExited) { child.Kill(true); child.WaitForExit(3000); }
                child.Dispose();
            }
            Directory.Delete(work, true);
        }
    }
}
