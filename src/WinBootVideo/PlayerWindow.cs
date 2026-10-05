using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinBootVideo.Core;
using WinBootVideo.Shared;
using Forms = System.Windows.Forms;

namespace WinBootVideo;

public sealed class PreviewReceipt
{
    public bool Opened { get; set; }
    public bool PlaybackAdvanced { get; set; }
    public bool Succeeded { get; set; }
    public bool ScreenCoverageVerified { get; set; }
    public bool InputDesktopConfirmed { get; set; }
    public long WaitingForDesktopMilliseconds { get; set; }
    public double PlaybackStartPositionMilliseconds { get; set; }
    public string VideoSha256 { get; set; } = "";
    public string Message { get; set; } = "";
}
public sealed class PlayerWindow : Window
{
    private readonly string[] args;
    private readonly AppPaths paths;
    private readonly Settings settings;
    private readonly MediaElement media;
    private readonly List<Window> masks = new();
    private readonly DispatcherTimer timer;
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private readonly Func<bool>? visibilityProbe;
    private long playbackStartedAt = -1;
    private long interactiveSince = -1;
    private long coldInteractiveSince = -1;
    private volatile bool overlayReady;
    private string lastDesktopReason = "";
    private const long DesktopWaitMilliseconds = StartupLimits.InputDesktopWaitMilliseconds;
    private readonly PreviewReceipt receipt = new();
    private bool finishing;
    private bool begun;
    private volatile bool stopped;
    private readonly bool windowed;
    public PlayerWindow(string[] args, AppPaths paths, Func<bool>? visibilityProbe = null)
    {
        this.args = args; this.paths = paths;
        this.visibilityProbe = visibilityProbe;
        LogTiming("constructor-enter");
        settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Arguments.Value(args, "--config") ?? paths.SettingsFile), Settings.JsonOptions) ?? throw new InvalidDataException("配置为空。");
        settings.Validate(); receipt.VideoSha256 = settings.VideoSha256;
        LogTiming("settings-loaded");
        windowed = Arguments.Has(args, "--windowed");
        Title = "WinBootVideo · Esc / 空格跳过";
        Background = Brushes.Black;
        WindowStyle = windowed ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = windowed; Topmost = !windowed;
        Width = 800; Height = 450;
        media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform, Volume = settings.Volume, ScrubbingEnabled = true };
        Content = media;
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.ScreenDevice) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        if (!windowed) PositionOnScreen(this, screen);
        SourceInitialized += (_, _) => LogTiming("window-handle-ready");
        Loaded += (_, _) => LogTiming("window-loaded");
        ContentRendered += (_, _) => { LogTiming("window-content-rendered"); BeginPlayback(screen); };
        media.MediaOpened += (_, _) =>
        {
            if (media.NaturalVideoWidth <= 0 || media.NaturalVideoHeight <= 0) { _ = FinishAsync(30, "文件没有可解码的视频轨道。"); return; }
            receipt.Opened = true;
            LogTiming("media-opened position-ms=" + media.Position.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
        };
        media.MediaFailed += (_, failure) => { App.Log(paths, failure.ErrorException); _ = FinishAsync(30, "解码失败：" + failure.ErrorException.Message); };
        media.MediaEnded += (_, _) => { receipt.PlaybackAdvanced |= media.Position.TotalMilliseconds > 100; _ = FinishAsync(0, "播放完成。"); };
        PreviewKeyDown += (_, key) => { if (key.Key is Key.Escape or Key.Space) { key.Handled = true; _ = FinishAsync(2, "用户跳过。"); } };
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (finishing) return;
            bool canPresent = CanPresent(out string reason);
            if (!overlayReady)
            {
                if (!canPresent) coldInteractiveSince = -1;
                else
                {
                    if (coldInteractiveSince < 0) coldInteractiveSince = lifetime.ElapsedMilliseconds;
                    if (lifetime.ElapsedMilliseconds - coldInteractiveSince >= StartupLimits.ColdStartMilliseconds)
                        _ = FinishAsync(31, "桌面可见后播放器仍未在 30 秒内启动，跳过本次动画。");
                }
                if (lifetime.ElapsedMilliseconds >= DesktopWaitMilliseconds) _ = FinishAsync(31, "等待登录桌面超过 15 分钟，跳过本次动画。");
                return;
            }
            if (Interlocked.Read(ref playbackStartedAt) < 0)
            {
                if (reason != lastDesktopReason)
                {
                    lastDesktopReason = reason;
                    LogTiming("waiting-for-input-desktop " + reason + " position-ms=0");
                }
                if (!canPresent) interactiveSince = -1;
                else
                {
                    if (interactiveSince < 0) interactiveSince = lifetime.ElapsedMilliseconds;
                    if (lifetime.ElapsedMilliseconds - interactiveSince >= 300) StartVisiblePlayback();
                }
                if (Interlocked.Read(ref playbackStartedAt) < 0 && lifetime.ElapsedMilliseconds >= DesktopWaitMilliseconds + settings.StartupTimeoutSeconds * 1000L)
                    _ = FinishAsync(31, "等待登录桌面超过 15 分钟，跳过本次动画。");
                return;
            }
            if (!canPresent) { _ = FinishAsync(31, "播放期间离开登录桌面，跳过本次动画。"); return; }
            receipt.PlaybackAdvanced |= receipt.Opened && media.Position.TotalMilliseconds > 100;
            if (!receipt.Opened && PlaybackElapsedSeconds >= settings.StartupTimeoutSeconds) _ = FinishAsync(31, "视频加载超时。");
            if (PlaybackElapsedSeconds >= settings.MaxDurationSeconds - settings.FadeOutMilliseconds / 1000.0 - 0.4) _ = FinishAsync(31, "达到播放等待上限。请剪短视频。");
        };
        Closing += (_, e) => { if (!stopped) { e.Cancel = true; _ = FinishAsync(2, "用户关闭预览。"); } };
        timer.Start();
        LogTiming("constructor-complete screens=" + Forms.Screen.AllScreens.Length);
        StartGuardian();
    }
    private async void BeginPlayback(Forms.Screen selected)
    {
        if (begun || finishing) return;
        begun = true;
        var rendered = new List<Task>();
        if (!windowed && settings.CoverOtherScreens)
        {
            foreach (var screen in Forms.Screen.AllScreens.Where(s => s.DeviceName != selected.DeviceName))
            {
                var mask = new Window { Background = Brushes.Black, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false };
                var ready = new TaskCompletionSource();
                mask.ContentRendered += (_, _) => { LogTiming("secondary-content-rendered"); ready.TrySetResult(); };
                rendered.Add(ready.Task);
                mask.Closing += (_, e) => { if (!stopped) { e.Cancel = true; _ = FinishAsync(2, "用户关闭预览。"); } };
                mask.PreviewKeyDown += (_, key) => { if (key.Key is Key.Escape or Key.Space) _ = FinishAsync(2, "用户跳过。"); };
                PositionOnScreen(mask, screen); masks.Add(mask); mask.Show();
            }
        }
        await Task.WhenAll(rendered);
        if (finishing) return;
        if (!windowed)
        {
            receipt.ScreenCoverageVerified = Covers(this, selected);
            if (settings.CoverOtherScreens)
                receipt.ScreenCoverageVerified &= masks.Zip(Forms.Screen.AllScreens.Where(s => s.DeviceName != selected.DeviceName)).All(pair => Covers(pair.First, pair.Second));
            if (!receipt.ScreenCoverageVerified) { await FinishAsync(30, "无法完整覆盖所选屏幕，请检查显示设置。"); return; }
        }
        // Signal the mask first so Explorer can prepare behind it and Winlogon can
        // leave its desktop. Rendering here does not establish user visibility.
        overlayReady = true;
        SignalHost("Ready");
        LogTiming("overlay-rendered mode=" + (Arguments.Has(args, "--preview") ? "preview" : "login"));
        timer.Start();
    }
    private bool CanPresent(out string reason)
    {
        if (visibilityProbe != null) { bool result = visibilityProbe(); reason = result ? "test-input-active" : "test-input-inactive"; return result; }
        var state = InputDesktopState.Read(); reason = state.Reason; return state.CanPresent;
    }
    private double PlaybackElapsedSeconds
    {
        get
        {
            long start = Interlocked.Read(ref playbackStartedAt);
            return start < 0 ? 0 : (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
        }
    }
    private void StartVisiblePlayback()
    {
        if (finishing || Interlocked.Read(ref playbackStartedAt) >= 0) return;
        Activate(); Focus();
        if (!CanPresent(out _)) { interactiveSince = -1; return; }
        receipt.InputDesktopConfirmed = true;
        receipt.WaitingForDesktopMilliseconds = lifetime.ElapsedMilliseconds;
        media.Source = new Uri(settings.VideoPath, UriKind.Absolute);
        media.Position = TimeSpan.Zero;
        receipt.PlaybackStartPositionMilliseconds = media.Position.TotalMilliseconds;
        Interlocked.Exchange(ref playbackStartedAt, Stopwatch.GetTimestamp());
        LogTiming("playback-start input-confirmed=true position-ms=" + receipt.PlaybackStartPositionMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
        SignalHost("Playback");
        media.Play();
    }
    private void SignalHost(string phase)
    {
        string? token = Arguments.Value(args, "--token");
        if (token == null) return;
        using var ready = EventWaitHandle.OpenExisting(@"Local\WinBootVideo." + phase + "." + token);
        ready.Set();
    }
    private void LogTiming(string message)
    {
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            File.AppendAllText(Path.Combine(paths.LogDirectory, "playback-timing.log"), DateTime.Now.ToString("s") + " pid=" + Process.GetCurrentProcess().Id + " elapsed-ms=" + lifetime.ElapsedMilliseconds + " " + message + Environment.NewLine);
        }
        catch { }
    }
    private async Task FinishAsync(int exitCode, string message)
    {
        if (finishing) return;
        finishing = true; timer.Stop();
        receipt.PlaybackAdvanced |= receipt.Opened && media.Position.TotalMilliseconds > 100;
        LogTiming("playback-exit code=" + exitCode + " position-ms=" + media.Position.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " reason=" + message);
        try
        {
            media.Pause(); // Hold the last decoded frame during the transition.
            if (!Arguments.Has(args, "--preview") && exitCode is 0 or 2)
            {
                var wait = Stopwatch.StartNew();
                while (GetShellWindow() == IntPtr.Zero && wait.Elapsed.TotalSeconds < 2 && PlaybackElapsedSeconds < settings.MaxDurationSeconds - 1) await Task.Delay(80);
            }
            if (exitCode is 0 or 2 && settings.FadeOutMilliseconds > 0)
            {
                var duration = TimeSpan.FromMilliseconds(settings.FadeOutMilliseconds);
                var fade = new DoubleAnimation(0, new Duration(duration));
                foreach (var mask in masks) mask.BeginAnimation(OpacityProperty, fade);
                BeginAnimation(OpacityProperty, fade);
                media.BeginAnimation(MediaElement.VolumeProperty, new DoubleAnimation(0, new Duration(duration)));
                await Task.Delay(duration);
            }
            receipt.Succeeded = (exitCode is 0 or 2) && receipt.Opened && receipt.PlaybackAdvanced;
            receipt.Message = receipt.Succeeded ? message : message + " 未确认有效视频播放。";
            string? report = Arguments.Value(args, "--receipt");
            if (report != null) AtomicJson.Write(report, receipt);
        }
        catch (Exception ex) { App.Log(paths, ex); exitCode = 30; }
        finally
        {
            stopped = true;
            try { media.Close(); } catch (Exception ex) { App.Log(paths, ex); }
            foreach (var mask in masks) mask.Close();
            Close(); Application.Current.Shutdown(exitCode);
        }
    }
    private void StartGuardian()
    {
        string? parentText = Arguments.Value(args, "--parent");
        int parentId = int.TryParse(parentText, out int id) ? id : 0;
        var thread = new Thread(() =>
        {
            Process? parent = null;
            long coldVisibleAt = -1;
            try
            {
                if (parentId > 0) parent = Process.GetProcessById(parentId);
                while (!stopped)
                {
                    if (!overlayReady)
                    {
                        bool active = InputDesktopState.ReadInputDesktop().CanPresent;
                        if (!active) coldVisibleAt = -1;
                        else if (coldVisibleAt < 0) coldVisibleAt = lifetime.ElapsedMilliseconds;
                    }
                    bool expired = !overlayReady
                        ? (coldVisibleAt >= 0 && lifetime.ElapsedMilliseconds - coldVisibleAt > StartupLimits.ColdStartMilliseconds + 2000) || lifetime.ElapsedMilliseconds > DesktopWaitMilliseconds + 2000
                        : Interlocked.Read(ref playbackStartedAt) < 0
                            ? lifetime.ElapsedMilliseconds > DesktopWaitMilliseconds + settings.StartupTimeoutSeconds * 1000L + 2000
                            : PlaybackElapsedSeconds > settings.MaxDurationSeconds + 2;
                    if ((parent != null && parent.HasExited) || expired) Environment.Exit(31);
                    Thread.Sleep(200);
                }
            }
            catch { if (!stopped) Environment.Exit(31); }
            finally { parent?.Dispose(); }
        }) { IsBackground = true, Name = "Playback safety deadline" };
        thread.Start();
    }
    private static void PositionOnScreen(Window window, Forms.Screen screen)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        // HWND coordinates are physical pixels, including negative monitor origins.
        void Place()
        {
            var rect = screen.Bounds;
            if (!SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), rect.X, rect.Y, rect.Width, rect.Height, 0x0040)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        window.SourceInitialized += (_, _) => Place();
        window.Loaded += (_, _) => Place();
    }
    private static bool Covers(Window window, Forms.Screen screen)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) return false;
        var bounds = screen.Bounds;
        return rect.Left == bounds.Left && rect.Top == bounds.Top && rect.Right == bounds.Right && rect.Bottom == bounds.Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
