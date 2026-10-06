using WinBootVideo.Host;

internal static class LoginWaitChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Replay the observed 2026-10-06 host trace with a virtual clock:
        // 13:15:56 own-desktop-not-input; 13:15:57 overlay-confirmed;
        // 13:30:56 input-desktop-wait-timeout. No Windows settings are changed.
        var policy = new SupervisorPolicy(8000, 45000);
        policy.Tick(171, false, false, false, false);
        policy.Tick(1726, true, false, false, false);
        var waiting = policy.Tick(900000, true, false, false, false);
        check(!waiting.Finished && !waiting.KillPlayer && !waiting.StartDesktop,
            "重放实际日志：认证桌面仍在前台，十五分钟后仍保留首次登录动画而不提前启动 Explorer");
        // The following transition is synthetic: the user signs in after the
        // observed timeout. Playback must still get its complete visible budget.
        check(!policy.Tick(1200000, true, false, true, false).Finished,
            "晚于二十分钟进入输入桌面时仍获得完整的加载预算");
        check(!policy.Tick(1200300, true, true, true, false).StartDesktop,
            "晚登录后请求播放仍不提前放行桌面");
        var firstFrame = policy.Tick(1200500, true, true, true, false, true);
        check(firstFrame.StartDesktop && !firstFrame.Finished && !firstFrame.KillPlayer,
            "晚登录后首帧呈现才放行桌面");
        check(!policy.Tick(1201000, true, true, true, false, true).StartDesktop,
            "晚登录时也只放行一次桌面");
    }
}
