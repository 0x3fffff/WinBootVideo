using System;
using System.IO;
using System.Text.Json;
using WinBootVideo.Shared;

namespace WinBootVideo.Core;

public sealed class LogonTaskBackup
{
    public int SchemaVersion { get; set; } = 1;
    public string UserSid { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string OwnedXml { get; set; } = "";
    public LoginTaskSnapshot Original { get; set; } = new();
}

public sealed class LogonTaskController
{
    private readonly AppPaths paths;
    private readonly ILoginTaskStore store;
    private string HostPath => Path.Combine(paths.AppDirectory, "WinBootVideo.Host.exe");
    public string BackupFile => Path.Combine(paths.DataDirectory, "login-task-backup.json");
    public LogonTaskController(AppPaths paths, ILoginTaskStore store) { this.paths = paths; this.store = store; }
    public string OwnedXml(string sid) => LoginTaskDefinition.Create(HostPath, paths.DataDirectory, sid);
    private bool Owned(LoginTaskSnapshot task, string sid) => task.Exists && LoginTaskDefinition.IsOwned(task.Xml, HostPath, paths.DataDirectory, sid);
    public bool HasOwnedTask(string sid) => Owned(store.Read(LoginTaskDefinition.TaskName(sid)), sid);
    public bool IsEnabled(string sid)
    {
        var current = store.Read(LoginTaskDefinition.TaskName(sid));
        return Owned(current, sid) && LoginTaskDefinition.IsEnabled(current.Xml);
    }
    public void Enable(string sid)
    {
        string name = LoginTaskDefinition.TaskName(sid);
        var current = store.Read(name);
        if (current.Exists && !Owned(current, sid)) throw new InvalidOperationException("已有其他应用使用同名登录任务，本版不会覆盖它。");
        if (Owned(current, sid))
        {
            if (LoginTaskDefinition.IsEnabled(current.Xml)) return;
            ReadBackup(sid); // Re-enabling an owned disabled task preserves its original backup.
        }
        else
        {
            AtomicJson.Write(BackupFile, new LogonTaskBackup { UserSid = sid, TaskName = name, OwnedXml = OwnedXml(sid), Original = current });
        }
        try
        {
            store.Write(name, OwnedXml(sid));
            if (!IsEnabled(sid)) throw new IOException("登录任务注册后核验失败。");
        }
        catch (Exception failure)
        {
            try
            {
                if (Owned(store.Read(name), sid)) RestoreSnapshot(name, current);
            }
            catch (Exception rollback) { throw new AggregateException("登录任务注册失败，回滚也未完成。", failure, rollback); }
            throw;
        }
    }
    public bool Restore(string sid)
    {
        string name = LoginTaskDefinition.TaskName(sid);
        if (!Owned(store.Read(name), sid)) return false;
        var backup = ReadBackup(sid);
        RestoreSnapshot(name, backup.Original);
        return true;
    }
    private LogonTaskBackup ReadBackup(string sid)
    {
        if (!File.Exists(BackupFile)) throw new FileNotFoundException("缺少登录任务备份，不能自动恢复。", BackupFile);
        var backup = JsonSerializer.Deserialize<LogonTaskBackup>(File.ReadAllText(BackupFile), Settings.JsonOptions) ?? throw new InvalidDataException("登录任务备份为空。");
        if (backup.SchemaVersion != 1 || backup.UserSid != sid || backup.TaskName != LoginTaskDefinition.TaskName(sid) || backup.Original == null ||
            !LoginTaskDefinition.IsOwned(backup.OwnedXml, HostPath, paths.DataDirectory, sid) ||
            (backup.Original.Exists && !LoginTaskDefinition.IsOwned(backup.Original.Xml, HostPath, paths.DataDirectory, sid)))
            throw new InvalidDataException("登录任务备份与当前用户或应用路径不匹配。");
        return backup;
    }
    private void RestoreSnapshot(string name, LoginTaskSnapshot original)
    {
        if (original.Exists) store.Write(name, original.Xml);
        else store.Delete(name);
    }
}

public sealed class LoginStartupController
{
    private readonly ShellController shell;
    private readonly LogonTaskController task;
    public LoginStartupController(AppPaths paths, IUserShellRegistry? registry = null, ILoginTaskStore? tasks = null)
    {
        shell = new ShellController(paths, registry);
        task = new LogonTaskController(paths, tasks ?? new ComLoginTaskStore());
    }
    public bool ShellEnabled => shell.IsEnabled;
    public bool IsEnabled(string sid) => shell.IsEnabled && task.IsEnabled(sid);
    public bool IsStrictEnabled(string sid) => shell.IsEnabled && !task.IsEnabled(sid);
    public bool HasOwnedStartup(string sid) => shell.IsEnabled || task.HasOwnedTask(sid);
    public void Enable(string sid)
    {
        bool shellWasOwned = shell.IsEnabled;
        shell.Enable(sid);
        try { task.Enable(sid); }
        catch (Exception failure)
        {
            try { if (!shellWasOwned && shell.IsEnabled) shell.Disable(sid); }
            catch (Exception rollback) { throw new AggregateException("启用登录动画失败，Shell 回滚也未完成。", failure, rollback); }
            throw;
        }
    }
    public void EnableStrict(string sid)
    {
        bool shellWasOwned = shell.IsEnabled;
        shell.Enable(sid);
        try
        {
            task.Restore(sid);
            if (!IsStrictEnabled(sid)) throw new InvalidOperationException("备用播放任务仍启用，尚未完成桌面前模式切换。");
        }
        catch
        {
            if (!shellWasOwned && shell.IsEnabled) shell.Disable(sid);
            throw;
        }
    }
    public bool Disable(string sid)
    {
        bool taskRestored = false, shellRestored = false;
        Exception? taskFailure = null, shellFailure = null;
        try { taskRestored = task.Restore(sid); } catch (Exception ex) { taskFailure = ex; }
        try { shellRestored = shell.Disable(sid); } catch (Exception ex) { shellFailure = ex; }
        if (taskFailure != null && shellFailure != null)
            throw new AggregateException("登录任务和 Shell 均未完成恢复，请保留备份后重试。", taskFailure, shellFailure);
        if (taskFailure != null)
            throw new InvalidOperationException(shellRestored ? "Shell 已恢复，但登录任务尚未恢复，请保留备份后重试。" : "登录任务尚未恢复；当前 Shell 已不是本应用配置，已保留。", taskFailure);
        if (shellFailure != null)
            throw new InvalidOperationException(taskRestored ? "登录任务已恢复，但 Shell 尚未恢复，请保留备份后重试。" : "Shell 尚未恢复；当前登录任务已不是本应用配置，已保留。", shellFailure);
        return taskRestored || shellRestored;
    }
    public void Deploy(string sourceDirectory) => shell.Deploy(sourceDirectory);
}
