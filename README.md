# WinBootVideo 0.1.5

Windows 11 的**登录后动画**工具。完成密码 / PIN / 图片密码验证、登录画面退场后，显示全屏视频，再淡出到桌面。Shell 入口优先在桌面出现前覆盖；备用入口可能在桌面刚出现后覆盖播放。每次登录播放一次，锁屏解锁不重复；注销后重新登录会再播放。

这一版用 C# / WPF 编写，提供中文浅色 GUI，可选视频、检查、转码、预览、启用和恢复。初次运行不会更改登录配置。它不替换主板开机 Logo、Windows 引导动画或锁屏画面。

0.1.2 修复视频在不可见桌面上提前播放、登录后只能看到结尾的问题：窗口渲染后先等待自己的桌面成为输入桌面，再从第 0 秒开始播放。输入图片密码或等待登录画面的时间不消耗视频加载与播放预算。新增实际非输入桌面回归及等待后从零播放验证，保留 0.1.1 的保存预览修复。未成功预览时“启用登录动画”保持不可用。

0.1.3 修复冷启动尚未收到遮罩就绪信号时过早终止播放器的问题：桌面不可见时继续等待，桌面可见后保留独立 30 秒冷启动上限；默认 8 秒的回退只请求桌面继续准备。成功请求 Explorer 后，同一宿主的退出或守护路径不会重复请求，避免额外打开文件夹窗口。正常就绪时仍立即播放，不增加固定等待。

0.1.4 修复多屏环境中勾选“播放时遮住其他屏幕”后全屏预览一直黑屏的问题。副屏遮罩现在包含实际的黑色内容，使 WPF 能发出渲染完成事件，所有屏幕覆盖就绪后再播放。此前窗口化测试跳过了副屏遮罩，从这一版开始加入真实双屏全屏回归。

0.1.5 增加当前用户登录任务作为备用入口。启用时同时设置 Shell 与备用任务；备用入口先等待当前会话的桌面就绪，再共用会话去重判断。当 Windows 没有调用 Shell 入口时，备用入口仍能覆盖播放；Shell 已接管同次登录时不会重复播放。新增宿主入口、重复实例、同会话跳过与备用等待日志。此方案提高启动可靠性，但不保证备用播放时桌面完全不闪现。

## 开始使用

1. 打开 `artifacts\WinBootVideo\WinBootVideo.exe`。请保留同目录的全部文件。
2. 选择 / 拖入本地视频。可以先用包内 `demo-login.mp4` 测试。
3. 点击“检查视频”。若缺少 FFmpeg 工具，可以直接预览；或选择同时包含 `ffmpeg.exe`、`ffprobe.exe` 的目录。
4. 需要处理时，指定保留的前几秒，点击“转换并选用”，保存为**新文件**。
5. 设置音量、屏幕和淡出，点击“保存并全屏预览”。按 Esc / 空格可跳过。播放至少前进 100 毫秒后，正常完成或跳过才算成功；损坏、加载失败或超时不算成功。
6. 确认效果后点击“启用登录动画”。它会备份并设置**当前用户**的登录 Shell 和备用登录任务，不保存密码、不请求管理员运行。下次注销 / 重启后登录生效。旧版已有启用时，“修复登录启动”可补上备用入口，原视频验证仍有效。
7. 点击“恢复普通登录”可恢复原来的 Shell 值（包括原本没有该值的情况），并移除本应用的备用任务，下次登录生效。

点击“保存并全屏预览”后，应用会将视频复制到 `%LOCALAPPDATA%\WinBootVideo\media`，成功预览、启用后不依赖原下载目录或原视频文件。**原视频及转码时另存的原位置文件可以删除；只选中了视频、还没有保存时应先保留。** 应用数据目录里的 `media` 副本、`app` 程序、`settings.json` 和恢复备份需要保留，启用期间不要删除 `%LOCALAPPDATA%\WinBootVideo`。重新保存预览会先恢复普通登录，验证新视频后再点击启用；单纯选取或转换文件不会修改已保存的登录动画。

## 视频要求与处理

