using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using WinBootVideo.Core;

internal static class GuiFlowChecks
{
    public static async Task Run(string bundle, Action<bool, string> check, string? videoPath = null, bool fullscreen = false)
    {
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-gui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(work);
        var store = new SettingsStore(paths);
        var draft = new Settings { VideoPath = Path.GetFullPath(videoPath ?? Path.Combine(bundle, "demo-login.mp4")), Volume = fullscreen || videoPath != null ? 0 : .71, FadeOutMilliseconds = 100, MaxDurationSeconds = 15, StartupTimeoutSeconds = 5, CoverOtherScreens = fullscreen };
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
                var window = new WinBootVideo.MainWindow(paths, registry, Path.Combine(bundle, "WinBootVideo.Host.exe"), previewWindowed: !fullscreen)
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
                        if (fullscreen)
                        {
                            // The successful production GUI flow has already checked the
                            // actual playback receipt and matching video SHA. Ready is only
                            // signalled after the native screen coverage checks pass.
                            string timing = File.ReadAllText(Path.Combine(paths.LogDirectory, "playback-timing.log"));
                            int secondary = timing.IndexOf("secondary-content-rendered", StringComparison.Ordinal);
                            int ready = timing.IndexOf("overlay-rendered mode=preview", StringComparison.Ordinal);
                            int playback = timing.IndexOf("playback-start input-confirmed=true position-ms=0", StringComparison.Ordinal);
                            int opened = timing.IndexOf("media-opened", StringComparison.Ordinal);
                            check(secondary >= 0, "GUI 真正全屏预览经过真实副屏遮罩 ContentRendered，未绕过多屏路径");
                            check(ready > secondary, "GUI 副屏遮罩渲染及 HWND 物理覆盖检查成功后完成 Ready 握手");
                            check(playback > ready && opened > playback, "GUI 真实 Host 与 WPF 播放器完成可见桌面确认、从零开始及视频解码");
                            check(timing.Contains("playback-exit code=0", StringComparison.Ordinal), "GUI 真正全屏视频播放完整结束，未靠 Esc 跳过通过验证");
                        }
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
