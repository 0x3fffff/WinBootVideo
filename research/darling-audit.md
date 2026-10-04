# DARLING-in-the-Windows 源码审查

研究日期：2026-10-05（Asia/Shanghai）。审查对象为 GitHub `main` 在查询时的 HEAD `99c458439aa2ba8ab374c2484c5bf0141950b109`，对应 `v1.1.1`。只下载并静态阅读公开源码，没有编译、运行项目，没有改动 Windows 设置。源码副本位于 `research/sources/darling/`。

## 结论

该项目的路线与“输入密码/PIN 后、桌面出现前播放全屏动画”匹配：登录时由当前用户 Shell Wrapper 启动 WPF 遮罩，遮罩显示以后才拉起 Explorer，动画期间后台准备桌面，然后淡出。它不是 UEFI/Windows 引导画面的替换，也不是微软 Windows Shell Launcher 功能。现有源码适合作为个人原型和视觉行为参考；还不足以原样作为强调登录可靠性的成熟产品基础。核心原因是父进程缺少独立超时、播放器/UI 定时器失效时无法可靠解封、所谓壁纸 ready 没有参与实际 reveal 决策，以及多用户/混合 DPI 等边界尚未处理。

## 实际时序与边界

1. `RegisterShell` 仅写入 `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell`，值为项目目录下 `BootShell.exe` 的原始绝对路径；不替换系统 Explorer 文件，也不写 HKLM Shell。会保存用户 Shell 是否存在、原值和已有计划任务状态。已有同名计划任务会被禁用以避免重复播放。[注册代码](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/WallpaperBoot.ps1#L325-L374)
2. `BootShell.exe` 启动系统 Windows PowerShell，传入 `-STA -WindowStyle Hidden -ShellMode`。它等待脚本退出后再检查 Explorer 是否存在；如果脚本启动失败或退出异常，也尝试启动 Explorer。[BootShell](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/BootShell.cs#L8-L118)
3. 脚本先验证配置和视频、加载 WPF 及现场编译的 Win32 互操作代码，再创建黑色、无边框、置顶的全屏窗口。`ContentRendered` 事件中才启动 Explorer，100 ms 定时重试“窗口是否成为前景窗口”；成为前景后播放视频并启动 Wallpaper Engine。[窗口和媒体](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L504-L545)、[播放及启动桌面](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L765-L813)
4. 当前版本没有检查 `LogonUI` 进程，也没有读取 `waitForLogonUiExitSeconds`。虽然 v1.1.0 发布说明写了这项配置，当前实现以遮罩获得前景为播放门槛。应按实际 HEAD 代码评价，不能承诺发布说明中的所有行为仍以相同方式实现。[v1.1.0 元数据](https://api.github.com/repos/Rantkid/DARLING-in-the-Windows/releases/tags/v1.1.0)、[当前播放门槛](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L765-L798)

## 已有工程能力

| 能力 | 核查结果 |
|---|---|
| 可跳过 | 窗口收到 Escape/Space 时触发快速淡出，鼠标跳过可配置。 |
| 音画淡出 | 正常结束与手动跳过有独立时长；视频遮罩透明度和媒体音量使用相同缓动一起淡出。 |
| 视频失败 | `MediaFailed` 直接关闭遮罩；异常情况下脚本 trap/finally 与父进程都尝试启动 Explorer。 |
| 清理恢复 | 正常结束或受控异常会停止定时器、停止媒体、恢复任务栏/图标和被记录的输入法窗口。 |
| 单次播放记录 | 用系统 boot timestamp 加当前 token 的 AuthenticationId 识别一次登录会话，MediaOpened 后记录已播。 |
| 易回退 | `RestoreShell` 恢复原 HKCU Shell；如果此前不存在该值，则删除用户覆盖并依赖原机器 Shell。 |
| 管理脚本 | 有编译、注册/恢复、独立壁纸启动、启动项备份、文件 SHA256 清单、只读标志与快照操作。 |

依据：[跳过与淡出](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L547-L677)、[事件和状态](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L720-L763)、[清理](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L829-L872)、[Shell 恢复](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/WallpaperBoot.ps1#L376-L414)。

## 成熟度限制

### 1. 缺少独立 watchdog，是登录可靠性的首要缺口

`BootShell` 对 PowerShell 使用无上限的 `WaitForExit()`。脚本 `maxDurationSeconds`（示例为 45 秒）的定时器是获得前景、调用 `media.Play()` 后才启动，并运行在同一个 WPF Dispatcher。若脚本卡在前置初始化、窗口一直没有前景、UI 线程阻塞，或者 `startDelayMilliseconds` 的同步 sleep 很长，这个时限无法覆盖。强制杀掉 PowerShell时也不能指望脚本 finally 执行。[父进程等待](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/BootShell.cs#L71-L83)、[子进程定时器](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L765-L798)

建议：父 Shell 为初始化、首帧及总存活时间各设置硬期限；任何失败/超时都启动当前会话的 Explorer、停止自己的播放器，再由独立清理路径恢复被改动的可见性。无需等待播放器同意。把可播放动画和“桌面永远能恢复”作为两条独立责任。

### 2. 壁纸 readiness 目前没有决定 reveal

v1.1.1 将此前结尾同步等待改为播放期间的异步探测：每 200 ms 检查 `wallpaper64/wallpaper32` 进程，随后等固定 `extraRevealDelayMilliseconds`（默认 700 ms）。这是进程存在加延迟，不能证明壁纸首帧已经显示。更关键的是，`$script:wallpaperReady` 仅被探测代码读写，动画结束和提前淡出逻辑不读取它；视频短于壁纸启动时间时，仍直接淡出。这是静态源码可确定的行为。[ready 探测](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L679-L725)、[提前结束计时](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L728-L763)、[最新提交差异](https://github.com/Rantkid/DARLING-in-the-Windows/commit/99c458439aa2ba8ab374c2484c5bf0141950b109)

建议：定义 `OverlayVisible/FirstFramePresented/DesktopAvailable` 握手；壁纸可选且超时后允许普通桌面。对无法提供首帧事件的第三方壁纸，明确采用有界等待的启发式，不能把进程名检测称为可靠 ready。

### 3. 单次播放实际为每次登录会话一次

判定同时要求 `LastBootUpTime` 和 token `AuthenticationId` 相同，才跳过。这样能处理快速启动保留 boot time 但建立新用户登录会话的情况；同时意味着同一次内核 boot 下注销再登录，也会重新播放。锁屏后解锁一般不重建 Shell，当前实现也没有解锁触发器。命名 `playOncePerBoot` 容易使需求理解偏离实际行为。未见独立命名 Mutex；JSON 状态记录也不是并发锁。[状态判定](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L81-L138)、[AuthenticationId 与分支](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L235-L295)

### 4. Explorer 存在不等于当前用户桌面已准备好

父进程和脚本均通过进程名判断是否已有 Explorer，没有检查 SessionId、当前用户、shell 窗口或桌面首帧。多用户切换/RDP 时，其他会话的 Explorer 可能使 fallback 误判无需启动。同样，Wallpaper Engine 进程检查没有会话筛选。[Explorer 检查](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/BootShell.cs#L87-L116)、[Wallpaper 检查](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L346-L370)

### 5. 临时隐藏操作依赖 Explorer 内部窗口结构

代码通过 `Shell_TrayWnd`、`Shell_SecondaryTrayWnd`、`SHELLDLL_DefView` 与 `WorkerW` 查找窗口，再调用 `ShowWindow`。这可避免修改永久桌面设置，但窗口结构可随 Windows 更新变化。它只在创建遮罩前隐藏一次任务栏/图标，ShellMode 下 Explorer 尚未启动时通常没有这些窗口；后续只有输入法窗口有 150 ms watchdog。遮罩置顶负责主要遮挡。恢复通常是强制“显示”，没有保存任务栏/图标先前的可见状态；输入法记录了句柄，恢复时也没有验证句柄是否仍属于原进程。[查找与隐藏](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L374-L515)

建议：默认只靠完全不透明的覆盖窗，尽可能避免隐藏其他进程窗口；确有必要时保存原状态，并由外部清理器恢复。不要把 SHA256 清单和只读文件标志视为防篡改安全边界。

### 6. 多屏是整张虚拟桌面一个窗口

代码用 `VirtualScreenLeft/Top/Width/Height` 覆盖所有屏幕的包围矩形，因此是一个视频跨全部显示器铺展；默认 `UniformToFill` 在拼接比例下会裁切。它没有每屏窗口、各屏独立缩放、monitor hotplug 或显式 DPI change 处理。微软说明 VirtualScreenWidth 是经过 DPI 调整的全部显示器包围矩形。源码不足以证明异分辨率、竖屏、混合 100%/150%/200% DPI、HDR 的正确性，需要实机验证。[窗口布局](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L517-L542)、[微软 VirtualScreenWidth 文档](https://learn.microsoft.com/en-us/dotnet/api/system.windows.systemparameters.virtualscreenwidth?view=windowsdesktop-10.0)

### 7. 系统媒体栈依赖需要明示

播放器是 WPF `MediaElement`，没有内置解码器、codec 检测、视频转码或素材预检。WPF 官方文档描述其使用 Windows 媒体播放能力；不能因为扩展名是 MP4 就保证任意 codec 能播。建议原型统一用 H.264/AAC、普通 SDR MP4，并实测目标机器；更广泛的格式要求可另选 libVLC/mpv，但也要处理播放器初始化和超时，且体积与部署成本更高。[媒体控件](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1#L532-L542)、[WPF Multimedia Overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/multimedia-overview)、[Windows 原生媒体格式](https://learn.microsoft.com/en-us/windows/win32/medfound/supported-media-formats-in-media-foundation)

### 8. 路径与部署恢复仍需强化

Shell 值直接保存可执行文件绝对路径，未在该字段为包含空格的路径加入命令行引号；是否影响具体登录解析，需要在目标 Windows build 验证，不能仅凭源码认定一定失败。代码也未配置固定安装目录、ACL、签名、注册前完整预检、安装更新事务或独立恢复入口。只在首次注册创建备份；项目路径搬移/丢失、媒体异常与 shell 注册是否仍指向有效文件均应纳入验收。[注册与备份](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/WallpaperBoot.ps1#L325-L374)、[恢复](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/WallpaperBoot.ps1#L376-L414)

## 项目状态与许可

- 查询时 GitHub API 返回仓库创建于 2026-07-12、最近代码推送于 2026-08-17；共 7 次提交。发布为 v1.0.0、v1.1.0、v1.1.1。三个 release 均无附加二进制 assets，供用户下载的是 GitHub 生成的源码归档。仓库目录未见测试、CI workflow、安装器或发行签名。这些是成熟度证据，不等同于证明项目无法正常运行。[提交列表](https://api.github.com/repos/Rantkid/DARLING-in-the-Windows/commits?per_page=100)、[发布列表](https://api.github.com/repos/Rantkid/DARLING-in-the-Windows/releases?per_page=100)、[固定版本文件树](https://github.com/Rantkid/DARLING-in-the-Windows/tree/99c458439aa2ba8ab374c2484c5bf0141950b109)
- 许可证为 PolyForm Noncommercial 1.0.0。个人非商业研究/娱乐用途有授权范围；若目标为商业产品或收费分发，需要另行核查/取得授权。视频素材不随源码提供。对于当前个人桌面目标，这不是技术路线阻断点。[LICENSE](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/LICENSE)

## 采用建议

在用户当前家庭版 Windows 11 上，HKCU Winlogon Shell Wrapper 与微软 Enterprise/Education/IoT 的 Shell Launcher 是不同机制，不能因后者不支持家庭版而推断前者一定不可用；也不能将前者说成由官方 Shell Launcher 保障的方案。下一步应在虚拟机/测试账户和用户实际 25H2 build 上验证登录时序，再做有保护的原型。

若选择这一方向，建议自行构建小型 C# Shell supervisor 加播放器，保留 DARLING 的“覆盖窗先显示、Explorer 后启动、动画自然淡出”顺序，同时补上父进程硬超时、会话内 Explorer 检查、首帧 handshake、原状态清理、Mutex、每屏窗口和安装回退。Wallpaper Engine 应作为可选背景准备项，不能成为恢复桌面的必要依赖。
