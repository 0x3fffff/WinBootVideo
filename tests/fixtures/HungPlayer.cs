using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class HungPlayer
{
    private static void Main(string[] args)
    {
        string pidPath = Environment.GetEnvironmentVariable("WINBOOTVIDEO_FIXTURE_PID");
        if (pidPath != null) File.WriteAllText(pidPath, Process.GetCurrentProcess().Id.ToString());
        if (Environment.GetEnvironmentVariable("WINBOOTVIDEO_FIXTURE_READY") == "1")
        {
            int index = Array.IndexOf(args, "--token");
            using (var ready = EventWaitHandle.OpenExisting(@"Local\WinBootVideo.Ready." + args[index + 1])) ready.Set();
            using (var playback = EventWaitHandle.OpenExisting(@"Local\WinBootVideo.Playback." + args[index + 1])) playback.Set();
            // A synthetic handshake only: this fixture tests the independent
            // watchdog, while actual presentation is verified with real WPF.
            if (Environment.GetEnvironmentVariable("WINBOOTVIDEO_FIXTURE_FRAME") == "1")
                using (var frame = EventWaitHandle.OpenExisting(@"Local\WinBootVideo.FirstFrame." + args[index + 1])) frame.Set();
        }
        Thread.Sleep(Timeout.Infinite);
    }
}
