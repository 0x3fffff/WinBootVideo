# WinBootVideo 0.2.0

Windows 11 的**登录后动画**工具。完成密码 / PIN / 图片密码验证、登录画面退场后，显示全屏视频，再淡出到桌面。0.1.6 采用桌面前模式：视频呈现后才允许本宿主启动 Explorer，覆盖层保持到动画淡出；不使用桌面出现后再播放的备用入口。每次登录播放一次，锁屏解锁不重复；注销后重新登录会再播放。

这一版用 C# / WPF 编写，提供中文浅色 GUI，可选视频、检查、转码、预览、启用和恢复。初次运行不会更改登录配置。新增独立的“开机 Logo”向导，使用固定版本 HackBGRT 更换 UEFI 启动 Windows 时的静态 BGRT 图片；不刷主板固件，不替换 Windows 转圈动画或锁屏画面。

## 开机 Logo（0.2.0 初版）

在左侧点击“开机 Logo”，按“选图 → 导出准备包 → 管理员只读检查 → 恢复确认 → 安装”操作。登录视频与开机 Logo 的启用和恢复分别管理。普通界面和登录播放器继续使用普通权限；Logo 的检查、安装和恢复在单独辅助进程中申请 UAC。

初版仅支持已经处于 UEFI x64、系统盘完全未加密、一个 EFI 系统分区且与 Windows 位于同一磁盘的环境；固件必须只有当前默认 Windows 启动项，不存在另一个系统、旧 HackBGRT 或不明额外项，也没有待执行的 BootNext。未知、加密（即使保护已暂停）、多分区或无法核实的配置会阻止安装。**不要为了绕过检查解密磁盘、删除其他启动项或关闭安全功能。**

