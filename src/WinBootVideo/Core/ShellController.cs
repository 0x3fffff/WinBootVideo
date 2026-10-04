using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using WinBootVideo.Shared;

namespace WinBootVideo.Core;

public sealed class ShellValue
{
    public bool Exists { get; set; }
    public string Value { get; set; } = "";
    public int Kind { get; set; } = (int)RegistryValueKind.String;
}
public sealed class ShellBackup
{
    public int SchemaVersion { get; set; } = 1;
    public ShellValue Original { get; set; } = new();
    public string OwnedCommand { get; set; } = "";
    public string UserSid { get; set; } = "";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
}
public interface IUserShellRegistry
{
    ShellValue Read();
    void Write(ShellValue value);
    bool HasCustomUserPolicy();
}
public sealed class UserShellRegistry : IUserShellRegistry
{
    public const string KeyPath = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public ShellValue Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key == null || !key.GetValueNames().Contains("Shell", StringComparer.OrdinalIgnoreCase)) return new ShellValue();
        var kind = key.GetValueKind("Shell");
        if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString) throw new InvalidDataException("现有 Shell 值不是文本，本版不会覆盖。");
        return new ShellValue { Exists = true, Kind = (int)kind, Value = (string?)key.GetValue("Shell", "", RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "" };
    }
    public void Write(ShellValue value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (value.Exists) key.SetValue("Shell", value.Value, (RegistryValueKind)value.Kind);
        else key.DeleteValue("Shell", false);
    }
    public bool HasCustomUserPolicy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
        return !string.IsNullOrWhiteSpace(key?.GetValue("Shell") as string);
    }
}
public sealed class ShellController
{
    private readonly IUserShellRegistry registry;
    private readonly AppPaths paths;
    public string Command => WindowsCommandLine.Quote(Path.Combine(paths.AppDirectory, "WinBootVideo.Host.exe")) + " --data-dir " + WindowsCommandLine.Quote(paths.DataDirectory);
    public ShellController(AppPaths paths, IUserShellRegistry? registry = null) { this.paths = paths; this.registry = registry ?? new UserShellRegistry(); }
    public bool IsEnabled => IsOwned(registry.Read());
    public bool IsOwned(ShellValue current) => current.Exists && string.Equals(current.Value, Command, StringComparison.OrdinalIgnoreCase);
    public void Enable(string userSid)
    {
        if (registry.HasCustomUserPolicy()) throw new InvalidOperationException("已有自定义用户界面策略，本版不会覆盖。请先处理该策略。");
        var current = registry.Read();
        if (IsOwned(current)) return;
        if (current.Exists && !IsExplorer(current.Value)) throw new InvalidOperationException("当前用户已有其他自定义 Shell，本版不会替换它。");
        var backup = new ShellBackup { Original = current, OwnedCommand = Command, UserSid = userSid };
        AtomicJson.Write(paths.BackupFile, backup); // A durable backup must precede the only registry mutation.
        try
        {
            registry.Write(new ShellValue { Exists = true, Value = Command });
            if (!IsEnabled) throw new IOException("启动配置写入后核验失败。");
        }
        catch
        {
            if (IsOwned(registry.Read())) registry.Write(current);
            throw;
        }
    }
    public bool Disable(string userSid)
    {
        var current = registry.Read();
        if (!IsOwned(current)) return false; // Never overwrite another app's newer Shell setting.
        if (!File.Exists(paths.BackupFile)) throw new FileNotFoundException("缺少 Shell 备份；请使用说明中的恢复步骤。", paths.BackupFile);
        var backup = JsonSerializer.Deserialize<ShellBackup>(File.ReadAllText(paths.BackupFile), Settings.JsonOptions) ?? throw new InvalidDataException("备份为空。");
        if (backup.SchemaVersion != 1 || backup.UserSid != userSid || backup.OwnedCommand != Command || backup.Original == null || (backup.Original.Exists && backup.Original.Kind != 1 && backup.Original.Kind != 2))
            throw new InvalidDataException("Shell 备份与当前用户或应用路径不匹配。");
        registry.Write(backup.Original);
        return true;
    }
    private static bool IsExplorer(string value)
    {
        string normalized = Environment.ExpandEnvironmentVariables(value).Trim().Trim('"');
        return normalized.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || normalized.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);
    }
    public void Deploy(string sourceDirectory)
    {
        string[] required = { "WinBootVideo.exe", "WinBootVideo.dll", "WinBootVideo.runtimeconfig.json", "WinBootVideo.deps.json", "WinBootVideo.Host.exe", "WinBootVideo.Host.exe.config" };
        foreach (string file in required) if (!File.Exists(Path.Combine(sourceDirectory, file))) throw new FileNotFoundException("应用包不完整：" + file);
        Directory.CreateDirectory(paths.AppDirectory);
        foreach (string file in required)
        {
            string source = Path.GetFullPath(Path.Combine(sourceDirectory, file));
            string target = Path.Combine(paths.AppDirectory, file);
            if (!source.Equals(target, StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, true);
        }
    }
}
