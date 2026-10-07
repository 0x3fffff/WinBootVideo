using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinBootVideo.Core;

namespace WinBootVideo;

public partial class BootLogoWindow : Window
{
    private readonly AppPaths paths;
    private readonly Func<BootLogoRequest, Task<BootLogoResponse>> execute;
    private readonly Func<string, bool> confirm;
    private readonly Func<BootLogoJournal?> readJournal;
    private byte[]? image;
    private string source = "", prepared = "";
    private BootLogoEnvironment? environment;
    private bool probePassed;
    private bool alreadyInstalled;
    private bool? logoIsDefault;
    private bool busy;
    private int step;
    public BootLogoWindow(AppPaths paths, Func<BootLogoRequest, Task<BootLogoResponse>>? executor = null, Func<string, bool>? confirmation = null,
        Func<BootLogoJournal?>? journalReader = null)
    {
        InitializeComponent(); this.paths = paths; execute = executor ?? RunElevated;
        confirm = confirmation ?? (text => MessageBox.Show(this, text, "开机 Logo · 确认操作", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK);
        readJournal = journalReader ?? BootLogoInstaller.ReadJournal;
        FirstBootGuideText.Text = BootLogoPackage.FirstBootGuide;
        PermanentGuideText.Text = BootLogoPackage.PermanentGuide;
        Refresh(); Closing += (_, e) => { if (busy) { e.Cancel = true; LogoStatus.Text = "请等待操作完成。安装或恢复进行中不能关闭窗口。"; } };
    }
    private void Refresh()
    {
        if (!IsInitialized) return;
        ChooseImageButton.IsEnabled = CanvasChoice.IsEnabled = ProbeButton.IsEnabled = !busy;
        FirmwareDetailsButton.IsEnabled = !busy && !string.IsNullOrEmpty(environment?.FirmwareDetails);
        ExportButton.IsEnabled = !busy && image != null;
        RescueCheck.IsEnabled = TrialCheck.IsEnabled = ShimCheck.IsEnabled = !busy;
        var missing = InstallationRequirements();
        InstallLogoButton.IsEnabled = !busy && missing.Count == 0;
        InstallHint.Text = busy ? "正在执行操作，请等待完成。" : alreadyInstalled ?
            "Logo 已安装，无需重复安装。请按操作详情验证启动；如需更换图片，先恢复原 Logo 后重新安装。" : missing.Count == 0 ? "准备完成，可以安装。安装后先单次试启动，Windows Boot Manager 仍是默认。" :
            "暂不能安装：" + string.Join("；", missing);
        InstallLogoButton.ToolTip = InstallHint.Text;
        try { RestoreLogoButton.IsEnabled = !busy && readJournal() is { Phase: not "Restored" }; }
        catch { RestoreLogoButton.IsEnabled = false; }
        RefreshFlow();
    }
    internal void ShowStep(int value)
    {
        step = Math.Clamp(value, 0, 3); RefreshFlow();
        if (step == 3 && alreadyInstalled && logoIsDefault == true) FirstBootSteps.IsExpanded = false;
        LogoScroll.ScrollToTop();
    }
    private void LogoStepClick(object sender, RoutedEventArgs e)
    {
        if (!busy && sender is Button button && int.TryParse(button.Tag?.ToString(), out int value)) ShowStep(value);
    }
    private void BackClick(object sender, RoutedEventArgs e) { if (!busy) ShowStep(step - 1); }
    private void NextClick(object sender, RoutedEventArgs e)
    {
        Refresh();
        if (busy || !LogoNextButton.IsEnabled) return;
        if (step == 3) { Close(); return; }
        ShowStep(alreadyInstalled ? 3 : step + 1);
    }
    private void CloseClick(object sender, RoutedEventArgs e) { if (!busy) Close(); }
    private void RefreshFlow()
    {
        var pages = new FrameworkElement[] { CheckPage, ImagePage, InstallPage, FinishPage };
        var buttons = new[] { CheckStepButton, ImageStepButton, InstallStepButton, FinishStepButton };
        string[] titles = { "先检查电脑是否支持", "准备你的开机图片", "确认恢复准备，再安装", "试启动，再保存默认入口" };
        string[] descriptions =
        {
            "管理员检查只读取系统和启动信息，不会安装或更改设置。",
            "选择图片后自动转换，导出准备包并保留恢复说明。",
            "只创建独立启动入口。安装完成后，Windows Boot Manager 仍是默认。",
            "安装完成不会自动变成默认。请按下面的步骤验证与保存。"
        };
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i].Visibility = step == i ? Visibility.Visible : Visibility.Collapsed;
            buttons[i].Background = step == i ? (Brush)FindResource("Selection") : Brushes.Transparent;
            buttons[i].Foreground = step == i ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        }
        LogoPageTitle.Text = step == 3 && alreadyInstalled && logoIsDefault == true ? "默认启动入口已保存" : titles[step];
        LogoPageDescription.Text = step == 3 && alreadyInstalled && logoIsDefault == true ?
            "只读检查确认 HackBGRT 已为默认。请验证重启与关机后开机的效果。" : descriptions[step];
        bool defaultSaved = alreadyInstalled && logoIsDefault == true;
        CompletedPageDetails.Visibility = defaultSaved ? Visibility.Visible : Visibility.Collapsed;
        FirstSetupReference.Visibility = defaultSaved ? Visibility.Collapsed : Visibility.Visible;
        FinishStepButton.Content = defaultSaved ? "04  完成与恢复" : "04  首次启动与生效";
        LogoSteps.IsEnabled = CloseLogoButton.IsEnabled = LogoBackButton.IsEnabled = !busy;
        LogoBackButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        InstallLogoButton.Visibility = step == 2 && !alreadyInstalled ? Visibility.Visible : Visibility.Collapsed;
        LogoNextButton.Visibility = step == 2 && !alreadyInstalled ? Visibility.Collapsed : Visibility.Visible;
        bool expired = environment != null && (DateTime.UtcNow - environment.CheckedUtc).TotalMinutes is > 10 or < -1;
        bool supported = probePassed && environment != null && environment.Blockers().Count == 0 && !expired;
        LogoNextButton.IsEnabled = !busy && (alreadyInstalled || step switch
        {
            0 => supported,
            1 => image != null && prepared.Length > 0,
            2 => false,
            _ => true
        });
        LogoNextButton.Content = alreadyInstalled && step < 3 ? "查看首次启动与生效步骤  →" : step switch
        {
            0 => "下一步：准备图片  →",
            1 => "下一步：确认与安装  →",
            _ => "关闭向导"
        };
        LogoNextButton.ToolTip = step == 0 && !supported ? "先完成管理员只读检查；不支持的配置请查看报告。" :
            step == 1 && (image == null || prepared.Length == 0) ? "先选择图片并导出准备包。" : "继续";
        PreparedText.Text = prepared.Length > 0 ? "已导出：" + prepared : "尚未导出。建议把恢复说明另存到手机或其他设备。";
        PreparedText.ToolTip = prepared;
        LogoBadge.Text = busy ? "操作进行中" : alreadyInstalled ? logoIsDefault switch
        {
            true => "已安装 · 已设为默认",
            false => "已安装 · 尚未设为默认",
            _ => "已安装 · 默认入口待检查"
        } : expired ? "检查已过期" : supported ? "检查已通过" :
            environment != null ? "安装条件未满足" : "尚未检查";
        InstalledHeading.Text = alreadyInstalled ? "Logo 已安装，无需重复安装" : "安装与默认启动是两件事";
        InstalledDescription.Text = alreadyInstalled ? logoIsDefault switch
        {
            true => "已核验文件、恢复记录及默认启动项。Logo 保存在 EFI，无需让软件常驻。固件更新或恢复默认设置后，若效果消失，请先重新检查启动顺序。",
            false => "文件与启动项已保存，Windows Boot Manager 仍为默认。下面的单次试启动与 BIOS 保存步骤完成后，才能让每次开机都显示新 Logo。",
            _ => "文件已保存在 EFI。请回到第一步运行只读检查，确认默认启动入口；无需重复安装。"
        } :
            "安装后 Logo 保存在 EFI。先单次试启动，确认效果和 Windows 登录都正常，再手动保存默认入口。";
    }
    private List<string> InstallationRequirements()
    {
        var missing = new List<string>();
        if (alreadyInstalled) { missing.Add("Logo 已安装，无需重复安装；如需更换图片，先恢复原 Logo"); return missing; }
        if (image == null) missing.Add("选择图片并完成转换");
        if (prepared.Length == 0) missing.Add("导出准备包，保留恢复说明");
        if (environment == null) missing.Add("完成管理员只读检查");
        else
        {
            var hardware = environment.Blockers(); missing.AddRange(hardware);
            if (!probePassed && hardware.Count == 0) missing.Add("只读检查未通过，请重新检查并查看启动项详情");
            double age = (DateTime.UtcNow - environment.CheckedUtc).TotalMinutes;
            if (age > 10 || age < -1) missing.Add("检查结果已过期，请重新进行只读检查");
        }
        if (RescueCheck.IsChecked != true) missing.Add("准备恢复 U 盘和帐户密码后勾选第一项确认");
        if (TrialCheck.IsChecked != true) missing.Add("阅读单次试启动说明后勾选第二项确认");
        if (environment?.SecureBoot == true && ShimCheck.IsChecked != true) missing.Add("阅读 Secure Boot / shim 指引后勾选第三项确认");
        return missing;
    }
    private void ChooseImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择静态开机 Logo", Filter = "静态图片|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(this) == true) SelectImage(dialog.FileName);
    }
    internal void SelectImage(string path)
    {
        if (busy) return;
        source = path; ConvertImage(); ShowStep(1);
    }
    private void CanvasChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized && source.Length != 0 && !busy) ConvertImage(); }
    private void ConvertImage()
    {
        image = null; prepared = ""; LogoPreview.Source = null;
        try
        {
            var dimensions = CanvasChoice.SelectedIndex switch { 1 => (256, 256), 2 => (400, 240), 3 => (600, 360), _ => (320, 180) };
            image = BootLogoImage.Convert(source, dimensions.Item1, dimensions.Item2);
            using var stream = new MemoryStream(image);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            LogoPreview.Source = bitmap; PreviewPlaceholder.Visibility = Visibility.Collapsed;
            ImagePathText.Text = Path.GetFileName(source) + " · " + dimensions.Item1 + " × " + dimensions.Item2 + " · BMP3";
            ImagePathText.ToolTip = source;
            LogoStatus.Text = "图片转换与结构校验通过。选择导出目录，保留恢复说明。";
        }
        catch (Exception ex) { PreviewPlaceholder.Visibility = Visibility.Visible; LogoStatus.Text = ex.Message; }
        Refresh();
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择准备包的父文件夹" };
        if (dialog.ShowDialog(this) == true) ExportTo(Path.Combine(dialog.FolderName, "WinBootVideo-Logo-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
    }
    internal void ExportTo(string newDirectory)
    {
        if (busy || image == null) return;
        prepared = "";
        try { prepared = BootLogoPackage.Extract(newDirectory, image); LogoStatus.Text = "准备包已导出：" + prepared + "。删除原图不影响准备包；导出未改启动设置。"; }
        catch (Exception ex) { LogoStatus.Text = ex.Message; }
        Refresh();
    }
    private async void ProbeClick(object sender, RoutedEventArgs e) => await Work(async () =>
    {
        environment = null; probePassed = false; alreadyInstalled = false; logoIsDefault = null;
        HardwareText.Text = "正在进行管理员只读检查…"; Refresh();
        BootLogoResponse response;
        try { response = await execute(new BootLogoRequest()); }
        catch { HardwareText.Text = "只读检查未完成；尚不能确认安装条件，请重新检查。"; throw; }
        environment = response.Environment; probePassed = response.Success; alreadyInstalled = response.Success && response.AlreadyInstalled;
        logoIsDefault = alreadyInstalled ? response.LogoIsDefault : null;
        HardwareText.Text = environment == null ? response.Message :
            environment.Model + "\nUEFI x64：" + (environment.Uefi && environment.X64 ? "是" : "否 / 未知") +
            " · 固件：" + (environment.FirmwareManufacturer.Length == 0 ? "未知" : environment.FirmwareManufacturer) +
            " · 安全启动：" + (environment.SecureBoot == null ? "未知" : environment.SecureBoot.Value ? "开（使用 shim）" : "关") +
            "\n系统盘：" + (environment.OsFullyDecrypted == true ? "未加密" : "已加密 / 未知") + " · EFI 分区：" + environment.EspCount +
            " · 可用：" + (environment.EspFreeBytes == null ? "未知" : environment.EspFreeBytes.Value / (1024 * 1024) + " MB") +
            "\n" + response.Message;
        LogoStatus.Text = response.Message;
        if (alreadyInstalled) ShowStep(3);
    });
    private void ConfirmationChanged(object sender, RoutedEventArgs e) => Refresh();
    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        Refresh(); if (!InstallLogoButton.IsEnabled || image == null) return;
        if (!File.Exists(Path.Combine(prepared, "splash.bmp")) || BootLogoPackage.HashFile(Path.Combine(prepared, "splash.bmp")) != BootLogoPackage.Hash(image))
        { prepared = ""; Refresh(); LogoStatus.Text = "准备包图片被改动或删除，请重新导出。"; return; }
        string detail = "将重新检查电脑、备份启动器与固件项，安装 EFI\\HackBGRT 文件并把独立项追加到启动顺序末尾。\nWindows Boot Manager 保持默认；不会自动重启。\n\n已导出：" + prepared + "\n\n无法保证所有固件兼容。确认已准备恢复 U 盘并接受先单次试启动后，继续安装？";
        if (!confirm(detail)) return;
        await Work(async () =>
        {
            var result = await execute(new BootLogoRequest { Action = "install", ImageFile = Path.Combine(prepared, "splash.bmp"), ImageHash = BootLogoPackage.Hash(image),
                RescueReady = RescueCheck.IsChecked == true, ManualTrialAccepted = TrialCheck.IsChecked == true, ShimUnderstood = ShimCheck.IsChecked == true });
            LogoStatus.Text = result.Message; environment = null; probePassed = false; alreadyInstalled = result.Success;
            logoIsDefault = result.Success ? result.LogoIsDefault : null;
            if (result.Success) ShowStep(3);
        });
    }
    private async void RestoreClick(object sender, RoutedEventArgs e)
    {
        if (!confirm("将只移除本软件记录的 Logo 启动项，保留 Windows 启动器和 EFI Logo 文件。继续恢复？")) return;
        await Work(async () => { var result = await execute(new BootLogoRequest { Action = "restore" }); LogoStatus.Text = result.Message; environment = null; probePassed = false; alreadyInstalled = false; logoIsDefault = null; });
    }
    private async Task Work(Func<Task> action)
    {
        if (busy) return; busy = true; LogoBusy.Visibility = Visibility.Visible; Refresh();
        try { await action(); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { LogoStatus.Text = "已取消管理员授权，未开始该操作。"; }
        catch (Exception ex) { LogoStatus.Text = ex.Message; }
        finally { busy = false; LogoBusy.Visibility = Visibility.Collapsed; Refresh(); }
    }
    private async Task<BootLogoResponse> RunElevated(BootLogoRequest request)
    {
        string directory = Path.Combine(paths.DataDirectory, "logo-requests", Guid.NewGuid().ToString("N"));
        BootLogoPaths.OrdinaryDirectory(directory); Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "request.json"), output = Path.Combine(directory, "response.json");
        File.WriteAllText(input, JsonSerializer.Serialize(request, Settings.JsonOptions));
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--boot-logo-helper"); start.ArgumentList.Add(input); start.ArgumentList.Add(output);
        using var process = Process.Start(start) ?? throw new IOException("无法启动管理员操作。");
        // Never kill an elevated firmware transaction on a GUI timeout; closing is blocked.
        await process.WaitForExitAsync();
        if (!File.Exists(output)) throw new IOException("管理员操作没有返回报告。默认启动不会由 GUI 自动更改。请检查恢复记录。");
        var result = JsonSerializer.Deserialize<BootLogoResponse>(File.ReadAllText(output), Settings.JsonOptions) ?? throw new IOException("操作报告无效。");
        if (!result.Success && request.Action != "probe") throw new IOException(result.Message);
        return result;
    }
    private void GuideClick(object sender, RoutedEventArgs e)
        => ShowText("开机 Logo · 图片、BIOS 与恢复说明", BootLogoPackage.Guide);
    private void PermanentGuideClick(object sender, RoutedEventArgs e)
        => ShowText("开机 Logo · 每次开机生效", BootLogoPackage.PermanentGuide);
    private void RequirementsClick(object sender, RoutedEventArgs e)
    {
        Refresh(); ShowText("开机 Logo · 安装条件", InstallHint.Text);
    }
    private void DetailsClick(object sender, RoutedEventArgs e) => ShowText("开机 Logo · 操作详情", LogoStatus.Text +
        (string.IsNullOrEmpty(environment?.FirmwareDetails) ? "" : "\n\n" + environment.FirmwareDetails));
    private void FirmwareDetailsClick(object sender, RoutedEventArgs e) => ShowText("开机 Logo · 只读启动项详情", environment?.FirmwareDetails ?? "尚未读取固件启动项。");
    private void ShowText(string title, string text)
    {
        InformationWindow.Show(this, title, text);
    }
    private void FirstBootClick(object sender, RoutedEventArgs e)
        => ShowText("开机 Logo · 首次安全验证步骤", BootLogoPackage.FirstBootGuide);
    private void ShimGuideClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/Metabolix/HackBGRT/blob/v2.6.0/shim.md") { UseShellExecute = true });
}
