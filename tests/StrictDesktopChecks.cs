using WinBootVideo.Host;
using System.IO;
using WinBootVideo.Core;
using WinBootVideo.Shared;

internal static class StrictDesktopChecks
{
    public static void Run(Action<bool, string> check)
    {
        var policy = new SupervisorPolicy(8000, 45000);
        check(!policy.Tick(1900, true, false, false, false).StartDesktop,
            "生产 Shell 策略：只有黑色遮罩就绪、尚无可见视频时不启动 Explorer");
        check(!policy.Tick(8000, true, false, false, false).StartDesktop,
            "生产 Shell 策略：认证等待中不因八秒软期限提前露出桌面");
        check(!policy.Tick(11000, true, true, true, false).StartDesktop,
            "媒体已请求播放但尚未确认首帧时仍不启动 Explorer");
        check(policy.Tick(11200, true, true, true, false, true).StartDesktop,
            "输入桌面上视频首帧呈现后才放行 Explorer");
        check(!policy.Tick(11300, true, true, true, false, true).StartDesktop,
            "正常交接只请求一次 Explorer");
        var failure = new SupervisorPolicy(8000, 45000);
        failure.Tick(0, true, false, true, false);
        var abandoned = failure.Tick(8000, true, false, true, false);
        check(abandoned.Finished && abandoned.KillPlayer && abandoned.StartDesktop,
            "首帧握手失败时结束动画再放行桌面，不维持晚到的播放器");
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-strict-" + Guid.NewGuid().ToString("N"));
        const string sid = "S-1-5-21-1-2-3-1001";
        try
        {
            var paths = new AppPaths(work);
            var registry = new FakeRegistry();
            var tasks = new FakeTaskStore();
            var startup = new LoginStartupController(paths, registry, tasks);
            startup.Enable(sid); // Reproduce an existing 0.1.5 installation.
            string backup = File.ReadAllText(paths.BackupFile);
            startup.EnableStrict(sid);
            check(startup.IsStrictEnabled(sid) && !tasks.Read(LoginTaskDefinition.TaskName(sid)).Exists
                && File.ReadAllText(paths.BackupFile) == backup,
                "从旧版迁移严格模式时删除自己的备用任务，保留原 Shell 和恢复备份");
            startup.Disable(sid);
            check(!registry.Current.Exists, "严格模式仍可按原始备份恢复普通登录");
            startup.EnableStrict(sid);
            check(startup.IsStrictEnabled(sid) && !tasks.Read(LoginTaskDefinition.TaskName(sid)).Exists,
                "严格模式首次启用只设置 Shell，不创建晚到的备用播放任务");
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }
}
