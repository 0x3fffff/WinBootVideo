# WinBootVideo

让 Windows 的开机过程更像你自己。

[![Windows build](https://github.com/0x3fffff/WinBootVideo/actions/workflows/windows.yml/badge.svg)](https://github.com/0x3fffff/WinBootVideo/actions/workflows/windows.yml)
[![MIT license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

WinBootVideo 是 Windows 11 上的开机个性化工具：用一段视频迎接登录，或替换 Windows 加载时的静态 Logo。中文分步向导包含素材处理、预览、状态检查和恢复入口。

**[下载 v0.3.0 公开预览版](https://github.com/0x3fffff/WinBootVideo/releases/tag/v0.3.0)** · [使用说明](docs/usage.md) · [报告问题](https://github.com/0x3fffff/WinBootVideo/issues) · [更新记录](CHANGELOG.md)

![WinBootVideo 首页](docs/screenshots/home.png)

## 两个功能，两个阶段

| 功能 | 出现阶段 | 操作流程 |
| --- | --- | --- |
| 登录动画 | 密码 / PIN / 图片密码验证后，进入桌面前 | 选视频 → 播放设置 → 预览 → 启用 |
| 开机 Logo | UEFI 启动 Windows 时的静态图片 | 检查电脑 → 准备图片 → 安装 → 单次试启动 → 设为默认 |

- 登录动画每次登录播放一次，锁屏解锁不重复。正常播放结束后，宿主和播放器退出。
- 视频检查与转换支持 H.264 / SDR、保留比例、补黑边和移除音轨；也可直接预览现有视频。
- Logo 图片自动转换为黑底 BMP3，内置固定版本 HackBGRT 2.6.0。
- Logo 安装保留 Windows Boot Manager，通过独立启动项试启动；界面区分“已安装”和“已设为默认”。
- 两个功能分别启用、分别恢复。首次打开不会更改启动设置。

## 开始使用

1. 在 [Releases](https://github.com/0x3fffff/WinBootVideo/releases) 下载 `WinBootVideo.zip` 并解压，保留同目录全部文件。
2. 安装 [x64 .NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)，运行 `WinBootVideo.exe`。普通 .NET Runtime 不含 WPF 桌面组件。
3. 首页选择功能，按向导完成。登录动画可先使用包内 `demo-login.mp4` 测试。

运行环境为 **Windows 11 x64**。视频检查 / 转换另需 FFmpeg 与 ffprobe，直接预览和 Logo 图片转换不需要。软件不自动下载这些工具。

**当前为公开预览版。** Logo 流程已有一台 COLORFUL / Insyde 实机的操作反馈；不同固件仍需先单次试启动。登录动画有输入桌面长时间不可见时的超时诊断尚未解决，预览成功不代表所有电脑的冷启动都已验证。详见 [验证记录与已知限制](QA.md)。

## 开机 Logo 的安装方式

![Logo 安装前的只读检查](docs/screenshots/logo-setup.png)

先检查硬件，再准备图片和恢复资料。安装后，Windows Boot Manager 仍是默认入口：从固件 Boot Menu **单次选择 HackBGRT**，确认 Logo 和 Windows 登录正常，再在 BIOS 的启动顺序中把 HackBGRT 放到第一位并保存。软件提供完整步骤，默认顺序由用户手动设置。

保持现有 Secure Boot、TPM、UEFI / CSM 和 SATA 设置。Secure Boot 开启时可能需要首次 shim 文件哈希确认，[使用说明](docs/usage.md#首次启动与每次生效)包含按键顺序。当前只支持单 Windows 启动器、单 EFI 系统分区、未加密的系统盘；未知配置会停止安装。刚通电时固件自己的厂商图片仍可能短暂出现，本项目不刷写 BIOS。

### 准备包到底有什么用？

它是**安装材料文件夹**，包含转换后的 `splash.bmp`、配置、官方工具和操作说明，方便离线保留和查阅。导出不会更改启动项，也不会让 Logo 生效。

| 当前状态 | 对用户有什么区别 |
| --- | --- |
| 没有导出 | 可以选图和查看说明；当前版本不能继续安装 |
| 已导出，还未安装 | 安装读取包内图片；删除原图不影响它，删除准备包则要重新导出 |
| 已安装且试启动成功 | Logo 已有 EFI 副本，准备包和原图可以删除；软件无需常驻 |

准备包**不含本机原启动状态的备份**。真正的恢复记录在 `%ProgramData%\WinBootVideo-BootLogo`，安装后需要保留，并把恢复说明另存到其他设备。[文件保留与恢复说明](docs/usage.md#文件保留与删除)解释各目录的用途。

## 构建与贡献

需要 Windows x64、.NET 8 SDK 和 Windows 自带的 .NET Framework C# 编译器，无第三方 NuGet 依赖。

```powershell
.\build.ps1 -Test
.\build.ps1 -Package
```

程序统一输出到 `artifacts\WinBootVideo`，ZIP 固定为 `artifacts\WinBootVideo.zip`。构建前关闭输出目录中运行的程序。CI 只运行隔离检查，不修改真实启动配置。

欢迎复现报告、兼容性反馈和小范围修复。开始前请读 [贡献说明](CONTRIBUTING.md)；涉及启动项、恢复或权限边界的问题请读 [安全报告方式](SECURITY.md)。实现细节见 [架构说明](docs/architecture.md)，完整测试范围见 [QA.md](QA.md)。下一阶段优先处理真实登录时序、更多固件验证和 .NET 10 LTS 迁移。

## 许可与致谢

作者：**[0x3fffff](https://github.com/0x3fffff)**。项目代码和原创示例视频采用 [MIT](LICENSE) 许可。

Logo 集成使用 [HackBGRT](https://github.com/Metabolix/HackBGRT) 官方发布包，保留其 MIT、GNU-EFI 和 shim 许可，详见 [第三方声明](THIRD-PARTY-NOTICES.md)。登录动画的架构调研参考 [DARLING-in-the-Windows](https://github.com/Rantkid/DARLING-in-the-Windows) 和 [AutoBloom](https://github.com/willnode/autobloom)，没有复制其动画素材或受限制的实现代码。[调研记录](research/windows-login-animation.md)保留了方案和许可比较。
