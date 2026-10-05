#if NET8_0_OR_GREATER
#pragma warning disable CS8600, CS8602, CS8603, CS8604, CS8625
#endif

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml;

namespace WinBootVideo.Shared
{
    public sealed class LoginTaskSnapshot
    {
        public bool Exists { get; set; }
        public string Xml { get; set; }
        public LoginTaskSnapshot() { Xml = ""; }
    }

    public interface ILoginTaskStore
    {
        LoginTaskSnapshot Read(string name);
        void Write(string name, string xml);
        void Delete(string name);
    }

    // No shell command, credentials, or dynamic binder is involved in registration.
    public sealed class ComLoginTaskStore : ILoginTaskStore
    {
        private const int FileNotFound = unchecked((int)0x80070002);
        public LoginTaskSnapshot Read(string name)
        {
            object service = OpenService();
            object folder = null;
            object task = null;
            try
            {
                folder = CallResult(service, "GetFolder", new object[] { "\\" });
                try { task = CallResult(folder, "GetTask", new object[] { name }); }
                catch (Exception ex)
                {
                    // The CLR can map this COM HRESULT to FileNotFoundException.
                    if (ex.HResult == FileNotFound) return new LoginTaskSnapshot();
                    throw;
                }
                return new LoginTaskSnapshot { Exists = true, Xml = Convert.ToString(Get(task, "Xml"), CultureInfo.InvariantCulture) ?? "" };
            }
            finally { Release(task); Release(folder); Release(service); }
        }

        public void Write(string name, string xml)
        {
            string sid = LoginTaskDefinition.PrincipalSid(xml);
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null || !string.Equals(identity.User.Value, sid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("登录任务只能注册给当前用户。");
            }
            object service = OpenService();
            object folder = null;
            object task = null;
            try
            {
                folder = CallResult(service, "GetFolder", new object[] { "\\" });
                // CREATE_OR_UPDATE | IGNORE_REGISTRATION_TRIGGERS, interactive token.
                // The caller has already verified ownership before any replacement.
                task = CallResult(folder, "RegisterTask", new object[] { name, xml, 6 | 32, sid, Type.Missing, 3, Type.Missing });
            }
            finally { Release(task); Release(folder); Release(service); }
        }

        public void Delete(string name)
        {
            object service = OpenService();
            object folder = null;
            try
            {
                folder = CallResult(service, "GetFolder", new object[] { "\\" });
                try { Call(folder, "DeleteTask", new object[] { name, 0 }); }
                catch (Exception ex) { if (ex.HResult != FileNotFound) throw; }
            }
            finally { Release(folder); Release(service); }
        }

