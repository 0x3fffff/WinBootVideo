using System;
using System.IO;
using System.Threading;

namespace WinBootVideo.Host
{
    public enum FallbackStartupResult { Ready, Disabled, TimedOut }

    public static class FallbackStartupGate
    {
        public static FallbackStartupResult WaitUntilDesktop(Func<bool> enabled, Func<bool> desktopExists, Func<long> elapsed, Action delay, Action<string> log, long maximumWaitMilliseconds = 30000)
        {
            if (enabled == null) throw new ArgumentNullException("enabled");
            if (desktopExists == null) throw new ArgumentNullException("desktopExists");
            if (elapsed == null) throw new ArgumentNullException("elapsed");
            if (delay == null) throw new ArgumentNullException("delay");
            if (log == null) throw new ArgumentNullException("log");
            if (maximumWaitMilliseconds <= 0) throw new ArgumentOutOfRangeException("maximumWaitMilliseconds");
            if (!enabled()) { log("task-disabled-or-foreign-shell"); return FallbackStartupResult.Disabled; }
            long began = elapsed();
            log("task-desktop-wait");
            while (!desktopExists())
            {
                if (!enabled()) { log("task-disabled-or-foreign-shell-after-wait"); return FallbackStartupResult.Disabled; }
                if (elapsed() - began >= maximumWaitMilliseconds) { log("task-desktop-wait-timeout"); return FallbackStartupResult.TimedOut; }
                delay();
            }
            if (!enabled()) { log("task-disabled-or-foreign-shell-after-wait"); return FallbackStartupResult.Disabled; }
            log("task-desktop-present waited-ms=" + (elapsed() - began));
            return FallbackStartupResult.Ready;
        }
    }

    public static class LoginAttemptGate
    {
        public static int Run(string session, string stampPath, Func<int> run, Action alreadyAttempted, Action<string> log)
        {
            if (string.IsNullOrEmpty(session)) throw new ArgumentException("A logon session identity is required.", "session");
            if (run == null) throw new ArgumentNullException("run");
            if (alreadyAttempted == null) throw new ArgumentNullException("alreadyAttempted");
            if (log == null) throw new ArgumentNullException("log");
            bool created;
            using (var mutex = new Mutex(true, @"Local\WinBootVideo.Host." + session, out created))
            {
                if (!created)
                {
                    log("duplicate-mutex");
                    return 0;
                }
                try
                {
                    if (File.Exists(stampPath) && File.ReadAllText(stampPath) == session)
                    {
                        log("same-session-skipped");
                        alreadyAttempted();
                        return 0;
                    }
                    var directory = Path.GetDirectoryName(Path.GetFullPath(stampPath));
                    if (directory == null) throw new ArgumentException("The stamp must be a file path.", "stampPath");
                    Directory.CreateDirectory(directory);
                    // A failed attempt still counts: the other login entry must not replay it.
                    File.WriteAllText(stampPath, session);
                    return run();
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
