# Windows 登录动画技术调研

调研日期：2026-10-05。范围：输入密码/PIN 后、桌面出现前；先供个人使用；用户接受可恢复的当前用户 Shell 调整，优先动画与桌面的衔接。

结论基于 GitHub 源码、发布信息和微软官方文档。此次只做静态研究，没有安装、注册启动项或测试真实登录。因此，“基础组件成熟”“项目具备功能”和“已在当前电脑验证稳定”应分别判断。

## 推荐路线

采用轻量 C# Shell 宿主 + WPF 全屏遮罩 + 本地视频。参考 DARLING 的启动顺序，补齐有界等待、当前会话的 Explorer 恢复和配置回滚。

```text
密码/PIN 验证完成
    → 当前用户 Shell 宿主
    → 全屏黑底遮罩完成渲染
    → 播放动画，同时启动当前会话的 Explorer
    → 动画结束、桌面基本就绪，或等待达到上限
    → 淡出遮罩，进入桌面
```

这是根据用户目标作出的工程建议，并非微软提供的“登录视频”成品功能。DARLING 已有 `ContentRendered` 后启动 Explorer 的实现，可用来验证顺序，但其整条故障路径尚不够完整。[源码](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1)

## 检索到的项目与路线

| 项目或路线 | 实际作用阶段 | 成熟度与本需求的关系 |
| --- | --- | --- |
| [DARLING-in-the-Windows](https://github.com/Rantkid/DARLING-in-the-Windows) | 用户登录后的 Shell 启动阶段 | 最贴近目标。C# 启动器调用 PowerShell/WPF 视频遮罩；适合作为个人效果原型。源码核查发现启动超时、就绪判断、会话隔离仍需加强。 |
| [AutoBloom](https://github.com/willnode/autobloom) | 登录计划任务播放 Windows 11 Bloom 视频 | 本轮补充检索：安装器创建 ONLOGON 和 ONSTART 两个任务；播放脚本设置首帧壁纸、用 mpv 播放，再切换末帧壁纸。值得参考视频与桌面壁纸的视觉衔接，无法保证先于 Explorer。[安装脚本](https://github.com/willnode/autobloom/blob/master/install.bat)、[播放脚本](https://github.com/willnode/autobloom/blob/master/src/run.bat) |
| [GameLauncherShell](https://github.com/quangmach/GameLauncherShell) | 用户初始化阶段的游戏启动动画 | 本轮补充检索：Playnite 安装脚本写入 HKLM Winlogon Userinit；播放脚本终止 Explorer，再用播放器遮挡游戏启动器和桌面启动。适合作为时序思路参考，其机器级登录入口修改不建议直接照搬。[安装脚本](https://github.com/quangmach/GameLauncherShell/blob/main/Playnite_Installer.bat)、[播放脚本](https://github.com/quangmach/GameLauncherShell/blob/main/Playnite/Launcher.bat) |
| [PlayniteShell](https://github.com/Blueforcer/PlayniteShell) | 登录后以自定义 Shell 播放视频并启动 Playnite | 本轮补充检索：README 明确以 Windows 11 为例；VLC 播放本地视频，Desktop.bat 修改机器级 Shell 并启动 Explorer。教程涉及关闭 UAC，仅参考视觉流程，不照搬安装配置。[说明](https://github.com/Blueforcer/PlayniteShell)、[桌面脚本](https://github.com/Blueforcer/PlayniteShell/blob/main/Desktop.bat) |
| [LogonVistaOrb](https://github.com/xxxman360/LogonVistaOrb) | LogonUI 相关登录、锁屏及注销流程 | 有安装器与发布包，但通过 IFEO 介入 LogonUI，主要是图片动画。系统介入范围大于本需求，不建议作为通用 MP4 登录动画基础。详见[候选调研](login-candidates.md)。 |
| [HackBGRT](https://github.com/Metabolix/HackBGRT) | UEFI/Windows 引导前后的静态标志 | 适合静态 Logo；无法代替登录后视频动画。较完整的引导工具也不等于满足本需求。详见[引导边界调研](boot-boundaries.md)。 |
| [AnimeBoot](https://github.com/helloyork/AnimeBoot) | Windows Boot Manager 前 | UEFI 预播放动画的 PoC，与用户选定阶段不同。详见[引导边界调研](boot-boundaries.md)。 |
| [Windows 7 Boot Updater](https://github.com/coderforlife/windows-7-boot-updater) | Windows 7 原生引导画面 | 针对 Windows 7 的系统文件修改方案；不能据此推定适配 Windows 10/11。[项目说明](https://github.com/coderforlife/windows-7-boot-updater#readme) |
| [WIN-B_M](https://github.com/official-Arvind/WIN-B_M) | 普通登录计划任务，同时包含 EFI Logo 功能 | 本次检索仅见 1 个提交，无 latest release。普通登录覆盖层无法给出前桌面顺序保证；缺少认定其成熟的证据。详见[候选调研](login-candidates.md)。 |
| 登录计划任务 + [mpv](https://github.com/mpv-player/mpv) 或自写播放器 | 用户登录后并发启动 | 平台和播放器成熟、容易移除。微软文档仅保证登录触发，不保证任务先于 Explorer 显示；因此无法据此承诺没有桌面闪现。[登录触发文档](https://learn.microsoft.com/en-us/windows/win32/taskschd/starting-an-executable-when-a-user-logs-on) |

补充检索新增了 AutoBloom、GameLauncherShell 和 PlayniteShell，扩大了登录后动画的参考范围；这些项目仍各有时序或恢复方面的限制。本次检索中，没有找到同时具备广泛维护、任意视频、严格前桌面时序和完整恢复设计的通用成品。表中的静态 Logo、UEFI 动画、LogonUI 动画和播放器组件不应混为同一种方案。

## DARLING 的源码判断

审阅版本：`99c458439aa2ba8ab374c2484c5bf0141950b109`，对应 v1.1.1。当前检索到 7 个提交和 3 个 Release；Release 没有附加可执行资产，需按源码说明构建。提交、发布数量只是维护规模线索，不是可靠性证明。[提交历史](https://github.com/Rantkid/DARLING-in-the-Windows/commits/main/)、[v1.1.1](https://github.com/Rantkid/DARLING-in-the-Windows/releases/tag/v1.1.1)

值得借鉴的部分：遮罩内容渲染后才启动 Explorer；支持跳过和淡出；正常退出、媒体失败及脚本清理路径有恢复逻辑；提供当前用户 Shell 注册/恢复管理入口。[播放脚本](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1)、[管理脚本](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/WallpaperBoot.ps1)

主要局限：

1. **启动等待没有独立上限。** `BootShell.cs` 使用无超时的 `WaitForExit()`。配置的 45 秒播放超时在取得前景窗口、调用播放后才启动，而且依赖 UI Dispatcher。不能覆盖脚本初始化卡住、前景等待或 UI 线程停顿。[宿主源码](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/BootShell.cs)、[播放脚本](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1)
2. **壁纸就绪并不控制桌面露出。** `wallpaperReady` 会被赋值，但动画结束、媒体失败和提前淡出路径不以它作为退出条件。因此等待 Wallpaper Engine 的配置并不等于确认壁纸已绘制。[播放脚本](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1)
3. **Explorer 进程存在不等于当前会话桌面存在。** 外层 fallback 按进程名全局检查；多用户会话下需要改成当前会话判断，并结合桌面/任务栏窗口信号。此处是根据代码推导出的风险，尚未复现。[宿主源码](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/BootShell.cs)
4. **单次播放和多屏能力要按代码理解。** `playOncePerBoot` 实际按登录会话记录，注销重登仍会播放；多屏使用整个虚拟桌面的单窗口铺展视频，没有逐屏布局或显式混合 DPI 处理。[状态及窗口代码](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/Start-BootIntro.ps1)

注册入口是当前用户的 `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell`，不是微软 Shell Launcher 组件。恢复逻辑可还原备份值或删除新增覆盖项。个人非商业使用可按 PolyForm Noncommercial 条款使用源码；媒体素材另行处理。[管理脚本](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/WallpaperBoot.ps1)、[许可证](https://github.com/Rantkid/DARLING-in-the-Windows/blob/99c458439aa2ba8ab374c2484c5bf0141950b109/LICENSE)

更多细节见[源码审阅记录](darling-audit.md)。

## Windows 版本与官方入口

只读查询当前电脑得到：`EditionID=CoreCountrySpecific`、`DisplayVersion=25H2`、`CurrentBuild=26200`、`UBR=9168`。ProductName 仍写 Windows 10 Home China，但微软版本表将 Build 26200 归于 Windows 11 25H2；不能只按 ProductName 判断系统代际。[微软版本表](https://learn.microsoft.com/en-us/windows/release-health/windows11-release-information)

| 入口 | 官方支持范围 | 应用判断 |
| --- | --- | --- |
| Custom User Interface / CustomShell 用户策略 | Pro、Enterprise、Education、IoT Enterprise | 官方文档明确可以替代 Explorer；可作为专业版及以上的配置入口。它不是 Shell Launcher。[文档](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-winlogon) |
| Shell Launcher | Enterprise、Education、IoT Enterprise 及相应 LTSC | 官方提供自定义 Shell 监控/退出动作；偏专用终端。Home/Pro 不在支持范围内。会启动其他进程后立即退出的宿主，需要另行设计退出动作，否则可能被重复启动。[概览](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/)、[启动与退出配置](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure) |
| DARLING 的 HKCU Winlogon Shell 覆盖 | 社区方案，需逐系统验证 | 当前家庭版不能直接采用上述官方策略支持范围。可将这一路线作为个人试验入口，但不宣称拥有官方 Shell Launcher 的支持与监控保证。 |

## 播放组件与实现建议

第一版建议使用 WPF `MediaElement`，素材先统一为 MP4 / H.264 / AAC，并在当前电脑手动验证。WPF 是官方桌面 UI 技术；它的媒体支持依赖 Windows 媒体栈，不能仅凭扩展名保证视频可播放。[WPF 媒体文档](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/multimedia-overview)

若后续需要更多编码格式，可评估 [LibVLCSharp](https://github.com/videolan/libvlcsharp) 或 [mpv/libmpv](https://github.com/mpv-player/mpv)。它们是播放基础组件，不包含登录顺序、遮罩交接和 Shell 恢复。LibVLCSharp 需要随程序部署 LibVLC；WPF 的原生视频窗口组合、覆盖层及透明度需要单独验证。[LibVLCSharp 安装说明](https://github.com/videolan/libvlcsharp#installation)、[WPF airspace 说明](https://github.com/videolan/libvlcsharp/blob/3.x/docs/getting_started.md#wpf)

针对本次个人需求，建议补齐这些具体设计：

- 将播放 UI 与启动监督分开；从宿主启动开始设置墙钟期限，渲染器没有就绪或挂起时也能结束等待并启动当前会话 Explorer。
- 先渲染黑底遮罩，再允许 Explorer 启动；用明确进程间信号交接，不以“进程存在”代替窗口就绪。
- 动画结束后只等待有限的桌面准备时间；Wallpaper Engine 作为可选步骤，其进程存在不等于壁纸已完成渲染。
- 优先让全屏遮罩覆盖桌面，减少直接隐藏/恢复任务栏和桌面图标的操作。需要隐藏时，监督进程负责异常恢复。
- 保存原 Shell 项的存在状态、类型和值，提供明确恢复入口；正常退出、跳过、媒体失败和超时统一走收尾流程。

这些是待实现和验证的建议。后续验证应覆盖重启、关机后开机/快速启动、锁屏解锁、注销重登、视频缺失或损坏、渲染进程异常退出，以及实际使用的屏幕/DPI 配置。当前尚未测量登录耗时、帧率、桌面闪现或真实故障恢复。
