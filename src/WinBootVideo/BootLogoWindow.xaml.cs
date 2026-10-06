using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private bool busy;
    public BootLogoWindow(AppPaths paths, Func<BootLogoRequest, Task<BootLogoResponse>>? executor = null, Func<string, bool>? confirmation = null,
        Func<BootLogoJournal?>? journalReader = null)
    {
        InitializeComponent(); this.paths = paths; execute = executor ?? RunElevated;
        confirm = confirmation ?? (text => MessageBox.Show(this, text, "开机 Logo · 确认操作", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK);
        readJournal = journalReader ?? BootLogoInstaller.ReadJournal;
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
        source = path; ConvertImage();
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
        environment = null; probePassed = false; alreadyInstalled = false;
        HardwareText.Text = "正在进行管理员只读检查…"; Refresh();
        BootLogoResponse response;
        try { response = await execute(new BootLogoRequest()); }
        catch { HardwareText.Text = "只读检查未完成；尚不能确认安装条件，请重新检查。"; throw; }
        environment = response.Environment; probePassed = response.Success; alreadyInstalled = response.Success && response.AlreadyInstalled;
        HardwareText.Text = environment == null ? response.Message :
            environment.Model + "\nUEFI x64：" + (environment.Uefi && environment.X64 ? "是" : "否 / 未知") +
            " · 固件：" + (environment.FirmwareManufacturer.Length == 0 ? "未知" : environment.FirmwareManufacturer) +
            " · 安全启动：" + (environment.SecureBoot == null ? "未知" : environment.SecureBoot.Value ? "开（使用 shim）" : "关") +
            "\n系统盘：" + (environment.OsFullyDecrypted == true ? "未加密" : "已加密 / 未知") + " · EFI 分区：" + environment.EspCount +
            " · 可用：" + (environment.EspFreeBytes == null ? "未知" : environment.EspFreeBytes.Value / (1024 * 1024) + " MB") +
            "\n" + response.Message;
        LogoStatus.Text = response.Message;
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
        });
    }
    private async void RestoreClick(object sender, RoutedEventArgs e)
    {
        if (!confirm("将只移除本软件记录的 Logo 启动项，保留 Windows 启动器和 EFI Logo 文件。继续恢复？")) return;
        await Work(async () => { var result = await execute(new BootLogoRequest { Action = "restore" }); LogoStatus.Text = result.Message; environment = null; probePassed = false; alreadyInstalled = false; });
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
    private void RequirementsClick(object sender, RoutedEventArgs e)
    {
        Refresh(); ShowText("开机 Logo · 安装条件", InstallHint.Text);
    }
    private void DetailsClick(object sender, RoutedEventArgs e) => ShowText("开机 Logo · 操作详情", LogoStatus.Text +
        (string.IsNullOrEmpty(environment?.FirmwareDetails) ? "" : "\n\n" + environment.FirmwareDetails));
    private void FirmwareDetailsClick(object sender, RoutedEventArgs e) => ShowText("开机 Logo · 只读启动项详情", environment?.FirmwareDetails ?? "尚未读取固件启动项。");
    private void ShowText(string title, string text)
    {
        var window = new Window { Title = title, Owner = this, Width = 820, Height = 690, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20) } };
        window.ShowDialog();
    }
    private void ShimGuideClick(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/Metabolix/HackBGRT/blob/v2.6.0/shim.md") { UseShellExecute = true });
}
