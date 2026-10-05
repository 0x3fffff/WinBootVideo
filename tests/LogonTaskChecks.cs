using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using WinBootVideo.Core;
using WinBootVideo.Host;
using WinBootVideo.Shared;

internal static class LogonTaskChecks
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static void RunLogic(Action<bool, string> check)
    {
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-task-logic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        const string sid = "S-1-5-21-1-2-3-1001";
        try
        {
            var paths = new AppPaths(Path.Combine(work, "combined"));
            string host = Path.Combine(paths.AppDirectory, "WinBootVideo.Host.exe");
            string xml = LoginTaskDefinition.Create(host, paths.DataDirectory, sid);
            var definition = XDocument.Parse(xml);
            var principal = definition.Descendants(Ns + "Principal").Single();
            var trigger = definition.Descendants(Ns + "Triggers").Single().Elements().Single();
            check(trigger.Name == Ns + "LogonTrigger" && (string?)trigger.Element(Ns + "UserId") == sid,
                "备用任务只有当前用户 SID 的登录触发器，不由锁屏解锁触发");
            check((string?)principal.Element(Ns + "UserId") == sid && (string?)principal.Element(Ns + "LogonType") == "InteractiveToken"
                && (string?)principal.Element(Ns + "RunLevel") == "LeastPrivilege", "备用任务复用交互用户令牌，不保存密码或要求管理员权限");
            check(!definition.Descendants(Ns + "Repetition").Any() && !definition.Descendants(Ns + "SessionStateChangeTrigger").Any(),
                "备用任务没有重复运行或会话解锁触发器");
            check((string?)definition.Descendants(Ns + "MultipleInstancesPolicy").Single() == "IgnoreNew",
                "调度器拒绝同任务并行重复实例");
            check(XmlConvert.ToTimeSpan((string)definition.Descendants(Ns + "ExecutionTimeLimit").Last()) >= TimeSpan.FromSeconds(992),
                "任务执行上限覆盖十五分钟桌面等待和最长视频预算");
            var action = definition.Descendants(Ns + "Exec").Single();
            check((string?)action.Element(Ns + "Command") == host && (string?)action.Element(Ns + "Arguments") == "--startup-fallback --data-dir " + WindowsCommandLine.Quote(paths.DataDirectory),
                "任务直接执行固定 Host，数据路径通过统一 Windows 引号规则传入");
            check(LoginTaskDefinition.TaskName(sid) == LoginTaskDefinition.TaskName(sid)
                && LoginTaskDefinition.TaskName(sid) != LoginTaskDefinition.TaskName("S-1-5-21-1-2-3-1002"), "备用任务名稳定且按用户隔离");
            check(LoginTaskDefinition.IsOwned(xml, host, paths.DataDirectory, sid), "识别本应用的精确任务定义");
            var disabled = XDocument.Parse(xml);
            SetValue(disabled.Descendants(Ns + "Settings").Single(), "Enabled", "false");
            check(LoginTaskDefinition.IsOwned(disabled.ToString(), host, paths.DataDirectory, sid), "已禁用的本应用任务仍可识别和安全恢复");
            var foreignAction = XDocument.Parse(xml);
            foreignAction.Descendants(Ns + "Command").Single().Value = Path.Combine(work, "different-program.exe");
            check(!LoginTaskDefinition.IsOwned(foreignAction.ToString(), host, paths.DataDirectory, sid), "不把同名其他程序任务当作本应用任务");
            var extraAction = XDocument.Parse(xml);
            extraAction.Descendants(Ns + "Actions").Single().Add(new XElement(action));
            check(!LoginTaskDefinition.IsOwned(extraAction.ToString(), host, paths.DataDirectory, sid), "额外执行动作不能混入本应用任务");

            var registry = new FakeRegistry();
            var tasks = new FakeTaskStore();
            string name = LoginTaskDefinition.TaskName(sid);
            tasks.BeforeWrite = (_, _) => check(File.Exists(paths.BackupFile)
                && File.Exists(Path.Combine(paths.DataDirectory, "login-task-backup.json")), "Shell 与任务备份均先落盘，再注册备用任务");
            var controller = new LoginStartupController(paths, registry, tasks);
            controller.Enable(sid);
            check(controller.ShellEnabled && controller.IsEnabled(sid) && controller.HasOwnedStartup(sid), "启用同时设置当前用户 Shell 和登录备用任务");
            string shellBackup = File.ReadAllText(paths.BackupFile);
            string taskBackup = File.ReadAllText(Path.Combine(paths.DataDirectory, "login-task-backup.json"));
            int writes = tasks.WriteCount;
            controller.Enable(sid);
            check(tasks.WriteCount == writes && File.ReadAllText(paths.BackupFile) == shellBackup
                && File.ReadAllText(Path.Combine(paths.DataDirectory, "login-task-backup.json")) == taskBackup, "重复启用不重写任务或覆盖原始恢复备份");
            var disabledOwnedTask = XDocument.Parse(tasks.Read(name).Xml);
            SetValue(disabledOwnedTask.Descendants(Ns + "Settings").Single(), "Enabled", "false");
            tasks.Items[name] = new LoginTaskSnapshot { Exists = true, Xml = disabledOwnedTask.ToString() };
            check(!controller.IsEnabled(sid) && controller.HasOwnedStartup(sid), "自己的任务被禁用时显示待修复状态，仍可安全恢复");
            controller.Enable(sid);
            check(controller.IsEnabled(sid) && File.ReadAllText(Path.Combine(paths.DataDirectory, "login-task-backup.json")) == taskBackup,
                "重新启用自己的禁用任务时保留最初恢复备份");
            ExpectFailure(() => controller.Disable("S-1-5-21-1-2-3-1002"), check, "错误用户不能恢复 Shell 或任务");
            check(controller.IsEnabled(sid), "错误用户恢复失败后保留已启用配置");
            check(controller.Disable(sid) && !registry.Current.Exists && !tasks.Read(name).Exists, "恢复同时还原 Shell 并删除原本不存在的备用任务");

            var rollbackPaths = new AppPaths(Path.Combine(work, "rollback"));
            var rollbackRegistry = new FakeRegistry();
            var rollbackTasks = new FakeTaskStore { ThrowAfterNextWrite = true };
            var rollback = new LoginStartupController(rollbackPaths, rollbackRegistry, rollbackTasks);
            ExpectFailure(() => rollback.Enable(sid), check, "任务写入后出错时启用整体失败");
            check(!rollbackRegistry.Current.Exists && !rollbackTasks.Read(name).Exists,
                "任务注册失败回滚本次新增 Shell 与自己的部分写入任务");

            var legacyPaths = new AppPaths(Path.Combine(work, "legacy"));
            var legacyRegistry = new FakeRegistry();
            var legacyShell = new ShellController(legacyPaths, legacyRegistry);
            legacyShell.Enable(sid);
            var legacyTasks = new FakeTaskStore { ThrowAfterNextWrite = true };
            var legacy = new LoginStartupController(legacyPaths, legacyRegistry, legacyTasks);
            ExpectFailure(() => legacy.Enable(sid), check, "旧版已启用 Shell 添加任务失败会报告错误");
            check(legacyShell.IsEnabled && !legacyTasks.Read(name).Exists, "迁移任务失败保留此前正常的旧版 Shell");

            var protectedPaths = new AppPaths(Path.Combine(work, "protected"));
            var protectedRegistry = new FakeRegistry();
            var protectedTasks = new FakeTaskStore();
            var protectedController = new LoginStartupController(protectedPaths, protectedRegistry, protectedTasks);
            protectedController.Enable(sid);
            string replacement = LoginTaskDefinition.Create(Path.Combine(work, "another.exe"), protectedPaths.DataDirectory, sid);
            protectedTasks.Items[name] = new LoginTaskSnapshot { Exists = true, Xml = replacement };
            protectedController.Disable(sid);
            check(protectedTasks.Read(name).Xml == replacement && !protectedRegistry.Current.Exists, "恢复不删除别人后来替换的同名任务，仍恢复自己的 Shell");

            var collisionPaths = new AppPaths(Path.Combine(work, "collision"));
            var collisionRegistry = new FakeRegistry();
            var collisionTasks = new FakeTaskStore();
            collisionTasks.Items[name] = new LoginTaskSnapshot { Exists = true, Xml = replacement };
            var collision = new LoginStartupController(collisionPaths, collisionRegistry, collisionTasks);
            ExpectFailure(() => collision.Enable(sid), check, "同名非本应用任务阻止启用，拒绝覆盖");
            check(!collisionRegistry.Current.Exists && collisionTasks.Read(name).Xml == replacement,
                "同名任务冲突不留下新 Shell 或覆盖外部任务");
            var partialPaths = new AppPaths(Path.Combine(work, "partial-restore"));
            var partialRegistry = new FakeRegistry();
            var partialTasks = new FakeTaskStore();
            var partial = new LoginStartupController(partialPaths, partialRegistry, partialTasks);
            partial.Enable(sid);
            File.WriteAllText(Path.Combine(partialPaths.DataDirectory, "login-task-backup.json"), "corrupt backup");
            ExpectFailure(() => partial.Disable(sid), check, "任务备份损坏时报告未完成的部分恢复");
            check(!partialRegistry.Current.Exists && partialTasks.Read(name).Exists,
                "任务备份损坏仍恢复自己的 Shell，保留不能安全还原的任务和备份");
            var tamperedPaths = new AppPaths(Path.Combine(work, "tampered-original"));
            var tamperedRegistry = new FakeRegistry();
            var tamperedTasks = new FakeTaskStore();
            var tampered = new LoginStartupController(tamperedPaths, tamperedRegistry, tamperedTasks);
            tampered.Enable(sid);
            string tamperedBackupPath = Path.Combine(tamperedPaths.DataDirectory, "login-task-backup.json");
            var tamperedBackup = JsonSerializer.Deserialize<LogonTaskBackup>(File.ReadAllText(tamperedBackupPath), Settings.JsonOptions)!;
            tamperedBackup.Original = new LoginTaskSnapshot { Exists = true, Xml = replacement };
            AtomicJson.Write(tamperedBackupPath, tamperedBackup);
            string beforeTamperedRestore = tamperedTasks.Read(name).Xml;
            int writesBeforeTamperedRestore = tamperedTasks.WriteCount;
            ExpectFailure(() => tampered.Disable(sid), check, "原始任务备份被篡改为外部动作时拒绝恢复该动作");
            check(!tamperedRegistry.Current.Exists && tamperedTasks.Read(name).Xml == beforeTamperedRestore
                && tamperedTasks.WriteCount == writesBeforeTamperedRestore,
                "篡改备份不覆盖当前自己的任务或执行外部动作，仍恢复自己的 Shell");
            RunFallbackGate(check);
            RunAttemptGate(Path.Combine(work, "attempt-gate"), check);
        }
        finally { Directory.Delete(work, true); }
    }

    private static void RunFallbackGate(Action<bool, string> check)
    {
        long elapsed = 0;
        int delays = 0;
        var trace = new List<string>();
        var disabled = FallbackStartupGate.WaitUntilDesktop(() => false, () => false, () => elapsed,
            () => { delays++; elapsed += 1000; }, trace.Add);
        check(disabled == FallbackStartupResult.Disabled && delays == 0,
            "备用入口在用户已恢复普通登录时立即退出，不等待桌面或争抢播放标记");
        elapsed = 0; delays = 0; trace.Clear();
        var timeout = FallbackStartupGate.WaitUntilDesktop(() => true, () => false, () => elapsed,
            () => { delays++; elapsed += 1000; }, trace.Add);
        check(timeout == FallbackStartupResult.TimedOut && elapsed == 30000 && delays == 30,
            "备用入口只等待已有桌面三十秒，不自行启动 Explorer");
        elapsed = 0; delays = 0; trace.Clear();
        var ready = FallbackStartupGate.WaitUntilDesktop(() => true, () => elapsed >= 3000, () => elapsed,
            () => { delays++; elapsed += 1000; }, trace.Add);
        check(ready == FallbackStartupResult.Ready && elapsed == 3000 && delays == 3,
            "现有桌面就绪后备用入口才允许进入统一播放守卫");
        elapsed = 0; delays = 0; trace.Clear();
        var revoked = FallbackStartupGate.WaitUntilDesktop(() => elapsed < 2000, () => elapsed >= 3000, () => elapsed,
            () => { delays++; elapsed += 1000; }, trace.Add);
        check(revoked == FallbackStartupResult.Disabled && elapsed == 2000,
            "等待已有桌面期间用户停用动画会取消备用入口");
    }

    private static void RunAttemptGate(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        string session = "WinBootVideoTest_" + Guid.NewGuid().ToString("N");
        string stamp = Path.Combine(directory, "last-logon.txt");
        int runs = 0, already = 0;
        var trace = new List<string>();
        int first = LoginAttemptGate.Run(session, stamp, () => { runs++; return 17; }, () => already++, trace.Add);
        check(first == 17 && runs == 1 && File.ReadAllText(stamp) == session,
            "生产登录守卫在播放前记录当前会话，原样返回播放流程退出码");
        int repeated = LoginAttemptGate.Run(session, stamp, () => { runs++; return 99; }, () => already++, trace.Add);
        check(repeated == 0 && runs == 1 && already == 1 && trace.Any(x => x.Contains("same-session-skipped")),
            "Shell 和备用任务同会话再次到达时跳过播放并记录诊断");
        LoginAttemptGate.Run(session + "_next", stamp, () => { runs++; return 0; }, () => already++, trace.Add);
        check(runs == 2 && already == 1 && File.ReadAllText(stamp) == session + "_next",
            "新的实际登录会话不会被上一会话标记抑制");

        string parallelSession = "WinBootVideoTest_" + Guid.NewGuid().ToString("N");
        string parallelStamp = Path.Combine(directory, "parallel-logon.txt");
        using var entered = new ManualResetEvent(false);
        using var finish = new ManualResetEvent(false);
        int parallelRuns = 0, parallelAlready = 0;
        var secondTrace = new List<string>();
        var firstProcess = Task.Run(() => LoginAttemptGate.Run(parallelSession, parallelStamp, () =>
        {
            Interlocked.Increment(ref parallelRuns);
            entered.Set();
            if (!finish.WaitOne(5000)) throw new TimeoutException("Test-only gate callback was not released.");
            return 0;
        }, () => Interlocked.Increment(ref parallelAlready), _ => { }));
        try
        {
            check(entered.WaitOne(2000), "并发回归的第一条实际生产守卫已取得唯一命名 mutex");
            int second = LoginAttemptGate.Run(parallelSession, parallelStamp,
                () => { Interlocked.Increment(ref parallelRuns); return 99; },
                () => Interlocked.Increment(ref parallelAlready), secondTrace.Add);
            check(second == 0 && parallelRuns == 1 && parallelAlready == 0 && secondTrace.Any(x => x.Contains("duplicate-mutex")),
                "并发第二入口只记录重复 mutex，不抢先播放或调用桌面回调");
        }
        finally { finish.Set(); firstProcess.GetAwaiter().GetResult(); }

        string failedSession = "WinBootVideoTest_" + Guid.NewGuid().ToString("N");
        string failedStamp = Path.Combine(directory, "failed-attempt.txt");
        ExpectFailure(() => LoginAttemptGate.Run(failedSession, failedStamp,
            () => throw new IOException("Injected own playback failure."), () => { }, _ => { }), check,
            "生产守卫保留播放错误，由 Host 的异常路径负责报告");
        bool retried = false;
        var failedTrace = new List<string>();
        LoginAttemptGate.Run(failedSession, failedStamp, () => { retried = true; return 0; }, () => { }, failedTrace.Add);
        check(!retried && failedTrace.Any(x => x.Contains("same-session-skipped")),
            "播放失败后的第二入口仍记录已尝试，不在一次登录中重复动画");
    }

    public static async Task RunCom(string bundle, Action<bool, string> check)
    {
        bundle = Path.GetFullPath(bundle);
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-com-task-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string name = "WinBootVideo.Test." + Guid.NewGuid().ToString("N");
        var paths = new AppPaths(work);
        dynamic? service = null, folder = null, registered = null, running = null, taskDefinition = null;
        var productionStore = new ComLoginTaskStore();
        bool createdTask = false;
        try
        {
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            string video = Path.Combine(bundle, "demo-login.mp4");
            string hash = await SettingsStore.HashAsync(video, CancellationToken.None);
            new SettingsStore(paths).Write(new Settings
            {
                VideoPath = video, VideoSha256 = hash, Volume = 0, FadeOutMilliseconds = 100,
                CoverOtherScreens = false, StartupTimeoutSeconds = 5, MaxDurationSeconds = 15
            });
            string receipt = Path.Combine(work, "scheduled-preview-receipt.json");
            string host = Path.Combine(bundle, "WinBootVideo.Host.exe");
            var definition = XDocument.Parse(LoginTaskDefinition.Create(host, work, sid));
            SetValue(definition.Descendants(Ns + "LogonTrigger").Single(), "Enabled", "false");
            var uri = definition.Descendants(Ns + "URI").FirstOrDefault();
            if (uri != null) uri.Value = "\\" + name;
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!);
            service!.Connect();
            folder = service.GetFolder("\\");
            check(!TaskExists(folder!, name), "实际 COM 回归任务名为独立 GUID，原先不存在");
            check(!productionStore.Read(name).Exists, "生产反射 COM 适配器将不存在任务的原生 HRESULT 识别为缺失");
            taskDefinition = service.NewTask(0);
            taskDefinition.XmlText = definition.ToString();
            // TASK_CREATE only: even an unexpected collision cannot overwrite a task.
            registered = folder!.RegisterTaskDefinition(name, taskDefinition, 2, sid, null, 3, null);
            createdTask = true;
            var captured = XDocument.Parse((string)registered!.Xml);
            var capturedPrincipal = captured.Descendants(Ns + "Principal").Single();
            var capturedTrigger = captured.Descendants(Ns + "LogonTrigger").Single();
            string? runLevel = (string?)capturedPrincipal.Element(Ns + "RunLevel");
            Console.WriteLine("Registered preview task identity: principal=" + (string?)capturedPrincipal.Element(Ns + "UserId")
                + " logon=" + (string?)capturedPrincipal.Element(Ns + "LogonType") + " runLevel=" + (runLevel ?? "<schema-default:LeastPrivilege>")
                + " triggerUser=" + (string?)capturedTrigger.Element(Ns + "UserId") + " triggerEnabled=" + (string?)capturedTrigger.Element(Ns + "Enabled"));
            check(captured.Descendants(Ns + "Triggers").Single().Elements().Count() == 1
                && (string?)capturedTrigger.Element(Ns + "Enabled") == "false"
                && IsSameUser((string?)capturedTrigger.Element(Ns + "UserId"), sid)
                && IsSameUser((string?)capturedPrincipal.Element(Ns + "UserId"), sid)
                && (string?)capturedPrincipal.Element(Ns + "LogonType") == "InteractiveToken"
                && (runLevel == null || runLevel == "LeastPrivilege"), "真实注册任务保留单用户交互令牌和最低权限，自动触发已禁用");
            Console.WriteLine("Registered original task XML: " + captured.ToString(SaveOptions.DisableFormatting));
            check(LoginTaskDefinition.IsOwned(captured.ToString(), host, work, sid),
                "生产所有权校验正确识别调度器规范化后的原始任务定义，初始禁用任务从未运行");
            var productionRead = productionStore.Read(name);
            check(productionRead.Exists && XDocument.Parse(productionRead.Xml).Descendants(Ns + "Exec").Single().Element(Ns + "Command")!.Value == host,
                "生产反射 COM 适配器读取自己的唯一预览测试任务");
            // Only after its disabled production definition has passed the real
            // registration round-trip do we replace this test's action with a
            // safe preview and manually run it. The login action never runs.
            var previewDefinition = new XDocument(definition);
            var previewExec = previewDefinition.Descendants(Ns + "Exec").Single();
            previewExec.Element(Ns + "Arguments")!.Value = "--preview --windowed --startup-fallback --config " + WindowsCommandLine.Quote(paths.SettingsFile)
                + " --data-dir " + WindowsCommandLine.Quote(work) + " --receipt " + WindowsCommandLine.Quote(receipt);
            SetValue(previewDefinition.Descendants(Ns + "Settings").Single(), "ExecutionTimeLimit", "PT45S");
            productionStore.Write(name, previewDefinition.ToString());
            var productionWritten = XDocument.Parse(productionStore.Read(name).Xml);
            check(productionWritten.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "Enabled")!.Value == "false"
                && productionWritten.Descendants(Ns + "Exec").Single().Element(Ns + "Arguments")!.Value == previewExec.Element(Ns + "Arguments")!.Value,
                "生产反射 COM 适配器写入自己的预览测试任务，禁用自动触发且保留独立数据路径");
            Release(registered);
            registered = folder.GetTask(name);
            running = registered.Run(null);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < 30)
            {
                if (File.Exists(receipt) && (int)registered.State != 4) break;
                await Task.Delay(100);
            }
            string logFile = Path.Combine(paths.LogDirectory, "host.log");
            string trace = File.Exists(logFile) ? File.ReadAllText(logFile) : "";
            Console.WriteLine("Scheduled preview Host evidence: " + Environment.NewLine + trace.TrimEnd());
            var report = File.Exists(receipt) ? JsonSerializer.Deserialize<WinBootVideo.PreviewReceipt>(File.ReadAllText(receipt), Settings.JsonOptions) : null;
            check(report?.Succeeded == true && report.Opened && report.PlaybackAdvanced && report.VideoSha256 == hash,
                "任务调度器实际启动 Host 和 WPF 子进程，成功解码播放并产生独立收据");
            check((int)registered.State != 4 && (int)registered.LastTaskResult == 0,
                "任务调度器确认本测试 Host 完成且退出码为零");
            check(trace.Contains("host-entry", StringComparison.Ordinal) && trace.Contains("player-start:", StringComparison.Ordinal),
                "真实任务启动记录 Host 入口和播放器启动，启动链不再无声退出");
            check(!File.Exists(Path.Combine(work, "last-logon.txt")), "实际 COM 预览未创建或修改登录去重标记");
        }
        finally
        {
            if (running != null)
            {
                try { running.Stop(); } catch (COMException) { }
            }
            if (folder != null && createdTask)
            {
                if (TaskExists(folder, name)) productionStore.Delete(name);
                productionStore.Delete(name); // Missing task must be an idempotent no-op.
                check(!TaskExists(folder, name) && !productionStore.Read(name).Exists,
                    "生产适配器最后只删除自己的 GUID 任务，再删除缺失任务仍安全幂等");
            }
            Release(running); Release(registered); Release(taskDefinition); Release(folder); Release(service);
            // Stop owns this task's Host; its existing parent guardian cleans the
            // child. Wait briefly for those handles to close before deleting data.
            await Task.Delay(300);
            Directory.Delete(work, true);
        }
    }

    private static bool TaskExists(dynamic folder, string name)
    {
        try { object task = folder.GetTask(name); Release(task); return true; }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return false; }
    }
    private static bool IsSameUser(string? user, string sid)
    {
        if (string.IsNullOrWhiteSpace(user)) return false;
        if (string.Equals(user, sid, StringComparison.OrdinalIgnoreCase)) return true;
        try { return string.Equals(((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value, sid, StringComparison.OrdinalIgnoreCase); }
        catch (IdentityNotMappedException) { return false; }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    private static void SetValue(XElement parent, string name, string value)
    {
        var element = parent.Element(Ns + name);
        if (element != null) element.Value = value;
        else parent.AddFirst(new XElement(Ns + name, value));
    }
    private static void ExpectFailure(Action action, Action<bool, string> check, string label)
    {
        bool failed = false;
        try { action(); } catch { failed = true; }
        check(failed, label);
    }
}

internal sealed class FakeTaskStore : ILoginTaskStore
{
    public readonly Dictionary<string, LoginTaskSnapshot> Items = new(StringComparer.OrdinalIgnoreCase);
    public Action<string, string>? BeforeWrite;
    public bool ThrowAfterNextWrite;
    public bool ThrowOnRead;
    public int WriteCount;
    public LoginTaskSnapshot Read(string name)
    {
        if (ThrowOnRead) throw new IOException("测试：登录任务服务不可读取。");
        return Items.TryGetValue(name, out var item)
            ? new LoginTaskSnapshot { Exists = item.Exists, Xml = item.Xml }
            : new LoginTaskSnapshot();
    }
    public void Write(string name, string xml)
    {
        BeforeWrite?.Invoke(name, xml);
        Items[name] = new LoginTaskSnapshot { Exists = true, Xml = xml };
        WriteCount++;
        if (ThrowAfterNextWrite) { ThrowAfterNextWrite = false; throw new IOException("Injected task registration failure after write."); }
    }
    public void Delete(string name) => Items.Remove(name);
}
