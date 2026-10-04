using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using WinBootVideo.Core;

internal static class GuiFlowChecks
{
    public static async Task Run(string bundle, Action<bool, string> check, string? videoPath = null)
    {
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-gui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(work);
        var store = new SettingsStore(paths);
        var draft = new Settings { VideoPath = Path.GetFullPath(videoPath ?? Path.Combine(bundle, "demo-login.mp4")), Volume = videoPath == null ? .71 : 0, FadeOutMilliseconds = 100, MaxDurationSeconds = 15, StartupTimeoutSeconds = 5, CoverOtherScreens = false };
        store.Write(draft);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            WinBootVideo.App? app = null;
            try
            {
                app = new WinBootVideo.App(); app.InitializeComponent();
                app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                var registry = new FakeRegistry();
                var window = new WinBootVideo.MainWindow(paths, registry, Path.Combine(bundle, "WinBootVideo.Host.exe"), previewWindowed: true)
                {
                    ShowInTaskbar = false, ShowActivated = false,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -10000, Top = -10000
                };
                bool started = false;
                window.ContentRendered += async (_, _) =>
                {
                    if (started) return;
                    started = true;
                    try
                    {
                        var preview = (Button)window.FindName("PreviewButton");
                        var enable = (Button)window.FindName("EnableButton");
                        var status = (TextBlock)window.FindName("StatusText");
                        check(!enable.IsEnabled, "GUI 未预览时保持启用按钮不可用");
                        preview.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
                        var deadline = Stopwatch.StartNew();
                        while (!preview.IsEnabled && deadline.Elapsed.TotalSeconds < 20) await Task.Delay(50);
                        var saved = store.Load();
                        check(enable.IsEnabled && saved.VideoSha256.Length == 64 && saved.VerifiedVideoSha256 == saved.VideoSha256,
                            "GUI 保存预览的实际 Click 流程成功并解锁启用按钮；状态：" + status.Text);
                        check(saved.Volume == draft.Volume && saved.FadeOutMilliseconds == draft.FadeOutMilliseconds && saved.CoverOtherScreens == draft.CoverOtherScreens && saved.MaxDurationSeconds == draft.MaxDurationSeconds,
                            "GUI 导入视频与确认预览后保留用户设置");
                        check(!registry.Current.Exists, "GUI 预览不写入登录 Shell");
                        completed.TrySetResult();
                    }
                    catch (Exception ex) { completed.TrySetException(ex); }
                    finally { window.Close(); app.Shutdown(); }
                };
                window.Show();
                Dispatcher.Run();
            }
            catch (Exception ex) { completed.TrySetException(ex); app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            thread.Join(3000);
            if (File.Exists(paths.SettingsFile)) Directory.Delete(work, true);
        }
    }
}
