using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using WinBootVideo.Core;

internal static class VisibilityGateChecks
{
    public static async Task RunIsolated(string twoSecondVideo, Action<bool, string> check)
    {
        // Each process gets exactly one WPF Application, independently of GUI checks.
        string runner = Environment.ProcessPath ?? throw new InvalidOperationException("No test runner path.");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(runner), "dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.AddRange(new[] { "--visibility-gate-only", twoSecondVideo });
        var result = await MediaTools.RunAsync(runner, arguments, TimeSpan.FromSeconds(12), CancellationToken.None);
        Console.WriteLine(result.Output.Trim());
        check(result.ExitCode == 0, "实际播放器先等待六秒、随后从零完整播放两秒视频。 " + MediaTools.Tail(result.Error));
    }

    public static async Task Run(string twoSecondVideo, Action<bool, string> check)
    {
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-visibility-gate-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(work);
        var settings = new Settings
        {
            VideoPath = Path.GetFullPath(twoSecondVideo),
            VideoSha256 = await SettingsStore.HashAsync(twoSecondVideo, CancellationToken.None),
            Volume = 0,
            FadeOutMilliseconds = 100,
            MaxDurationSeconds = 5,
            StartupTimeoutSeconds = 2,
            CoverOtherScreens = false
        };
        new SettingsStore(paths).Write(settings);
        string receipt = Path.Combine(work, "receipt.json");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            WinBootVideo.App? app = null;
            try
            {
                app = new WinBootVideo.App(); app.InitializeComponent();
                app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                bool inputDesktopActive = false;
                bool releaseChecked = false;
                bool rendered = false;
                var playback = new Stopwatch();
                var window = new WinBootVideo.PlayerWindow(
                    new[] { "--player", "--preview", "--windowed", "--config", paths.SettingsFile, "--receipt", receipt },
                    paths, () => inputDesktopActive);
                app.MainWindow = window;
                var media = (MediaElement)window.Content;
                media.MediaOpened += (_, _) =>
                {
                    try { check(releaseChecked && media.Position.TotalMilliseconds < 100, "输入桌面确认后首次解码从视频开头开始"); }
                    catch (Exception ex) { completion.TrySetException(ex); app.Shutdown(30); }
                };
                window.ContentRendered += async (_, _) =>
                {
                    if (rendered) return;
                    rendered = true;
                    try
                    {
                        await Task.Delay(6000);
                        check(window.IsVisible && media.Source == null && media.Position.TotalMilliseconds < 1,
                            "输入桌面未确认的六秒内未加载、未推进媒体，且不消耗五秒播放预算");
                        releaseChecked = true;
                        playback.Start();
                        inputDesktopActive = true;
                    }
                    catch (Exception ex) { completion.TrySetException(ex); app.Shutdown(30); }
                };
                window.Closed += (_, _) =>
                {
                    try
                    {
                        check(releaseChecked, "实际播放器等到可见性探针放行后才结束");
                        var report = JsonSerializer.Deserialize<WinBootVideo.PreviewReceipt>(File.ReadAllText(receipt), Settings.JsonOptions)!;
                        check(report.Succeeded && report.Opened && report.PlaybackAdvanced && report.InputDesktopConfirmed,
                            "可见后的真实解码与播放完成生成成功收据");
                        check(report.WaitingForDesktopMilliseconds >= 5900 && report.PlaybackStartPositionMilliseconds < 1,
                            "收据记录等待阶段与零毫秒播放起点");
                        check(playback.Elapsed.TotalSeconds >= 1.7 && playback.Elapsed.TotalSeconds < 5,
                            "放行后仍播放完整两秒素材，而非只显示结尾");
                        completion.TrySetResult();
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                };
                window.Show();
                Dispatcher.Run();
            }
            catch (Exception ex) { completion.TrySetException(ex); app?.Shutdown(30); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(11)); }
        finally
        {
            thread.Join(2000);
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
}
