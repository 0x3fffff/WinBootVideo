# Windows 登录后动画：候选项目与播放器基础件

调研日期：2026-10-05。目标阶段是输入密码/PIN 后、Explorer 桌面出现前。仅访问公开资料与代码，没有安装、执行项目或修改系统配置。DARLING-in-the-Windows 由另一份审计报告覆盖。

## 结论

本轮 GitHub 搜索没有发现一批经过长期验证、能够直接满足这个特定阶段的成品。应把“登录顺序控制”与“成熟视频播放后端”分开评价：播放器成熟，并不代表由它构建的启动接管程序已经具备可靠恢复、屏幕切换和 Explorer 交接能力。

对于个人机器、可控本地视频、优先衔接效果的 MVP，建议采用 C# + WPF MediaElement，配合单独的启动协调器与恢复机制；先将素材固定为 Windows 能解码的 MP4，再在目标设备验证。若出现确实的编码兼容或渲染问题，再评估 LibVLCSharp 或 libmpv。该建议是基于下面工程约束作出的选型判断，不是现成项目已经验证的结论。

## 候选及适用性

| 项目 | 实际用途与证据 | 对本需求的判断 |
|---|---|---|
| [xxxman360/LogonVistaOrb](https://github.com/xxxman360/LogonVistaOrb) | .NET/WPF，Windows 10 及以上的 Vista 风格登录动画。61 个 commits，GPL-3.0；[2.1.1 发布](https://github.com/xxxman360/LogonVistaOrb/releases/tag/2.1.1)于 2025-09-10，有 x64/x86 installer，2026-10-05 API 查询为 20 stars。 | 实际动画在 LogonUI 路径中，采用 5 张图片淡入和 WAV 音频；不是可替换 MP4 的 post-PIN/pre-desktop 视频方案。不推荐复用其系统接管机制。 |
| [official-Arvind/WIN-B_M](https://github.com/official-Arvind/WIN-B_M) | README 同时提供 EFI boot logo 修改与 AtLogon 计划任务启动 PowerShell 动画；仓库页面只有 1 个 commit。本轮 GitHub latest release API 返回 404。 | 登录动画是普通登录触发覆盖层，没有先于 Explorer 的顺序保证；也混合了本需求无需的 EFI 改动。没有充分证据认定其成熟。 |
| [mpv-player/mpv](https://github.com/mpv-player/mpv) | 活跃的媒体播放核心，[v0.41.0](https://github.com/mpv-player/mpv/releases/tag/v0.41.0)于 2025-12-21 发布，提供 Windows 构建。支持独立进程、JSON IPC、libmpv C API。 | 成熟播放基础件，适合作为兼容性更强的后端；不自带登录顺序控制和 Explorer 故障恢复。 |
| [videolan/libvlcsharp](https://github.com/videolan/libvlcsharp) | VideoLAN 的 .NET LibVLC 绑定，有 WPF/WinForms 集成和官方 Windows native package；[LibVLCSharp.WPF 3.10.1](https://www.nuget.org/packages/LibVLCSharp.WPF/)及 [VideoLAN.LibVLC.Windows 3.0.24](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/)可在 NuGet 查证。 | 成熟播放基础件。WPF 视频使用独立原生窗口，要专门处理淡出、覆盖内容及窗口交接。 |

## LogonVistaOrb 的代码核查

[MainWindow.xaml.cs](https://github.com/xxxman360/LogonVistaOrb/blob/main/LogonVistaOrb/MainWindow.xaml.cs)限制正式动画由 SYSTEM 账户运行。图片加载后删除 `HKLM\…\Image File Execution Options\LogonUI.exe\Debugger`，随后手动启动 `LogonUI.exe`，播放图片淡入动画，最终等待该进程退出。它没有替换 `Userinit` 或 Winlogon `Shell`。

[LogonVistaOrbInit/Form1.cs](https://github.com/xxxman360/LogonVistaOrb/blob/main/LogonVistaOrbInit/Form1.cs)在 `-shutdown` 分支把 IFEO `Debugger` 重新写为动画 EXE；`-logon` 分支是声音播放。[Installer/Form1.cs](https://github.com/xxxman360/LogonVistaOrb/blob/main/LogonVistaOrbInstaller/Form1.cs)把文件写入 System32、关闭 Fast Startup，并注册 shutdown、lock、logoff、unlock、logon 任务。主窗口源码注释记录 KB5041580 曾使动画完全跳过。这些是登录系统改动与更新兼容风险的具体依据。

## 播放器工程取舍

| 路径 | 优点 | 需要解决的限制 |
|---|---|---|
| WPF MediaElement | 微软文档支持音视频、XAML 布局与播放控制；和 WPF 图片、遮罩、Opacity 动画属于同一 UI 体系，适合视频结束后保留尾帧再过渡。 | 受目标机器 Windows 播放能力约束，应固定素材编码、实际测播放失败与首帧延迟。使用 Manual 播放模式，处理 MediaFailed/MediaEnded；素材按 Content 发布。 |
| LibVLCSharp.WPF | 官方 .NET 绑定与 Windows native package，方便将播放引擎随应用分发。 | 官方专门说明 WPF airspace 问题：原生视频窗口覆盖普通 WPF 内容，VideoView 内的覆盖 UI 通过另一透明窗口实现。因此不能把现有 WPF `Opacity` 淡出直接视作已兼容，需验证整窗渐隐、尾帧、DPI、多屏。 |
| mpv.exe + IPC / libmpv | 外部进程加 Windows named pipe 可把播放器生命周期独立管理；libmpv 是官方推荐的嵌入后端。 | 外部窗口需要控制创建、焦点、全屏和尾帧交接；嵌入 HWND 又增加渲染集成。官方不建议解析终端输出，应使用事件/API；这些仍不解决 Explorer 启动顺序。 |

来源：[微软 WPF Multimedia Overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/multimedia-overview)、[MediaFailed API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.mediaelement.mediafailed)、[VideoLAN WPF airspace 文档](https://github.com/videolan/libvlcsharp/blob/3.x/docs/getting_started.md#wpf)、[mpv 官方手册](https://mpv.io/manual/stable/#using-mpv-from-other-programs-or-scripts)、[mpv client API](https://github.com/mpv-player/mpv/blob/master/include/mpv/client.h)。

## 登录触发和可见性边界

普通 Task Scheduler [LogonTrigger](https://learn.microsoft.com/en-us/windows/win32/taskschd/logontrigger)只定义“用户登录时启动任务”，支持延迟；文档没有承诺排在 Explorer 之前。故“计划任务登录触发 + Topmost 全屏”只能作为覆盖型预览/MVP，不能据此保证桌面从未闪现。这是由 API 保证范围作出的工程判断。

微软 [Run logon scripts synchronously](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-scripts#run_logon_script_sync_2)明确规定等待登录脚本结束后才启动 File Explorer，确实能够控制这个顺序；但它并没有保证任意 GUI 视频在此时立即可见。微软旧版 [Welcome screen 30 秒延迟问题](https://learn.microsoft.com/en-us/troubleshoot/windows-server/user-profiles-and-logon/welcome-screen-displayed-for-30-seconds)显示同步脚本与交互桌面切换曾有时序问题。不能把旧版本现象直接推广到 Windows 11，也不能把这一策略直接当成已验证的平滑视频方案；如采用，需要目标系统实测。

## 搜索结果中的误命中

- [mimiaomc/mpv-login-video](https://github.com/mimiaomc/mpv-login-video)：GDM/Wayland/Linux 登录 session，可参考“播放器结束再启动 shell”的思路，不能安装到 Windows。
- [lzcapp/Windows-BootAnimation](https://github.com/lzcapp/Windows-BootAnimation)：Windows 风格的 Android boot animation 素材，非 Windows 实现。
- [n-src/windows11-oobe-firstlogonanim](https://github.com/n-src/windows11-oobe-firstlogonanim)：归档 Windows 首次账户初始化动画，是首次 OOBE，不是每次登录扩展点；仓库声明保留微软版权。
- [helloyork/AnimeBoot](https://github.com/helloyork/AnimeBoot)：Windows Boot Manager 前的 UEFI PoC，阶段与当前目标不同。