        private static object OpenService()
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) throw new InvalidOperationException("此系统的任务计划程序不可用。");
            var service = Activator.CreateInstance(type);
            if (service == null) throw new InvalidOperationException("无法连接任务计划程序。");
            try { Call(service, "Connect", new object[0]); return service; }
            catch { Release(service); throw; }
        }
        private static object CallResult(object target, string member, object[] arguments)
        {
            var result = Invoke(target, member, BindingFlags.InvokeMethod, arguments);
            if (result == null) throw new InvalidOperationException("任务计划程序未返回预期结果：" + member);
            return result;
        }
        private static void Call(object target, string member, object[] arguments)
        {
            Invoke(target, member, BindingFlags.InvokeMethod, arguments);
        }
        private static object Get(object target, string member)
        {
            var result = Invoke(target, member, BindingFlags.GetProperty, new object[0]);
            if (result == null) throw new InvalidOperationException("任务计划程序缺少属性：" + member);
            return result;
        }
        private static object Invoke(object target, string member, BindingFlags flags, object[] arguments)
        {
            try { return target.GetType().InvokeMember(member, flags, null, target, arguments, CultureInfo.InvariantCulture); }
            catch (TargetInvocationException ex)
            {
                if (ex.InnerException != null) ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    public static class LoginTaskDefinition
    {
        public const string Namespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        public const string OwnerDescription = "WinBootVideo current-user login fallback; schema=1";
        public static string TaskName(string sid)
        {
            ValidateSid(sid);
            return "WinBootVideo.Login." + sid;
        }
        public static string Create(string hostPath, string dataDirectory, string sid)
        {
            ValidateSid(sid);
            string host = AbsoluteLocalPath(hostPath);
            string data = AbsoluteLocalPath(dataDirectory);
            var document = new XmlDocument();
            var root = document.CreateElement("Task", Namespace);
            root.SetAttribute("version", "1.2"); document.AppendChild(root);
            var registration = Append(root, "RegistrationInfo", null);
            Append(registration, "Description", OwnerDescription);
            var triggers = Append(root, "Triggers", null);
            var logon = Append(triggers, "LogonTrigger", null);
            Append(logon, "Enabled", "true"); Append(logon, "UserId", sid);
            var principals = Append(root, "Principals", null);
            var principal = Append(principals, "Principal", null); principal.SetAttribute("id", "Author");
            Append(principal, "UserId", sid); Append(principal, "LogonType", "InteractiveToken"); Append(principal, "RunLevel", "LeastPrivilege");
            var settings = Append(root, "Settings", null);
            Append(settings, "MultipleInstancesPolicy", "IgnoreNew");
            Append(settings, "DisallowStartIfOnBatteries", "false"); Append(settings, "StopIfGoingOnBatteries", "false");
            Append(settings, "AllowHardTerminate", "true"); Append(settings, "StartWhenAvailable", "false");
            Append(settings, "RunOnlyIfNetworkAvailable", "false");
            var idle = Append(settings, "IdleSettings", null);
            Append(idle, "StopOnIdleEnd", "false"); Append(idle, "RestartOnIdle", "false");
            Append(settings, "AllowStartOnDemand", "true"); Append(settings, "Enabled", "true");
            Append(settings, "Hidden", "false"); Append(settings, "RunOnlyIfIdle", "false"); Append(settings, "WakeToRun", "false");
            Append(settings, "ExecutionTimeLimit", "PT20M"); Append(settings, "Priority", "7");
            var actions = Append(root, "Actions", null); actions.SetAttribute("Context", "Author");
            var execute = Append(actions, "Exec", null);
            Append(execute, "Command", host); Append(execute, "Arguments", Arguments(data));
            Append(execute, "WorkingDirectory", Path.GetDirectoryName(host));
            return document.OuterXml;
        }

        public static bool IsOwned(string xml, string hostPath, string dataDirectory, string sid)
        {
            try
            {
                ValidateSid(sid);
                string host = AbsoluteLocalPath(hostPath), data = AbsoluteLocalPath(dataDirectory);
                var document = Parse(xml); var names = Names(document);
                if (Text(document, names, "t:RegistrationInfo/t:Description") != OwnerDescription) return false;
                if (Count(document, names, "t:Principals/*") != 1 || Count(document, names, "t:Principals/t:Principal") != 1) return false;
                if (!UserMatchesSid(Text(document, names, "t:Principals/t:Principal/t:UserId"), sid)) return false;
                if (Count(document, names, "t:Principals/t:Principal/t:GroupId") != 0) return false;
                if (Text(document, names, "t:Principals/t:Principal/t:LogonType") != "InteractiveToken" || !IsLeastPrivilegeRunLevel(Text(document, names, "t:Principals/t:Principal/t:RunLevel"))) return false;
                if (Count(document, names, "t:Triggers/*") != 1 || Count(document, names, "t:Triggers/t:LogonTrigger") != 1) return false;
                if (!UserMatchesSid(Text(document, names, "t:Triggers/t:LogonTrigger/t:UserId"), sid)) return false;
                if (Count(document, names, "t:Triggers/t:LogonTrigger/t:Repetition") != 0) return false;
                if (Count(document, names, "t:Triggers/t:LogonTrigger/t:StartBoundary") != 0 || Count(document, names, "t:Triggers/t:LogonTrigger/t:EndBoundary") != 0) return false;
                string delay = Text(document, names, "t:Triggers/t:LogonTrigger/t:Delay");
                if (delay.Length != 0 && XmlConvert.ToTimeSpan(delay) != TimeSpan.Zero) return false;
                if (Count(document, names, "t:Actions/*") != 1 || Count(document, names, "t:Actions/t:Exec") != 1) return false;
                if (!Equal(AbsoluteLocalPath(Text(document, names, "t:Actions/t:Exec/t:Command")), host)) return false;
                if (Text(document, names, "t:Actions/t:Exec/t:Arguments") != Arguments(data)) return false;
                if (!Equal(AbsoluteLocalPath(Text(document, names, "t:Actions/t:Exec/t:WorkingDirectory")), Path.GetDirectoryName(host))) return false;
                string instances = Text(document, names, "t:Settings/t:MultipleInstancesPolicy");
                if (instances.Length != 0 && instances != "IgnoreNew") return false;
                string executionLimit = Text(document, names, "t:Settings/t:ExecutionTimeLimit");
                // Missing execution limit means 72 hours, not our 20-minute cap.
                if (executionLimit.Length == 0 || XmlConvert.ToTimeSpan(executionLimit) != TimeSpan.FromMinutes(20)) return false;
                foreach (string setting in new[] { "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries" })
                    if (!Boolean(document, names, "t:Settings/t:" + setting, false, true)) return false;
                foreach (string setting in new[] { "RunOnlyIfIdle", "RunOnlyIfNetworkAvailable", "StartWhenAvailable" })
                    if (!Boolean(document, names, "t:Settings/t:" + setting, false, false)) return false;
                return true;
            }
            catch (XmlException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidDataException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (FormatException) { return false; }
        }

        public static bool IsEnabled(string xml)
        {
            var document = Parse(xml); var names = Names(document);
            return Boolean(document, names, "t:Settings/t:Enabled", true, true) && Boolean(document, names, "t:Triggers/t:LogonTrigger/t:Enabled", true, true);
        }
        public static string PrincipalSid(string xml)
        {
            var document = Parse(xml); var names = Names(document);
            if (Count(document, names, "t:Principals/t:Principal") != 1) throw new InvalidDataException("登录任务必须指定一个用户。");
            if (Count(document, names, "t:Principals/t:Principal/t:GroupId") != 0 || Text(document, names, "t:Principals/t:Principal/t:LogonType") != "InteractiveToken" || !IsLeastPrivilegeRunLevel(Text(document, names, "t:Principals/t:Principal/t:RunLevel")))
                throw new InvalidDataException("登录任务仅使用交互会话和最低权限。");
            string sid = ResolveUserSid(Text(document, names, "t:Principals/t:Principal/t:UserId")); ValidateSid(sid);
            return sid;
        }
        public static bool IsLeastPrivilegeRunLevel(string value) { return string.IsNullOrEmpty(value) || value == "LeastPrivilege"; }
        public static string ResolveUserSid(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) throw new ArgumentException("登录任务必须明确指定用户。", "userId");
            if (userId.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) return new SecurityIdentifier(userId).Value;
            return ((SecurityIdentifier)new NTAccount(userId).Translate(typeof(SecurityIdentifier))).Value;
        }
        public static bool UserMatchesSid(string userId, string sid)
        {
            try { ValidateSid(sid); return Equal(ResolveUserSid(userId), sid); }
            catch (IdentityNotMappedException) { return false; }
            catch (ArgumentException) { return false; }
        }
        private static string Arguments(string data) { return "--startup-fallback --data-dir " + WindowsCommandLine.Quote(data); }
        private static string AbsoluteLocalPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)) throw new InvalidDataException("登录任务路径必须为绝对本地路径。");
            string root = Path.GetPathRoot(value);
            if (root == null || root.Length != 3 || root[1] != ':' || (root[2] != '\\' && root[2] != '/')) throw new InvalidDataException("登录任务不使用相对路径或网络路径。");
            return Path.GetFullPath(value);
        }
        private static void ValidateSid(string sid)
        {
            if (string.IsNullOrWhiteSpace(sid)) throw new ArgumentException("必须指定当前用户 SID。", "sid");
            var parsed = new SecurityIdentifier(sid);
            if (!Equal(parsed.Value, sid)) throw new ArgumentException("用户 SID 格式无效。", "sid");
        }
        private static XmlDocument Parse(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) throw new InvalidDataException("登录任务 XML 为空。");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), settings)) document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.LocalName != "Task" || document.DocumentElement.NamespaceURI != Namespace) throw new InvalidDataException("登录任务 XML 格式无效。");
            return document;
        }
        private static XmlNamespaceManager Names(XmlDocument document)
        {
            var names = new XmlNamespaceManager(document.NameTable); names.AddNamespace("t", Namespace); return names;
        }
        private static string Text(XmlDocument document, XmlNamespaceManager names, string path)
        {
            var node = document.DocumentElement.SelectSingleNode(path, names); return node == null ? "" : node.InnerText;
        }
        private static int Count(XmlDocument document, XmlNamespaceManager names, string path)
        {
            var nodes = document.DocumentElement.SelectNodes(path, names); return nodes == null ? 0 : nodes.Count;
        }
        private static bool Boolean(XmlDocument document, XmlNamespaceManager names, string path, bool expected, bool defaultValue)
        {
            if (Count(document, names, path) == 0) return defaultValue == expected;
            string value = Text(document, names, path); return expected ? value == "true" || value == "1" : value == "false" || value == "0";
        }
        private static bool Equal(string first, string second) { return string.Equals(first, second, StringComparison.OrdinalIgnoreCase); }
        private static XmlElement Append(XmlNode parent, string name, string value)
        {
            var document = parent.OwnerDocument; var element = document.CreateElement(name, Namespace);
            if (value != null) element.InnerText = value;
            parent.AppendChild(element); return element;
        }
    }
}