| 项目 | 推荐 | 处理行为 |
|---|---|---|
| 容器 / 编码 | MP4、H.264 / AVC | GUI 使用 libx264 转码 |
| 像素与色彩 | 8 位 `yuv420p`、SDR | HDR PQ / HLG 识别后色调映射到 SDR |
| 分辨率 / 帧率 | 1920×1080、30 fps | 保留比例缩放，补黑边，转为 30 fps |
| 时长 | 5–15 秒 | 从开头保留指定长度；不提供时间轴编辑 |
| 音频 | AAC、双声道；或无音轨 | 转为 AAC / 48 kHz / 双声道，或移除 |
| 衔接 | 黑色首帧；尾帧接近壁纸 | 保留视频结束画面，整体淡出到桌面 |

这些是推荐条件，实际解码能力依赖当前 Windows 的媒体组件。HEVC、AV1、10 位、HDR、透明视频或特殊封装建议先转换。推荐格式检查**不能代替成功预览**。此程序使用本机 WPF 媒体管线，不自带 mpv / VLC 解码器。

不同屏幕比例会保留视频比例，在黑色背景上显示，初版没有拉伸 / 裁切填满模式。副屏可选择用黑色遮罩覆盖，视频只在所选屏幕播放。显示器拔除后会回退到当前主屏。

本地非空视频最大 2 GB；网络路径 / 网络盘不接受。播放等待上限可设置 5–90 秒，默认 45 秒，**从桌面可见、请求播放时开始计时，包含视频加载、播放、等待桌面和淡出**。视频加载等待可设 2–15 秒，默认 8 秒，同时用于可见后的播放握手。这个时间到了还没有遮罩时，宿主先请求 Explorer，继续保留播放器；只有桌面可见后的独立 30 秒冷启动期限届满仍未就绪，才跳过动画。输入密码、等待登录画面的时间单独计算；超过 15 分钟仍未开始播放会安全跳过本次动画，之后直接进入普通桌面。视频太长会提前退出并判为预览失败，请先剪短。转码长度上限 80 秒。HDR 转码要求 FFmpeg 包含 `zscale`、`tonemap`，较旧或精简版本可能不支持。

典型 SDR 转码命令（GUI 已实现，无需手动执行）：

```powershell
ffmpeg -n -i "input.mp4" -t 15 -map 0:V:0 -map "0:a:0?" -vf "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p" -c:v libx264 -preset medium -crf 20 -profile:v high -level:v 4.1 -c:a aac -b:a 160k -ar 48000 -ac 2 -movflags +faststart "login-compatible.mp4"
```

