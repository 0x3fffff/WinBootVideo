using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class DeferredReadyPlayer
{
    private static int Main(string[] args)
    {
        string data = null;
        try
        {
            int dataIndex = Array.IndexOf(args, "--data-dir");
            data = args[dataIndex + 1];
            File.WriteAllText(Path.Combine(data, "deferred-player-pid.txt"), Process.GetCurrentProcess().Id.ToString());
            // A real child process exists, but has not rendered its overlay yet.
            // This intentionally crosses the Host's two-second initialization limit.
            Thread.Sleep(10000);
            int tokenIndex = Array.IndexOf(args, "--token");
            using (var ready = EventWaitHandle.OpenExisting(@"Local\WinBootVideo.Ready." + args[tokenIndex + 1])) ready.Set();
            File.WriteAllText(Path.Combine(data, "deferred-player-ready.txt"), "ready");
            Thread.Sleep(1000);
            return 0;
        }
        catch (Exception ex)
        {
            try { if (data != null) File.WriteAllText(Path.Combine(data, "deferred-player-failure.txt"), ex.ToString()); } catch { }
            return 30;
        }
    }
}