软件内置官方 [HackBGRT 2.6.0](https://github.com/Metabolix/HackBGRT/releases/tag/v2.6.0) ZIP，SHA-256 固定为 `6204911d777ac03e514b90126568ef6faa1d477779b31c536b142dba92f4a2c0`。管理员辅助进程重新检查硬件，将原始启动项、启动顺序及 Windows 启动器备份写入受管理员保护的 `%ProgramData%\WinBootVideo-BootLogo`，然后调用官方**仅安装文件**入口，核对全部六个 EFI 文件、图片像素和尺寸，再注册独立项到启动顺序末尾。原 Windows 启动项及启动器文件不写入，Windows Boot Manager 保持默认。未通过校验不会启用；失败尝试移除自己的项，撤销失败会明确报错并保留原始恢复记录。不会自动重启、修改 BCD、调用原版覆盖引导器选项，或整体导入旧备份。

### 图片与转换

EFI 分区必须有至少 20 MB 可用空间。空间不足或读不到时停止；软件不会删除其他 EFI 文件腾空间。

| 项目 | 初版要求 / 处理 |
|---|---|
| 输入 | 本地静态 PNG、JPEG、BMP；最大 16 MB、2000 万像素、每边最多 8192 |
| 推荐画布 | 320×180 或 256×256；最大 600×360，图案居中、背景黑 |
| 转换 | 保留比例缩放，透明像素合成到黑底，补黑边 |
| 输出 | 24 位未压缩 BMP3，54 字节头、正向高度，四字节行对齐 |
| 固件显示 | 保留原固件分辨率，图像居中偏上；桌面预览仅作示意 |

转换不需要 FFmpeg、Paint 或网络。导出后的 `splash.bmp`、`config.txt` 和恢复说明都是独立副本，删除原始图片不影响准备包。安装后还有 EFI 中的副本；保留准备包和恢复记录。主板通电后最早出现的厂商 Logo 可能仍短暂可见，HackBGRT 的 BGRT 替换从它被启动时才生效。

### BIOS / UEFI 与首次验证

1. 先准备可启动的 Windows 恢复 U 盘，把恢复说明保存到另一设备，确认现有帐户密码可用。GUI 不代替用户格式化 U 盘。
2. 保持目前 UEFI、CSM、SATA、TPM 设置，不清空安全启动密钥；不要切换 Legacy 模式。进入固件可按微软的 [Windows 11 固件入口说明](https://support.microsoft.com/en-us/windows/security/devicesecurity/windows-11-and-secure-boot)，从“设置 → 系统 → 恢复 → 高级启动 → 疑难解答 → 高级选项 → UEFI 固件设置”手动重启进入。先保存工作，软件不会自动重启。
3. 安装后查电脑厂商官方手册中的启动菜单键，**单次选择 HackBGRT**。此时 Windows Boot Manager 仍是默认。
4. Secure Boot 已开启时保留它。根据官方 [shim 指引](https://github.com/Metabolix/HackBGRT/blob/v2.6.0/shim.md)，首次可能需在 MOKManager 中 `Enroll hash from disk → EFI → HackBGRT → grubx64.efi`。选择文件哈希，避免信任整张证书。MOK 重启后需再次单次选择 HackBGRT。若固件拒绝 shim，返回 Windows Boot Manager 并停止；本软件不提供关闭安全功能的绕过方式。
5. 实际看到 Logo 且 Windows 登录正常后，才在固件 Boot Priority / Boot Order 中手动把 HackBGRT 放到第一位。先以“重启”验证；快速启动可能影响关机后开机的表现。

启动链变化可能影响 Windows Hello / PIN 和依赖 TPM 的反作弊。初版阻止加密系统的安装，不读取恢复密钥。请结合微软的 [BitLocker 恢复说明](https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview) 了解启动链变化的影响，不能把“已暂停 BitLocker”误认为“未加密”。

### Logo 恢复与验证边界

试启动失败时，从固件启动菜单选择 Windows Boot Manager，进入 Windows 后点击“恢复原 Logo”。软件核实受保护的记录与当前项，只移除自己的 BootOrder 引用及独立项；保留 EFI Logo 文件，避免残留固件引用变为悬空文件路径。后续可凭相同恢复记录和文件哈希重新准备安装。外来项、文件变化、Windows 启动器变化或损坏记录会停止自动操作，不能拿旧备份覆盖当前 Windows。恢复记录与文件都仍可查阅。

**此功能没有真实或虚拟 UEFI 首次启动验收，不能宣称所有固件兼容或绝无启动风险。** 自动检查覆盖模拟固件事务、失败撤销、BMP 字节与像素、GUI 按钮、辅助 CLI 拒绝路径，以及官方安装器在临时模拟 ESP 中的实际文件复制。开发过程中不改真实 EFI、NVRAM、BCD、Secure Boot、TPM 或加密设置，也不重启电脑。详细证据和限制见 [QA.md](QA.md) 与 [Logo 技术记录](research/boot-logo-integration.md)。

0.1.2 修复视频在不可见桌面上提前播放、登录后只能看到结尾的问题：窗口渲染后先等待自己的桌面成为输入桌面，再从第 0 秒开始播放。输入图片密码或等待登录画面的时间不消耗视频加载与播放预算。新增实际非输入桌面回归及等待后从零播放验证，保留 0.1.1 的保存预览修复。未成功预览时“启用登录动画”保持不可用。

0.1.3 修复冷启动尚未收到遮罩就绪信号时过早终止播放器的问题：桌面不可见时继续等待，桌面可见后保留独立 30 秒冷启动上限；默认 8 秒的回退只请求桌面继续准备。成功请求 Explorer 后，同一宿主的退出或守护路径不会重复请求，避免额外打开文件夹窗口。正常就绪时仍立即播放，不增加固定等待。

0.1.4 修复多屏环境中勾选“播放时遮住其他屏幕”后全屏预览一直黑屏的问题。副屏遮罩现在包含实际的黑色内容，使 WPF 能发出渲染完成事件，所有屏幕覆盖就绪后再播放。此前窗口化测试跳过了副屏遮罩，从这一版开始加入真实双屏全屏回归。

0.1.5 增加当前用户登录任务作为备用入口。启用时同时设置 Shell 与备用任务；备用入口先等待当前会话的桌面就绪，再共用会话去重判断。当 Windows 没有调用 Shell 入口时，备用入口仍能覆盖播放；Shell 已接管同次登录时不会重复播放。新增宿主入口、重复实例、同会话跳过与备用等待日志。此方案提高启动可靠性，但不保证备用播放时桌面完全不闪现。

0.1.6 按桌面前播放的要求改正交接顺序。只有黑色遮罩渲染完成不能放行 Explorer：播放器须先等待输入桌面、解码视频、确认播放进度和画面合成，再发送独立 FirstFrame 信号；宿主收到后才启动 Explorer。迁移会按归属和原备份移除 0.1.5 的备用播放任务，旧备用命令也不会启动播放器。动画无法初始化、加载或呈现时，结束动画后进入普通桌面，不保留晚到的播放。登录入口本身已晚于可见桌面时跳过动画，不隐藏或结束已有 Explorer 来伪造顺序。

## 开始使用

1. 打开 `artifacts\WinBootVideo\WinBootVideo.exe`。请保留同目录的全部文件。
2. 选择 / 拖入本地视频。可以先用包内 `demo-login.mp4` 测试。
3. 点击“检查视频”。若缺少 FFmpeg 工具，可以直接预览；或选择同时包含 `ffmpeg.exe`、`ffprobe.exe` 的目录。
4. 需要处理时，指定保留的前几秒，点击“转换并选用”，保存为**新文件**。
5. 设置音量、屏幕和淡出，点击“保存并全屏预览”。按 Esc / 空格可跳过。播放至少前进 100 毫秒后，正常完成或跳过才算成功；损坏、加载失败或超时不算成功。
6. 确认效果后点击“启用登录动画”。它会备份并设置**当前用户**的登录 Shell，不保存密码、不请求管理员运行。下次注销 / 重启后登录生效。旧版已有启用时，“修复登录启动”会切换到桌面前模式并移除自己的备用任务，原视频验证仍有效。
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

本地非空视频最大 2 GB；网络路径 / 网络盘不接受。播放等待上限可设置 5–90 秒，默认 45 秒，**从输入桌面激活、请求播放时开始计时，包含视频加载、播放、等待桌面和淡出**。视频加载等待可设 2–15 秒，默认 8 秒，也限制可见后的首帧握手。等待期限内不提前启动 Explorer；冷启动在输入桌面激活后仍有独立 30 秒上限，失败时先结束自己的播放器再放行桌面。输入密码、等待登录画面的时间单独计算；超过 15 分钟仍未开始播放会跳过本次动画，进入普通桌面。视频太长会提前退出并判为预览失败，请先剪短。转码长度上限 80 秒。HDR 转码要求 FFmpeg 包含 `zscale`、`tonemap`，较旧或精简版本可能不支持。

典型 SDR 转码命令（GUI 已实现，无需手动执行）：

```powershell
ffmpeg -n -i "input.mp4" -t 15 -map 0:V:0 -map "0:a:0?" -vf "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p" -c:v libx264 -preset medium -crf 20 -profile:v high -level:v 4.1 -c:a aac -b:a 160k -ar 48000 -ac 2 -movflags +faststart "login-compatible.mp4"
```

FFmpeg 为可选的外部工具，按用户选择或 PATH 查找，本包不下载或分发它。FFmpeg 的许可取决于具体构建，见 [FFmpeg 官方许可说明](https://ffmpeg.org/legal.html)；个人准备素材与未来分发含工具的应用包需分别考虑。

## 播放原理

```text
Winlogon 启动当前用户的 WinBootVideo.Host.exe
  → 宿主启动播放器，等待不透明全屏窗口就绪
  → 播放器等待自己的桌面成为当前可见的输入桌面
  → 从第 0 秒加载并播放视频，另发开始信号，播放预算此时开始
  → 解码及合成工作确认后发送 FirstFrame，宿主才请求 Explorer
  → Explorer 在覆盖层后面准备桌面
  → 保留结束画面，淡出覆盖层，播放器 / 宿主退出
```

“遮罩就绪”指 WPF `ContentRendered` 的**不透明窗口**已渲染，不等于用户已经看到它或第一帧视频已解码。播放器在 UI 线程查询自身桌面的 `GetUserObjectInformation(UOI_IO)`，只有它连续 300 毫秒成为输入桌面才开始播放。宿主分开“遮罩就绪”“开始播放”“首帧呈现”三个事件；首帧信号要求媒体已打开、播放进度前进、当前屏幕的原生窗口边界及置顶覆盖有效，并在 WPF 合成回调中完成 [DwmFlush](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush)。它同步的是本进程排队的合成工作，不能据此推定整个登录画面已退场或其他程序绝不会覆盖。查询成功的 WTS 会话状态也须为 Active；启动早期 WTS 不可用时，以 UOI_IO 判断并记录原因。播放中若离开输入桌面，本次动画会退出，后续解锁不续播。

Windows 的 [ARSO](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/component-updates/winlogon-automatic-restart-sign-on--arso-) 可在重启后先自动登录，再保持锁定，因此 Shell 启动可能早于用户完成图片密码解锁。本程序不以 Shell 启动或固定延迟代替可见性判断，也不调整 ARSO 或密码设置。[输入桌面](https://learn.microsoft.com/en-us/windows/win32/winstation/desktops) 是当前可见且接收输入的桌面；[UOI_IO](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectinformationw) 用于查询这个状态，不读取手势、凭据或登录画面内容。

桌面就绪判断另使用当前会话的 `GetShellWindow`，它只能表明 Shell 桌面窗口存在，不保证所有任务栏图标、启动软件或壁纸插件已经加载。

独立宿主使用 .NET Framework 4.8，播放器 / GUI 使用 .NET 8。宿主不依赖播放器的 UI 线程，冷启动硬期限、首帧握手或总播放等待超时会终止**自己启动的播放器进程**，随后放行 Explorer；播放器另有独立线程，在宿主死亡 / 超过硬期限时自行退出。桌面请求在同一宿主内用锁协调，成功请求只执行一次；仅明确启动异常允许冷却后的有界重试。不结束其他应用、不改任务栏 / 桌面图标可见状态。失败时本次动画会被放弃，不在普通桌面出现后继续播放。

0.1.5 的备用登录任务不提供 Explorer 前的顺序保证，因此 0.1.6 不再用它播放。迁移仍通过原有 `login-task-backup.json` 和精确用户、命令归属还原任务；外部替换的同名任务保持原样。

同次登录用 Windows 访问令牌的 AuthenticationId 与会话登录时间组合去重，避免 Shell 与备用任务重复播放，也避免重启后 LUID 被复用而误跳过新登录。会话时间通过只读 [LsaGetLogonSessionData](https://learn.microsoft.com/en-us/windows/win32/api/ntsecapi/nf-ntsecapi-lsagetlogonsessiondata) 获取。锁屏解锁不会重新触发登录入口。尝试标记在播放前写入，同次登录已尝试失败时备用任务也不会强行重播。恢复保留原始 Shell 的存在状态、文本与注册表类型，并核验用户 SID；若其他软件改了 Shell 或同名任务，本工具不会覆盖它。

Shell 登录配置写入位置：

```text
HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell
```

旧备用任务位于任务计划程序根目录，名称为 `WinBootVideo.Login.<当前用户SID>`。`login-task-backup.json` 保存任务原状态，完成迁移后仍保留此备份。严格模式首次启用不创建播放任务；恢复脚本和 GUI 仍兼容旧版的两项配置。

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

`build.ps1 -Test` 验证超时状态、首帧先于桌面请求、命令行转义、备份恢复、旧任务迁移和素材保存；注册表及配置事务通过模拟对象测试。`verify.ps1` 还进行真实转码、WPF 播放、首帧 IPC 与交接顺序、损坏文件、超时、卡死子进程、宿主死亡及播放器缺失验证。旧任务适配器回归用 GUID 唯一临时任务，关闭自动登录触发，手动运行独立窗口预览后只清理自己的任务；另验证真实旧备用命令不创建播放器。GUI 全屏播放在单屏或多屏均执行，专用副屏回归在少于两屏时明确跳过。自动测试不注册真实 Shell、不注销、不重启；播放器会短暂显示静音窗口或全屏视频。真实 Winlogon 的衔接仍须实际登录验证。

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
