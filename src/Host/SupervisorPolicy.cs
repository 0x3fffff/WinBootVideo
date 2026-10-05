namespace WinBootVideo.Host
{
    // Wall-clock supervision remains independent of the WPF dispatcher.
    public sealed class SupervisorDecision
    {
        public bool StartDesktop;
        public bool KillPlayer;
        public bool Finished;
        public string Reason = "";
    }
    public sealed class SupervisorPolicy
    {
        public const long DefaultDesktopWaitMilliseconds = WinBootVideo.Shared.StartupLimits.InputDesktopWaitMilliseconds;
        private readonly long startupLimit;
        private readonly long totalLimit;
        private readonly long desktopWaitLimit;
        private readonly long coldStartLimit;
        private bool desktopRequested;
        private bool overlaySeen;
        private bool finished;
        private long overlayAt = -1;
        private long interactiveAt = -1;
        private long playbackAt = -1;
        public SupervisorPolicy(long startupMilliseconds, long totalMilliseconds, long desktopWaitMilliseconds = DefaultDesktopWaitMilliseconds, long coldStartMilliseconds = WinBootVideo.Shared.StartupLimits.ColdStartMilliseconds)
        {
            if (startupMilliseconds <= 0 || totalMilliseconds <= startupMilliseconds || desktopWaitMilliseconds <= 0 || coldStartMilliseconds <= 0)
                throw new System.ArgumentOutOfRangeException("startupMilliseconds");
            startupLimit = startupMilliseconds; totalLimit = totalMilliseconds; desktopWaitLimit = desktopWaitMilliseconds;
            coldStartLimit = coldStartMilliseconds;
        }
        public SupervisorDecision Tick(long elapsed, bool overlayReady, bool playbackStarted, bool inputDesktopActive, bool exited, bool firstFramePresented = false)
        {
            var action = new SupervisorDecision();
            if (finished) { action.Finished = true; return action; }
            if (overlayReady && !overlaySeen) { overlaySeen = true; overlayAt = elapsed; }
            if (inputDesktopActive)
            {
                if (interactiveAt < 0) interactiveAt = elapsed;
                if (overlaySeen && playbackStarted && playbackAt < 0) playbackAt = elapsed;
            }
            else if (playbackAt < 0) interactiveAt = -1;
            if (exited) { action.Finished = true; action.Reason = "player-exited"; }
            else if (!overlaySeen && interactiveAt >= 0 && elapsed - interactiveAt >= coldStartLimit) Stop(action, "cold-start-timeout");
            else if (playbackAt >= 0 && !inputDesktopActive) Stop(action, "input-desktop-left");
            else if (playbackAt >= 0 && elapsed - playbackAt >= totalLimit) Stop(action, "total-timeout");
            else if (!firstFramePresented && overlaySeen && interactiveAt >= 0 && elapsed - System.Math.Max(interactiveAt, overlayAt) >= startupLimit) Stop(action, "playback-handshake-timeout");
            else if (playbackAt < 0 && elapsed >= desktopWaitLimit) Stop(action, "input-desktop-wait-timeout");
            // A rendered black window is not a visible video frame. Explorer is
            // released only after presentation, or when the animation is abandoned.
            if ((overlaySeen && playbackAt >= 0 && inputDesktopActive && firstFramePresented) || action.Finished)
            {
                action.StartDesktop = !desktopRequested;
                desktopRequested = true;
            }
            finished = action.Finished;
            return action;
        }
        private static void Stop(SupervisorDecision action, string reason)
        {
            action.KillPlayer = true; action.Finished = true; action.Reason = reason;
        }
    }
}
