# 架构说明

本项目的两个功能运行在不同阶段，共享设置界面，但不共享启用 / 恢复事务。

## 登录动画

```text
Winlogon → 当前用户 Shell：WinBootVideo.Host.exe
  → 启动 WPF 播放器，等待不透明覆盖窗口渲染
  → 等待播放器所在桌面成为输入桌面
  → 从第 0 秒播放，启动播放预算
  → 首帧解码、播放进度与合成确认后发 FirstFrame
  → 宿主请求 Explorer，在覆盖层后准备桌面
  → 视频结束，保留末帧并淡出，宿主 / 播放器退出
```

宿主采用 .NET Framework 4.8，GUI / 播放器采用 .NET 8 WPF。Shell 写入位置是当前用户的 `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell`，启用前保存原值的存在状态、文本、类型和用户 SID。已有外部 Shell 或策略时拒绝启用；恢复不覆盖已被其他工具修改的值。

`ContentRendered` 仅证明窗口渲染，不能证明用户已看到视频。播放器用 [GetUserObjectInformation / UOI_IO](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getuserobjectinformationw) 判断自身是否为输入桌面，连续稳定 300 毫秒才播放；WTS 查询可用时还要求会话 Active。首帧确认要求媒体打开、进度前进、原生窗口边界与覆盖有效，并在合成回调中 [DwmFlush](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush)。DwmFlush 不是整台系统的画面验收信号。

Windows [ARSO](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/component-updates/winlogon-automatic-restart-sign-on--arso-) 可能先自动登录再锁定，所以不能拿 Shell 创建时间当成用户认证完成时间。实现不读取密码、图片手势或凭据，也不修改 ARSO。播放中失去输入桌面就结束本次动画，解锁不续播。

宿主的超时与退出处理独立于 WPF UI 线程。无法初始化或在期限内呈现时，先结束自己启动的播放器，再放行 Explorer。桌面请求以锁协调，成功请求只执行一次，明确的启动异常才允许有界重试。播放器另有线程监控宿主死亡 / 硬期限。已有可见桌面时跳过动画，不结束现有 Explorer 来模拟桌面前效果。

同次登录以访问令牌 AuthenticationId 和只读 [LsaGetLogonSessionData](https://learn.microsoft.com/en-us/windows/win32/api/ntsecapi/nf-ntsecapi-lsagetlogonsessiondata) 登录时间去重，避免重启后 LUID 复用误判。0.1.5 曾用备用登录任务；0.1.6 起不再用它播放，迁移按原备份和精确归属恢复任务。旧任务名称为 `WinBootVideo.Login.<SID>`。

保存素材到 AppData，启用时复制宿主与 GUI；成功保存预览是启用的前提。日志、配置和恢复备份属于当前用户，不能作为仓库示例上传。

## 开机 Logo

```text
WPF 向导 → 独立管理员辅助进程
  → 只读检查：分区 / 加密 / EFI 空间 / 固件项
  → 保存受保护的原始恢复记录
  → 校验并解包固定 HackBGRT 官方文件
  → 仅安装文件，核对 EFI 内容
  → 创建独立项，追加 BootOrder 末尾
用户单次试启动 → 必要的 shim 哈希确认 → 手动设为默认
```

内置 [HackBGRT 2.6.0](https://github.com/Metabolix/HackBGRT/releases/tag/v2.6.0) ZIP，SHA-256 为 `6204911d777ac03e514b90126568ef6faa1d477779b31c536b142dba92f4a2c0`。准备包导出是普通文件操作；管理员安装使用导出的已校验图片，官方程序另从内置资源解包，不执行用户替换的准备包工具。

恢复记录在 `%ProgramData%\WinBootVideo-BootLogo`，安装材料不是恢复备份。独立 EFI 目录为 `EFI\HackBGRT`，不覆盖原 Windows 启动器，不修改 BCD、TPM、安全启动密钥，不自动重启或设为默认。官方 .NET Framework 安装器的卷 GUID 路径通过局部 `setup.exe.config` 兼容，参见 [微软路径规范化说明](https://learn.microsoft.com/en-us/dotnet/framework/migration-guide/mitigation-path-normalization)。

恢复记录先于写操作。每个阶段失败时撤销自身变更，失败记录保留。安装后查询核验原 Windows 项、自身项完整字节、已保存设备项、六个文件哈希及启动器备份，之后才从 BootOrder 返回 `LogoIsDefault`；失败 / 未知状态不能显示“已设为默认”。同名外来项不作为归属证明。

支持的 Insyde USB / 光驱 / 网络入口按厂商、编号、属性、固定参数及完整字节匹配，不仅看名称。未知固件项、加密、多 EFI 分区或读不清的配置仍停止；不提供强制放行。恢复只移除拥有凭据的自身项和顺序引用，保留 EFI 文件避免固件残留引用悬空，不整体导入旧备份。

## 代码与验证

| 目录 | 职责 |
| --- | --- |
| `src/WinBootVideo` | WPF 设置界面、播放器、Logo 辅助入口 |
| `src/WinBootVideo/Core` | 设置、媒体、Shell / 任务、Logo 包与安全事务 |
| `src/Host` | Framework 登录宿主、监督策略、桌面交接 |
| `src/Shared` | 命令行、输入桌面、启动限制、旧任务协议 |
| `tests` | 状态机、模拟注册表 / 固件事务、GUI 与媒体检查 |
| `research` | 调研、设计边界和上游来源 |

CI 不做真实 Shell / EFI 安装。GUI 与媒体验证需要实际 Windows 输入桌面，完整 Winlogon / 固件时序需要实机；两者不能用模拟测试的通过结果替代。命令和边界见 [QA.md](../QA.md) 与 [贡献说明](../CONTRIBUTING.md)。
