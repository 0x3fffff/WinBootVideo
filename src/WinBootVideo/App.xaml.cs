using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinBootVideo.Core;

namespace WinBootVideo;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var paths = new AppPaths(Arguments.Value(e.Args, "--data-dir"));
        DispatcherUnhandledException += (_, exception) =>
        {
            Log(paths, exception.Exception);
            exception.Handled = true;
            if (!Arguments.Has(e.Args, "--player")) MessageBox.Show(exception.Exception.Message, "WinBootVideo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(30);
        };
        try
        {
            if (Arguments.Has(e.Args, "--player"))
            {
                LogPhase(paths, "app-started");
                var player = new PlayerWindow(e.Args, paths);
                MainWindow = player;
                player.Show();
                LogPhase(paths, "window-show-returned");
                return;
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
            if (!Arguments.Has(e.Args, "--player")) MessageBox.Show(ex.Message, "WinBootVideo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(30);
        }
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
