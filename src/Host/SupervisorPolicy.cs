namespace WinBootVideo.Host
{
    // Pure state machine; time is measured outside the WPF dispatcher.
    public sealed class SupervisorDecision
    {
        public bool StartDesktop;
        public bool KillPlayer;
        public bool Finished;
        public string Reason = "";
    }
    public sealed class SupervisorPolicy
    {
        private readonly long startupLimit;
        private readonly long totalLimit;
        private bool desktopRequested;
        private bool overlaySeen;
        private bool finished;
        public SupervisorPolicy(long startupMilliseconds, long totalMilliseconds)
        {
            if (startupMilliseconds <= 0 || totalMilliseconds <= startupMilliseconds) throw new System.ArgumentOutOfRangeException("startupMilliseconds");
            startupLimit = startupMilliseconds; totalLimit = totalMilliseconds;
        }
        public SupervisorDecision Tick(long elapsed, bool overlayReady, bool exited)
        {
            var action = new SupervisorDecision();
            if (finished) { action.Finished = true; return action; }
            overlaySeen = overlaySeen || overlayReady;
            if (overlaySeen || exited || elapsed >= startupLimit)
            {
                action.StartDesktop = !desktopRequested;
                desktopRequested = true;
            }
            if (exited) { action.Finished = true; action.Reason = "player-exited"; }
            else if (!overlaySeen && elapsed >= startupLimit) { action.KillPlayer = true; action.Finished = true; action.Reason = "startup-timeout"; }
            else if (elapsed >= totalLimit) { action.KillPlayer = true; action.Finished = true; action.Reason = "total-timeout"; }
            finished = action.Finished;
            return action;
        }
    }
}
