using System.IO;
using WinBootVideo.Host;

internal static class DesktopLaunchChecks
{
    public static void Run(Action<bool, string> check)
    {
        int launches = 0;
        long elapsed = 0;
        var slowShell = new DesktopLaunchCoordinator(() => false, () => launches++, () => elapsed);
        check(slowShell.Ensure() && launches == 1, "桌面不存在时首次 Ensure 请求启动 Explorer");
        bool repeated = slowShell.Ensure();
        check(!repeated && launches == 1, "首次 Explorer 尚在启动且没有 Shell 窗口时，finally 的 Ensure 不重复启动");
        elapsed = 60000;
        check(!slowShell.Ensure() && launches == 1, "启动请求已经成功后，超过重试宽限也不再次请求 Explorer");

        int existingLaunches = 0;
        var existing = new DesktopLaunchCoordinator(() => true, () => existingLaunches++, () => 0);
        check(!existing.Ensure() && existingLaunches == 0, "已存在 Shell 窗口时不请求 Explorer");

        int forwardedLaunches = 0;
        long forwardedElapsed = 0;
        var forwarded = new DesktopLaunchCoordinator(() => false, () => forwardedLaunches++, () => forwardedElapsed);
        forwarded.Ensure(); forwardedElapsed = 60000;
        check(!forwarded.Ensure() && forwardedLaunches == 1,
            "Explorer 启动代理正常返回或退出但 Shell 尚未就绪时，本宿主也只请求一次");

        int failedLaunches = 0;
        long retryElapsed = 0;
        var failedStart = new DesktopLaunchCoordinator(() => false,
            () => { if (++failedLaunches == 1) throw new IOException("simulated Process.Start failure"); }, () => retryElapsed);
        bool failed = false;
        try { failedStart.Ensure(); } catch (IOException) { failed = true; }
        check(failed, "明确 Process.Start 异常保留故障信息");
        retryElapsed = 14999;
        check(!failedStart.Ensure() && failedLaunches == 1, "明确启动失败后的重试仍受十五秒冷却约束");
        retryElapsed = 15000;
        check(failedStart.Ensure() && failedLaunches == 2, "明确启动失败超过冷却后允许一次有界重试");
        retryElapsed = 90000;
        check(!failedStart.Ensure() && failedLaunches == 2, "重试成功后 finally 不再重复请求 Explorer");

        int concurrentLaunches = 0;
        var concurrent = new DesktopLaunchCoordinator(() => false,
            () => { Interlocked.Increment(ref concurrentLaunches); Thread.Sleep(30); }, () => 0);
        Parallel.For(0, 12, _ => concurrent.Ensure());
        check(concurrentLaunches == 1, "初始化守护与 finally 并发请求时，仅提交一次 Explorer 启动请求");

        int permanentFailures = 0;
        long failedElapsed = 0;
        var permanentlyFailed = new DesktopLaunchCoordinator(() => false,
            () => { permanentFailures++; throw new IOException("simulated permanent Process.Start failure"); }, () => failedElapsed);
        try { permanentlyFailed.Ensure(); } catch (IOException) { }
        failedElapsed = 15000;
        try { permanentlyFailed.Ensure(); } catch (IOException) { }
        failedElapsed = 30000;
        check(!permanentlyFailed.Ensure() && permanentFailures == 2,
            "持续 Process.Start 异常最多尝试两次，不形成无限重试");
    }
}
