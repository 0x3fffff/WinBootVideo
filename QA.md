# 验证记录

版本：0.3.0。日期：2026-10-08。构建环境：Windows 11 x64，.NET 8 SDK 8.0.422 / WindowsDesktop 8.0.28，.NET Framework 4.8。

## 实机 Logo 流程

实机使用反馈已确认 Windows 11 / COLORFUL P15 23 / Insyde UEFI 上的开机 Logo 流程跑通。环境为 Secure Boot 开启、系统盘未加密、单 EFI 系统分区。

- 软件选图、BMP3 转换、准备包导出、管理员检查和独立项安装完成。
- 固件启动菜单单次选择 HackBGRT，首次出现安全验证提示。
- 按 OK / Enter，在 5 秒倒计时内进入 MOKManager，确认 EFI/HackBGRT/grubx64.efi 的文件哈希后重启。
- 再次选择 HackBGRT，确认自定义 Logo 和 Windows 登录正常。
- 将 HackBGRT 设为默认入口并保存，完成每次启动生效的设置流程。

以上是使用者本机的操作反馈，软件未自动执行固件默认顺序或 MOK 确认。不能据此推断其他固件、加密磁盘、多系统、所有反作弊软件或固件更新后的兼容性。

## 自动检查

| 检查 | 最近通过结果 | 验证范围 |
| --- | --- | --- |
| build.ps1 -Test | 125 项 | 登录宿主策略、首帧交接、设置复制、命令行、备份和任务迁移 |
| Logo 隔离回归 | 488 项 | EFI 数据解析、文件校验、模拟注册与恢复、故障撤销、向导导航、默认状态、辅助 CLI 拒绝路径 |
| GUI 窗口预览 | 10 项 | 首页入口、三步导航、禁用原因、实际播放回执、设置保留 |
| GUI 真实全屏预览 | 14 项 | 完整向导流程、真实播放器、屏幕覆盖和首帧交接，未靠跳过结束 |
| GUI 故障恢复 | 3 项 | 任务服务故障时仍能恢复自己的 Shell，准确报告部分失败 |
| 官方安装器夹具 | 包含在 Logo 回归中 | 固定 HackBGRT 2.6.0 的实际路径初始化与文件复制，只使用普通临时目录 |

Logo 回归使用模拟固件；GUI 的安装和恢复执行器为模拟对象。官方安装器夹具不调用固件、BCD、挂载或安装主入口。自动检查不代表完整真实启动测试。

```powershell
.\build.ps1 -Test
New-Item -ItemType Directory artifacts\fixtures -Force | Out-Null
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /out:artifacts\fixtures\OfficialLogoFiles.exe tests\fixtures\OfficialLogoFiles.cs
& "$env:ProgramFiles\dotnet\dotnet.exe" run --project tests\WinBootVideo.Checks.csproj -c Release --no-restore -- --boot-logo-only artifacts\fixtures\OfficialLogoFiles.exe artifacts\WinBootVideo
```

0.3.0 固定发布目录已通过上述检查，首页、登录向导各页及 Logo 向导各页已渲染并检查。GUI 播放检查须在正常 Windows 输入桌面权限上下文运行；受限桌面上下文不能作为真实播放通过结果。本次没有重新安装 Logo 或修改固件配置。

默认启动状态来自已完成文件和启动项归属核验后的 BootOrder，使用结构化字段返回。单次选择 HackBGRT 仍显示“尚未设为默认”；手动保存为第一启动项后显示“已设为默认”。检查失败或未知状态不显示默认已保存，查询全过程不写入固件。

完整媒体回归可运行 `verify.ps1 -FFmpegDirectory <FFmpeg目录>`，要求目录中同时包含 ffmpeg.exe 和 ffprobe.exe。

## 已覆盖的 Logo 保护条件

- 首次安装只追加自身独立项，不覆盖 Windows 启动器；原 Windows 默认入口保留。
- 分区、系统盘加密、可用空间、启动项结构和固件设备格式不明确时停止。
- Insyde 通用 USB / 光驱 / 网络入口按厂商、编号、属性和完整字节核验，安装与恢复不更改这些项。
- 原始恢复记录先于文件和固件写入保存；各阶段失败撤销自身变更，撤销不完整时保留失败记录。
- 安装后检查核对受保护记录、原 Windows 项、自身项完整字节、已保存设备项、六文件哈希、Windows 文件和恢复备份。
- 单次从 HackBGRT 启动或手动设为默认后可以识别已安装状态；无记录、未完成 / 损坏记录和外来变化不放行重复安装。
- 官方卷 GUID 路径兼容由安装器本地配置启用，不修改系统注册表、分配盘符或格式化分区。
- 图片转换覆盖透明合成、黑边、BMP 行对齐和损坏数据拒绝。

## 待验证事项

- 不同主板、混合 DPI 多屏、远程桌面、多用户会话及第三方桌面组件。
- 固件更新、Secure Boot 数据库更新、断电或其他工具改动启动项后的兼容性。
- Windows Hello / PIN、TPM 相关功能及各游戏反作弊环境。
- 登录视频的真实冷启动时序。独立 `--login-wait-only` 诊断重放目前仍预期失败，记录输入桌面长期不可见时的超时路径；开机 Logo 的实机反馈不代表该问题已修复。

本地验证日志位于 artifacts/logs，界面渲染位于 artifacts/screenshots，均不提交到源码仓库。个人设置、媒体和恢复资料保存在应用数据目录，不能作为示例配置上传。