FFmpeg 为可选的外部工具，按用户选择或 PATH 查找，本包不下载或分发它。FFmpeg 的许可取决于具体构建，见 [FFmpeg 官方许可说明](https://ffmpeg.org/legal.html)；个人准备素材与未来分发含工具的应用包需分别考虑。

## 播放原理

```text
Winlogon 启动当前用户的 WinBootVideo.Host.exe
  → 宿主启动播放器，等待不透明全屏窗口就绪
  → 收到窗口就绪信号后请求本会话的 Explorer；未就绪时到软期限也放行桌面
  → 播放器等待自己的桌面成为当前可见的输入桌面
  → 从第 0 秒加载并播放视频，另发开始信号，播放预算此时开始
  → Explorer 在覆盖层后面准备桌面
  → 保留结束画面，淡出覆盖层，播放器 / 宿主退出
```

“遮罩就绪”指 WPF `ContentRendered` 的**不透明窗口**已渲染，不等于用户已经看到它或第一帧视频已解码。播放器在 UI 线程查询自身桌面的 `GetUserObjectInformation(UOI_IO)`，只有它连续 300 毫秒成为输入桌面才开始播放。宿主使用独立的“遮罩就绪”和“开始播放”事件，分别决定启动 Explorer 与播放超时。查询成功的 WTS 会话状态也须为 Active；启动早期 WTS 不可用时，以 UOI_IO 判断并记录原因。播放中若离开输入桌面，本次动画会退出，后续解锁不续播。

Windows 的 [ARSO](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/component-updates/winlogon-automatic-restart-sign-on--arso-) 可在重启后先自动登录，再保持锁定，因此 Shell 启动可能早于用户完成图片密码解锁。本程序不以 Shell 启动或固定延迟代替可见性判断，也不调整 ARSO 或密码设置。[输入桌面](https://learn.microsoft.com/en-us/windows/win32/winstation/desktops) 是当前可见且接收输入的桌面；[UOI_IO](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectinformationw) 用于查询这个状态，不读取手势、凭据或登录画面内容。

桌面就绪判断另使用当前会话的 `GetShellWindow`，它只能表明 Shell 桌面窗口存在，不保证所有任务栏图标、启动软件或壁纸插件已经加载。

独立宿主使用 .NET Framework 4.8，播放器 / GUI 使用 .NET 8。宿主不依赖播放器的 UI 线程，冷启动硬期限、播放握手或总播放等待超时会终止**自己启动的播放器进程**；Shell 模式还确保已请求 Explorer，备用任务模式沿用已有桌面。播放器另有独立线程，在宿主死亡 / 超过硬期限时自行退出。桌面请求在同一宿主内用锁协调，成功请求只执行一次；仅明确启动异常允许冷却后的有界重试。不结束其他应用、不改任务栏 / 桌面图标可见状态。若冷启动迟到触发桌面回退、遮罩尚未建立，可能短暂看到普通桌面；这条路径优先保证可以正常使用电脑。

备用任务使用当前用户 SID 的 `LogonTrigger` 和 `InteractiveToken`，最低权限运行，不存密码；没有锁屏解锁触发器、固定延迟、空闲或电池限制。任务等待本会话已有桌面后才进入播放流程，等待 30 秒仍无桌面会跳过；备用模式不启动或结束 Explorer。任务调度服务重启也可能再次枚举登录用户，因此两个入口共用下述去重。参见 [LogonTrigger](https://learn.microsoft.com/en-us/windows/win32/taskschd/logontrigger)、[任务安全上下文](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)。

同次登录用 Windows 访问令牌的 AuthenticationId 与会话登录时间组合去重，避免 Shell 与备用任务重复播放，也避免重启后 LUID 被复用而误跳过新登录。会话时间通过只读 [LsaGetLogonSessionData](https://learn.microsoft.com/en-us/windows/win32/api/ntsecapi/nf-ntsecapi-lsagetlogonsessiondata) 获取。锁屏解锁不会重新触发登录入口。尝试标记在播放前写入，同次登录已尝试失败时备用任务也不会强行重播。恢复保留原始 Shell 的存在状态、文本与注册表类型，并核验用户 SID；若其他软件改了 Shell 或同名任务，本工具不会覆盖它。

Shell 登录配置写入位置：

```text
HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell
```

备用任务位于任务计划程序根目录，名称为 `WinBootVideo.Login.<当前用户SID>`，只作用于当前用户。`login-task-backup.json` 保存任务原状态；删除或恢复任务前核验名称、用户与执行命令归属。Shell 与任务的启用失败会回滚本次新增配置。恢复脚本和 GUI 均处理这两项配置，恢复备份须保留。

Windows 11 Home / Pro 的个人桌面采用这一用户级方案；企业策略、不同 Windows 构建、媒体驱动和第三方 Shell 会影响实际体验。已有其他自定义用户 Shell 或用户 Shell 策略时，初版拒绝启用。此方案仍需在你的真实登录中验证；不能保证 Windows 欢迎页与视频之间完全无黑屏，也不能保证预览与冷启动的音频 / GPU 时序一致。

## 如果登录后没有桌面

1. 优先按 Esc / 空格跳过，或等待默认 45 秒。宿主会尝试放行 Explorer。
2. 若仍没有桌面，按 `Ctrl+Shift+Esc` 打开任务管理器；必要时先 `Ctrl+Alt+Del` → 任务管理器。
3. “运行新任务”输入 `explorer.exe`，先进入桌面。
4. 再运行 `%LOCALAPPDATA%\WinBootVideo\恢复普通登录.cmd`。这会使用 .NET Framework 宿主恢复备份，即使 .NET 8 GUI 无法运行也可使用。

也可以在任务管理器运行新任务中执行（把环境变量展开为当前用户真实路径）：

```text
"C:\Users\你的用户名\AppData\Local\WinBootVideo\app\WinBootVideo.Host.exe" --restore --data-dir "C:\Users\你的用户名\AppData\Local\WinBootVideo"
```

备份位于 `shell-backup.json` 与 `login-task-backup.json`，日志位于 `logs\host.log`、`logs\player.log` 与 `logs\playback-timing.log`。若宿主 / 备份被删除，任务管理器中运行 `regedit.exe`，查看上述 **HKCU** 键：仅在 Shell 仍是 WinBootVideo 命令时，按备份恢复；如果备份记录 `exists: false`，删除这个 **Shell 值**，不要删除整个 Winlogon 键。不要更改 HKLM。再在任务计划程序中检查本应用的当前用户任务，只有执行命令仍指向本应用才删除。原本是其他值时必须按原值和类型恢复。设置与备份都是当前用户文件，请保留到完成恢复。

卸载：先在 GUI 恢复普通登录，确认下一次登录正常，再删除 `%LOCALAPPDATA%\WinBootVideo` 和下载包。

## 运行与开发

运行：Windows 11 x64，.NET Framework 4.8、**.NET 8 Desktop Runtime x64**。Windows 11 通常含 Framework 4.8 系列；若 GUI 提示运行时缺失，安装 [微软 .NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)。使用 Desktop Runtime，单独的 .NET Runtime 不含 WPF。FFmpeg 仅检查 / 转码需要。该包为依赖运行时的版本，不是单文件自包含版本。

构建需要 x64 .NET 8 SDK（`global.json` 指定 8.0.422）、内置 Framework C# 编译器；无第三方 NuGet 包，仓库禁用了包源：

```powershell
./build.ps1 -Test
./verify.ps1 -FFmpegDirectory "E:\项目\ffmpeg\bin"
```

`build.ps1 -Test` 验证超时状态、命令行转义、备份恢复、登录任务和素材保存；注册表及配置事务通过模拟对象测试。`verify.ps1` 还进行真实转码、WPF 窗口播放、损坏文件、超时、卡死子进程、宿主死亡及播放器缺失验证。任务回归用 GUID 唯一临时任务，关闭自动登录触发，手动启动独立数据目录的窗口预览，结束后只清理自己的测试任务。另验证任务读取失败时 GUI 仍可恢复自己的 Shell。多屏时运行实际全屏遮罩就绪回归和 GUI 按钮的双屏全屏播放；少于两屏时明确跳过这两项多屏回归。自动播放验证全部用预览模式，不注册 Shell、不注销、不重启。测试播放器会短暂显示窗口，并在双屏 GUI 测试时显示一段静音全屏视频。

开发 / 检查可指定独立数据目录，不影响默认个人配置：

```powershell
.\artifacts\WinBootVideo\WinBootVideo.exe --data-dir "E:\Project\WinBootVideo\.runtime\dev"
.\artifacts\WinBootVideo\WinBootVideo.exe --render-ui "E:\Project\WinBootVideo\artifacts\gui-preview.png" --data-dir "E:\Project\WinBootVideo\.runtime\ui-check"
```

源码结构：`src\WinBootVideo` 是 GUI / 播放器；`src\Host` 是独立宿主；`tests` 是逻辑与集成验证。当前版不包含自动更新、开机前动画、EFI Logo 替换、时间轴编辑、主题素材库或多用户管理员部署。

## 开源参考与许可

实现代码为本项目新编写，使用 MIT 许可；示例视频为本项目原创。仅参考下列项目的架构与行为，没有复制动画素材或受限制的实现代码。`research/sources` 中用于个人调研的第三方源码快照沿用各原项目许可，不属于本项目 MIT 授权，也不放入可运行应用包：

- [DARLING-in-the-Windows](https://github.com/Rantkid/DARLING-in-the-Windows)：WPF 覆盖层、用户 Shell、Explorer 后台启动。它使用 PolyForm Noncommercial，不能将其视为可自由商用的 MIT 项目。
- [AutoBloom](https://github.com/willnode/autobloom)：登录视频、首尾画面衔接等思路，具体源代码与许可见调研。
- [HackBGRT](https://github.com/Metabolix/HackBGRT)：UEFI BGRT 静态 Logo，属于另一个阶段，不纳入此应用。

详细候选项目、许可证与差异见 [调研报告](research/windows-login-animation.md)。WPF 播放行为参考 [微软多媒体概览](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/multimedia-overview)，Shell 窗口判断参考 [GetShellWindow 官方文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getshellwindow)。
