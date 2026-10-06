using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;
using WinBootVideo.Core;

namespace WinBootVideo;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (Arguments.Has(e.Args, "--boot-logo-helper"))
        {
            int code = 30;
            try
            {
                if (e.Args.Length != 3 || e.Args[0] != "--boot-logo-helper") throw new InvalidDataException("Logo 管理员参数无效。");
                string input = Path.GetFullPath(e.Args[1]), output = Path.GetFullPath(e.Args[2]);
                string directory = Path.GetDirectoryName(input)!;
                if (input.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetFileName(input) != "request.json" || Path.GetFileName(output) != "response.json" ||
                    Path.GetDirectoryName(output) != directory || !Directory.Exists(directory) || File.Exists(output))
                    throw new InvalidDataException("Logo 请求目录或报告文件不安全。");
                BootLogoPaths.OrdinaryDirectory(directory);
                if ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0 || new FileInfo(input).Length > 16384) throw new InvalidDataException("Logo 请求无效。");
                BootLogoResponse result;
                try
                {
                    var request = JsonSerializer.Deserialize<BootLogoRequest>(File.ReadAllText(input), Settings.JsonOptions) ?? throw new InvalidDataException("Logo 请求为空。");
                    result = await BootLogoInstaller.ExecuteAsync(request); code = result.Success ? 0 : 30;
                }
                catch (Exception ex) { result = new BootLogoResponse { Success = false, Message = ex.Message }; }
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, result, Settings.JsonOptions);
            }
            catch { /* Malformed helper requests never create files elsewhere or open a dialog. */ }
            Shutdown(code); return;
        }
        var paths = new AppPaths(Arguments.Value(e.Args, "--data-dir"));
        DispatcherUnhandledException += (_, exception) =>
        {
            Log(paths, exception.Exception);
            exception.Handled = true;
            if (!Arguments.Has(e.Args, "--player") && !Arguments.Has(e.Args, "--repair-startup")) MessageBox.Show(exception.Exception.Message, "WinBootVideo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(30);
        };
        try
        {
            if (Arguments.Has(e.Args, "--repair-startup"))
            {
                var saved = new SettingsStore(paths).Load();
                saved.Validate();
                if (saved.VideoSha256.Length != 64 || saved.VerifiedVideoSha256 != saved.VideoSha256 ||
                    await SettingsStore.HashAsync(saved.VideoPath, CancellationToken.None) != saved.VideoSha256)
                    throw new InvalidDataException("修复启动入口前须已有成功预览且未变化的视频。");
                string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法识别当前用户。");
                var startup = new LoginStartupController(paths);
                if (!startup.HasOwnedStartup(sid)) throw new InvalidOperationException("此命令仅修复已有登录动画；首次启用请使用 GUI。");
                startup.EnableStrict(sid);
                string? report = Arguments.Value(e.Args, "--startup-report");
                if (report != null) AtomicJson.Write(report, new { version = "0.2.5", enabled = startup.IsStrictEnabled(sid), strictBeforeDesktop = true });
                Shutdown(startup.IsStrictEnabled(sid) ? 0 : 30);
                return;
            }
            if (Arguments.Has(e.Args, "--player"))
            {
                LogPhase(paths, "app-started");
                var player = new PlayerWindow(e.Args, paths);
                MainWindow = player;
                player.Show();
                LogPhase(paths, "window-show-returned");
                return;
            }
            if (Arguments.Value(e.Args, "--render-logo-ui") is string logoRender)
            {
                var logoWindow = new BootLogoWindow(paths);
                if (Arguments.Value(e.Args, "--logo-image") is string logoImage) logoWindow.SelectImage(logoImage);
                if (Arguments.Has(e.Args, "--logo-scroll-bottom")) ((System.Windows.Controls.ScrollViewer)logoWindow.FindName("LogoScroll")).ScrollToBottom();
                RenderWindow(logoWindow, logoRender, 1000, 740); Shutdown(); return;
            }
            var window = new MainWindow(paths);
            string? initialVideo = Arguments.Value(e.Args, "--video");
            if (initialVideo != null) window.SelectVideo(initialVideo);
            MainWindow = window;
            string? renderPath = Arguments.Value(e.Args, "--render-ui");
            if (renderPath != null)
            {
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(1080, 780));
                root.Arrange(new Rect(0, 0, 1080, 780));
                root.UpdateLayout();
                Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var bitmap = new RenderTargetBitmap(1080, 780, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(renderPath))!);
                using (var output = File.Create(renderPath)) encoder.Save(output);
                Shutdown();
                return;
            }
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception ex)
        {
            Log(paths, ex);
            if (!Arguments.Has(e.Args, "--player") && !Arguments.Has(e.Args, "--repair-startup")) MessageBox.Show(ex.Message, "WinBootVideo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(30);
        }
    }
    private void RenderWindow(Window window, string outputPath, int width, int height)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
        bitmap.Render(background); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var output = File.Create(outputPath); encoder.Save(output);
    }
    public static void Log(AppPaths paths, Exception exception)
    {
        try { Directory.CreateDirectory(paths.LogDirectory); File.AppendAllText(Path.Combine(paths.LogDirectory, "player.log"), DateTime.Now.ToString("s") + " " + exception + Environment.NewLine); } catch { }
    }
    private static void LogPhase(AppPaths paths, string phase)
    {
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            File.AppendAllText(Path.Combine(paths.LogDirectory, "playback-timing.log"), DateTime.Now.ToString("s") + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " startup-phase=" + phase + Environment.NewLine);
        }
        catch { }
    }
}
public static class Arguments
{
    public static bool Has(string[] args, string name) => Array.IndexOf(args, name) >= 0;
    public static string? Value(string[] args, string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
}
