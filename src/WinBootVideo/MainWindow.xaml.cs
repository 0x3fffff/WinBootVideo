using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WinBootVideo.Core;
using WinBootVideo.Shared;
using Forms = System.Windows.Forms;

namespace WinBootVideo;

public partial class MainWindow : Window
{
    private readonly AppPaths paths;
    private readonly SettingsStore store;
    private readonly LoginStartupController shell;
    private readonly string previewHost;
    private readonly bool previewWindowed;
    private Settings settings;
    private VideoInfo? videoInfo;
    private CancellationTokenSource? operation;
    private bool busy;
    private int loginStep;
    private bool loginVisible;
    private bool startupEnabled;
    private string Sid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法识别当前用户。");
    public MainWindow(AppPaths paths, IUserShellRegistry? registry = null, string? previewHost = null, bool previewWindowed = false, ILoginTaskStore? startupTasks = null)
    {
        InitializeComponent();
        this.paths = paths;
        store = new SettingsStore(paths);
        shell = new LoginStartupController(paths, registry, startupTasks);
        this.previewHost = previewHost ?? Path.Combine(AppContext.BaseDirectory, "WinBootVideo.Host.exe");
        this.previewWindowed = previewWindowed;
        try { settings = store.Load(); settings.Validate(false); }
        catch (Exception ex) { settings = new Settings(); StatusText.Text = "配置读取失败，可重新选择视频。" + ex.Message; }
        ScreenChoice.Items.Add(new ScreenOption("", "主屏幕（自动）"));
        int screenNumber = 0;
        foreach (var screen in Forms.Screen.AllScreens) ScreenChoice.Items.Add(new ScreenOption(screen.DeviceName, "屏幕 " + (++screenNumber) + (screen.Primary ? "（主屏）" : "") + " · " + screen.Bounds.Width + "×" + screen.Bounds.Height));
        ScreenChoice.SelectedValue = settings.ScreenDevice;
        if (ScreenChoice.SelectedIndex < 0) ScreenChoice.SelectedIndex = 0;
        CoverOthers.IsChecked = settings.CoverOtherScreens;
        VolumeSlider.Value = settings.Volume * 100;
        FadeText.Text = settings.FadeOutMilliseconds.ToString();
        MaximumText.Text = settings.MaxDurationSeconds.ToString();
        StartupText.Text = settings.StartupTimeoutSeconds.ToString();
        VersionText.Text = AppVersion.Display + " · Windows 11";
        RefreshFile(); RefreshTools(); RefreshStartup(); ShowHome();
        Closing += OnClosing;
    }
    private void RefreshFile()
    {
        FileNameText.Text = string.IsNullOrEmpty(settings.VideoPath) ? "把视频拖到这里，或点击选择" : Path.GetFileName(settings.VideoPath);
        FilePathText.Text = string.IsNullOrEmpty(settings.VideoPath) ? "尚未选择视频" : settings.VideoPath;
        FilePathText.ToolTip = settings.VideoPath;
    }
    private void BootLogoClick(object sender, RoutedEventArgs e)
    {
        if (!busy) { new BootLogoWindow(paths) { Owner = this }.ShowDialog(); ShowHome(); }
    }
    private void HomeClick(object sender, RoutedEventArgs e) { if (!busy) ShowHome(); }
    private void LoginClick(object sender, RoutedEventArgs e) { if (!busy) ShowLogin(); }
    private void ShowHome()
    {
        loginVisible = false; HomeView.Visibility = Visibility.Visible; LoginView.Visibility = Visibility.Collapsed;
        RefreshFlow();
    }
    internal void ShowLogin(int? step = null)
    {
        loginVisible = true; HomeView.Visibility = Visibility.Collapsed; LoginView.Visibility = Visibility.Visible;
        if (step != null) loginStep = Math.Clamp(step.Value, 0, 2);
        RefreshFlow(); LoginScroll.ScrollToTop();
    }
    private void LoginStepClick(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button button || !int.TryParse(button.Tag?.ToString(), out int step)) return;
        if (step > 0 && !File.Exists(settings.VideoPath)) { StatusText.Text = "先选择一个可用的本地视频。"; return; }
        ShowLogin(step);
    }
    private void LoginBackClick(object sender, RoutedEventArgs e) { if (!busy) ShowLogin(loginStep - 1); }
    private void LoginNextClick(object sender, RoutedEventArgs e)
    {
        if (busy || !File.Exists(settings.VideoPath)) return;
        if (loginStep == 1)
        {
            try { ReadControls(); }
            catch (Exception ex) { StatusText.Text = ex.Message; return; }
        }
        ShowLogin(loginStep + 1);
    }
    private void RefreshFlow()
    {
        bool hasVideo = File.Exists(settings.VideoPath);
        bool verified = settings.VideoSha256.Length == 64 && settings.VerifiedVideoSha256 == settings.VideoSha256;
        HomeButton.Background = loginVisible ? Brushes.Transparent : (Brush)FindResource("Selection");
        LoginButton.Background = loginVisible ? (Brush)FindResource("Selection") : Brushes.Transparent;
        var steps = new[] { VideoStepButton, SettingsStepButton, PreviewStepButton };
        for (int i = 0; i < steps.Length; i++)
        {
            steps[i].Background = loginStep == i ? (Brush)FindResource("Selection") : Brushes.Transparent;
            steps[i].Foreground = loginStep == i ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
            steps[i].IsEnabled = !busy && (i == 0 || hasVideo);
        }
        VideoPage.Visibility = loginStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = loginStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviewPage.Visibility = loginStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        LoginBackButton.Visibility = loginStep == 0 ? Visibility.Collapsed : Visibility.Visible;
        LoginBackButton.IsEnabled = !busy;
        LoginNextButton.Visibility = loginStep < 2 ? Visibility.Visible : Visibility.Collapsed;
        LoginNextButton.IsEnabled = !busy && hasVideo;
        LoginNextButton.Content = loginStep == 0 ? "下一步：播放设置  →" : "下一步：预览与启用  →";
        LoginNextButton.ToolTip = hasVideo ? "继续设置" : "先选择一个可用的本地视频。";
        PreviewButton.Visibility = EnableButton.Visibility = loginStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.IsEnabled = !busy && hasVideo;
        PreviewFileText.Text = hasVideo ? Path.GetFileName(settings.VideoPath) : "尚未选择视频";
        PreviewSummaryText.Text = (ScreenChoice.SelectedItem as ScreenOption)?.Label + " · 音量 " +
            VolumeSlider.Value.ToString("0") + "% · 淡出 " + FadeText.Text + " 毫秒";
        PreviewHeading.Text = startupEnabled ? "登录动画已启用" : "先确认播放效果，再启用";
        PreviewDescription.Text = startupEnabled ? "下次登录会播放。无需保持设置窗口开启，播放结束后宿主与播放器会退出。" : "全屏预览成功后，才可以启用下次登录播放。";
        PreviewResultText.Text = verified ? "视频已通过播放验证" : "尚未成功预览";
        EnableReasonText.Text = startupEnabled ? "如需更换视频或设置，重新保存预览后再启用。也可随时恢复普通登录。" :
            verified ? "确认预览效果后，点击“启用登录动画”。" : "先保存并全屏预览；加载失败或超时不会允许启用。";
        EnableButton.ToolTip = EnableReasonText.Text;
        HomeLoginStatus.Text = StartupBadge.Text;
    }
    private void RefreshTools()
    {
        string? ffmpeg = MediaTools.FindTool("ffmpeg", settings.FfmpegPath);
        string? probe = MediaTools.FindTool("ffprobe", settings.FfprobePath);
        ToolsText.Text = ffmpeg != null && probe != null ? "已找到 FFmpeg / ffprobe，可检查和转换。" : "检查需 ffprobe，转换需 FFmpeg；直接预览无需它们。";
        ToolsText.ToolTip = ffmpeg ?? "未找到 FFmpeg";
    }
    private void RefreshStartup()
    {
        try
        {
            bool enabled = shell.IsStrictEnabled(Sid);
            startupEnabled = enabled;
            bool owned = shell.HasOwnedStartup(Sid);
            StartupBadge.Text = enabled ? "已启用 · 下次登录生效" : owned ? "登录启动待修复" : "尚未启用";
            RestoreButton.IsEnabled = !busy && owned;
            EnableButton.Content = owned && !enabled ? "修复登录启动" : "启用登录动画";
            EnableButton.IsEnabled = !busy && !enabled && settings.VideoSha256.Length == 64 && settings.VerifiedVideoSha256 == settings.VideoSha256;
        }
        catch (Exception ex)
        {
            startupEnabled = false;
            StartupBadge.Text = "无法完整读取登录配置"; EnableButton.IsEnabled = false;
            // A scheduler failure must not prevent recovery of an owned Shell.
            try { RestoreButton.IsEnabled = !busy && shell.ShellEnabled; }
            catch { RestoreButton.IsEnabled = false; }
            StatusText.Text = ex.Message + (RestoreButton.IsEnabled ? " 可先恢复普通登录。" : "");
        }
        RefreshFlow();
    }
    private Settings ReadControls()
    {
        var draft = settings.Copy();
        draft.Volume = VolumeSlider.Value / 100;
        draft.FadeOutMilliseconds = ParseInteger(FadeText.Text, "淡出");
        draft.MaxDurationSeconds = ParseInteger(MaximumText.Text, "等待上限");
        draft.StartupTimeoutSeconds = ParseInteger(StartupText.Text, "视频加载等待");
        draft.ScreenDevice = (ScreenChoice.SelectedItem as ScreenOption)?.Device ?? "";
        draft.CoverOtherScreens = CoverOthers.IsChecked == true;
        draft.Validate();
        return draft;
    }
    private static int ParseInteger(string value, string label) => int.TryParse(value, out int number) ? number : throw new InvalidDataException(label + "须为整数。");
    private void ChooseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择登录动画视频", InitialDirectory = Path.GetDirectoryName(settings.VideoPath) ?? AppContext.BaseDirectory, Filter = "视频文件|*.mp4;*.m4v;*.mov;*.wmv;*.mkv;*.avi;*.webm|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) SelectVideo(dialog.FileName);
    }
    internal void SelectVideo(string file)
    {
        if (busy) return;
        try
        {
            Settings.ValidateLocalVideo(file);
            settings.VideoPath = Path.GetFullPath(file);
            settings.VideoSha256 = ""; settings.VerifiedVideoSha256 = ""; videoInfo = null;
            MetadataText.Text = "视频已选中。可检查格式，或直接保存并全屏预览。";
            StatusText.Text = "新视频尚未保存。已启用的动画会保留到下一次保存预览。";
            RefreshFile(); RefreshStartup(); ShowLogin(0);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void OnDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1) SelectVideo(files[0]); }
    private async Task WorkAsync(string message, Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true; operation = new CancellationTokenSource(); Editor.IsEnabled = false; ActionButtons.IsEnabled = false; RestoreButton.IsEnabled = false; Navigation.IsEnabled = false; LoginSteps.IsEnabled = false; LoginBackButton.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible; CancelButton.Visibility = Visibility.Visible; StatusText.Text = message;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "操作已取消。"; }
        catch (Exception ex) { StatusText.Text = "操作失败：" + ex.Message; App.Log(paths, ex); }
        finally { busy = false; operation.Dispose(); operation = null; Editor.IsEnabled = true; ActionButtons.IsEnabled = true; Navigation.IsEnabled = true; LoginSteps.IsEnabled = true; BusyBar.Visibility = Visibility.Collapsed; CancelButton.Visibility = Visibility.Collapsed; RefreshTools(); RefreshStartup(); }
    }
    private string Tool(string name)
    {
        string configured = name == "ffmpeg" ? settings.FfmpegPath : settings.FfprobePath;
        return MediaTools.FindTool(name, configured) ?? throw new FileNotFoundException("未找到 " + name + "。请点击“选择 FFmpeg”，选择同时含 ffmpeg.exe 和 ffprobe.exe 的目录。也可直接预览。");
    }
    private async void ProbeClick(object sender, RoutedEventArgs e) => await WorkAsync("正在检查视频格式…", async token =>
    {
        videoInfo = await MediaTools.ProbeAsync(settings.VideoPath, Tool("ffprobe"), token);
        MetadataText.Text = videoInfo.Summary;
        StatusText.Text = videoInfo.Recommended ? "符合推荐格式，仍需成功预览后启用。" : "建议转换为兼容 MP4；也可先预览检查此机器的解码能力。";
        if (videoInfo.Duration > 0 && videoInfo.Duration + settings.FadeOutMilliseconds / 1000.0 >= settings.MaxDurationSeconds)
            StatusText.Text += " 视频超过等待上限，会被截断；请剪短。";
    });
    private void ToolClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 ffmpeg.exe（ffprobe.exe 应放在同一目录）", Filter = "FFmpeg|ffmpeg.exe" };
        if (dialog.ShowDialog(this) != true) return;
        settings.FfmpegPath = dialog.FileName;
        settings.FfprobePath = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "ffprobe.exe");
        RefreshTools(); StatusText.Text = "工具路径已选择，会随下一次保存预览一起保存。";
    }
    private async void ConvertClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "保存处理后的视频（不覆盖原文件）", Filter = "MP4 视频|*.mp4", FileName = "login-compatible.mp4", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        await WorkAsync("正在转换为兼容 MP4，较长视频可能需要几分钟…", async token =>
        {
            if (!double.TryParse(TrimSeconds.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds) || !double.IsFinite(seconds) || seconds <= 0 || seconds > 80)
                throw new InvalidDataException("保留时长须大于 0 且不超过 80 秒，推荐 5–15 秒。");
            videoInfo = await MediaTools.ProbeAsync(settings.VideoPath, Tool("ffprobe"), token);
            await MediaTools.ConvertAsync(Tool("ffmpeg"), settings.VideoPath, dialog.FileName, seconds, MuteConversion.IsChecked == true, videoInfo.Hdr, token);
            settings.VideoPath = dialog.FileName; settings.VideoSha256 = ""; settings.VerifiedVideoSha256 = "";
            videoInfo = await MediaTools.ProbeAsync(settings.VideoPath, Tool("ffprobe"), token);
            MetadataText.Text = videoInfo.Summary; RefreshFile();
            StatusText.Text = "转换完成，已选用新视频。下一步保存并全屏预览。";
        });
    }
    private async void PreviewClick(object sender, RoutedEventArgs e) => await WorkAsync("正在保存视频并准备预览…", async token =>
    {
        var draft = ReadControls();
        if (shell.HasOwnedStartup(Sid)) shell.Disable(Sid);
        draft.VerifiedVideoSha256 = ""; // Each saved candidate needs a successful playback receipt.
        settings = await store.ImportAndSaveAsync(draft, token);
        RefreshFile();
        string receipt = Path.Combine(paths.DataDirectory, "preview-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            StatusText.Text = "预览中：按 Esc 或空格跳过。自动播放已暂停，成功后请点击“启用登录动画”。";
            var arguments = new[] { "--preview", "--config", paths.SettingsFile, "--data-dir", paths.DataDirectory, "--receipt", receipt }.Concat(previewWindowed ? new[] { "--windowed" } : Array.Empty<string>());
            var result = await MediaTools.RunAsync(previewHost, arguments, TimeSpan.FromSeconds(settings.MaxDurationSeconds + 10), token);
            PreviewReceipt? report = File.Exists(receipt) ? JsonSerializer.Deserialize<PreviewReceipt>(File.ReadAllText(receipt), Settings.JsonOptions) : null;
            if ((result.ExitCode != 0 && result.ExitCode != 2) || report?.Succeeded != true || report.VideoSha256 != settings.VideoSha256)
                throw new InvalidDataException("预览未成功（退出码 " + result.ExitCode + "）：" + (report?.Message ?? "播放器未完成。请检查视频，或转换后重试。") + (string.IsNullOrWhiteSpace(result.Error) ? "" : "\n" + MediaTools.Tail(result.Error)));
            if (await SettingsStore.HashAsync(settings.VideoPath, token) != settings.VideoSha256) throw new IOException("视频发生变化，请重新预览。");
            settings.VerifiedVideoSha256 = settings.VideoSha256;
            store.Write(settings);
            StatusText.Text = "预览成功。视频已复制到应用目录，原文件可以删除。确认效果后点击“启用登录动画”。";
        }
        finally { if (File.Exists(receipt)) File.Delete(receipt); }
    });
    private async void EnableClick(object sender, RoutedEventArgs e) => await WorkAsync("正在备份并设置当前用户登录动画…", async token =>
    {
        var draft = ReadControls();
        if (settings.VerifiedVideoSha256 != settings.VideoSha256 || settings.VideoSha256.Length != 64) throw new InvalidOperationException("请先成功预览视频。");
        if (await SettingsStore.HashAsync(settings.VideoPath, token) != settings.VideoSha256) throw new IOException("视频发生变化，请重新预览。");
        store.Write(draft);
        shell.Deploy(AppContext.BaseDirectory);
        WriteRecoveryCommand();
        shell.EnableStrict(Sid);
        settings = draft;
        StatusText.Text = "已启用桌面前模式。视频首帧呈现后才启动 Explorer；已移除本应用的备用播放任务。此窗口可恢复普通登录。";
    });
    private void WriteRecoveryCommand()
    {
        Directory.CreateDirectory(paths.DataDirectory);
        // This helper has no interpolated user values; the containing path is resolved by cmd itself.
        File.WriteAllText(Path.Combine(paths.DataDirectory, "恢复普通登录.cmd"), "@echo off\r\n\"%~dp0app\\WinBootVideo.Host.exe\" --restore --data-dir \"%~dp0.\"\r\nif errorlevel 1 (echo Recovery failed. Check logs and shell-backup.json. & pause)\r\n");
    }
    private void RestoreClick(object sender, RoutedEventArgs e)
    {
        try { StatusText.Text = shell.Disable(Sid) ? "已恢复此前的登录配置，下次登录进入普通桌面。" : "当前配置不属于本应用，没有更改。"; RefreshStartup(); }
        catch (Exception ex) { App.Log(paths, ex); RefreshStartup(); StatusText.Text = ex.Message; }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; operation?.Cancel(); StatusText.Text = "正在取消当前操作，请稍后关闭窗口。"; }
    }
    private void HelpClick(object sender, RoutedEventArgs e)
    {
        InformationWindow.Show(this, "使用说明与恢复", ApplicationGuide.Overview +
            "\n\n本次运行的数据目录：" + paths.DataDirectory +
            "\n本次运行的恢复宿主：" + Path.Combine(paths.AppDirectory, "WinBootVideo.Host.exe"));
    }
    private void AboutClick(object sender, RoutedEventArgs e)
        => InformationWindow.Show(this, "关于 WinBootVideo",
            "WinBootVideo " + AppVersion.Display + "\n作者：0x3fffff\n\nWindows 11 开机与登录个性化工具。\n" +
            "登录动画：设置当前用户的登录入口，验证身份后播放视频。\n开机 Logo：通过 HackBGRT 修改 Windows 加载时的 BGRT 图片。\n\n" +
            "开源许可：MIT。第三方 HackBGRT、shim 等组件保留各自许可；详见软件目录中的 THIRD-PARTY-NOTICES.md 和 licenses 文件夹。\n\n" +
            "使用范围与验证记录见 README.md / QA.md。真实冷启动时序及其他固件的兼容性仍需验证。");
    private sealed record ScreenOption(string Device, string Label);
}
