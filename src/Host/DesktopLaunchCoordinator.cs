using System;

namespace WinBootVideo.Host
{
    // Both the watchdog and normal exit path use this same launch boundary.
    public sealed class DesktopLaunchCoordinator
    {
        private readonly Func<bool> desktopExists;
        private readonly Action launch;
        private readonly Func<long> elapsedMilliseconds;
        private readonly long retryGraceMilliseconds;
        private readonly int maximumAttempts;
        private readonly object gate = new object();
        private bool requested;
        private int attempts;
        private long lastAttempt = -1;
        public DesktopLaunchCoordinator(Func<bool> desktopExists, Action launch, Func<long> elapsedMilliseconds, long retryGraceMilliseconds = 15000, int maximumAttempts = 2)
        {
            if (desktopExists == null || launch == null || elapsedMilliseconds == null) throw new ArgumentNullException();
            if (retryGraceMilliseconds < 0 || maximumAttempts < 1) throw new ArgumentOutOfRangeException();
            this.desktopExists = desktopExists;
            this.launch = launch; this.elapsedMilliseconds = elapsedMilliseconds;
            this.retryGraceMilliseconds = retryGraceMilliseconds;
            this.maximumAttempts = maximumAttempts;
        }
        public bool Ensure()
        {
            lock (gate)
            {
                // Explorer can hand off to another process and exit before the
                // desktop HWND exists. A successful request is still complete.
                if (desktopExists() || requested) return false;
                long now = elapsedMilliseconds();
                if (attempts >= maximumAttempts || (lastAttempt >= 0 && now - lastAttempt < retryGraceMilliseconds)) return false;
                attempts++; lastAttempt = now;
                launch(); // Only an explicit failure leaves a retry available.
                requested = true;
                return true;
            }
        }
    }
}
