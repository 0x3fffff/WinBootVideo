# Windows 启动阶段边界对照

核查日期：2026-10-05（Asia/Shanghai）。用户目标已确定为**输入密码/PIN 后、桌面出现前**；下列项目位于登录前的固件/系统引导阶段，作为技术边界对照，不作为当前主方案。仅阅读公开文档与源码，未安装、执行项目或修改启动设置。

| 项目 | 实际改动阶段及能力 | 当前核实到的发布状态 | 对当前需求的结论 |
|---|---|---|---|
| [HackBGRT](https://github.com/Metabolix/HackBGRT) | UEFI 应用替换 BGRT 启动 Logo，随后启动 Windows Boot Manager；静态图，不是视频/动图播放器 | 最新 [v2.6.0](https://github.com/Metabolix/HackBGRT/releases/tag/v2.6.0)，2026-04-02 发布，更新 shim 到 16.1；仓库未归档 | 适合改登录前 Logo，不满足登录后动画 |
| [AnimeBoot](https://github.com/helloyork/AnimeBoot) | UEFI GOP 逐帧播放，然后 chainload Windows Boot Manager | 最新 [v0.2.0](https://github.com/helloyork/AnimeBoot/releases/tag/v0.2.0)，2026-09-04 发布；README 仍定位为 proof of concept；仓库未归档 | 真动画，但出现在 Windows 加载前；不满足登录后动画 |
| [Windows 7 Boot Updater](https://github.com/coderforlife/windows-7-boot-updater) | 修改 Windows 7 的系统引导动画资源及引导文件 | GitHub 最新 [v0.0.1.3](https://github.com/coderforlife/windows-7-boot-updater/releases/tag/v0.0.1.3)，2021-07-01 初次开源发布；官网同版本下载更新时间 2018-10-07；仓库未归档 | Windows 7 专用旧路线，不应用到 Windows 10/11 |

发布时间、最后主分支提交及 archived 状态使用 GitHub 公开 API 核查。最后提交分别为 HackBGRT `62cc4a8`（2026-04-02）、AnimeBoot `f1af51c`（2026-09-04）、Windows 7 Boot Updater `e5f132c`（2021-07-01）；“未归档”不等于稳定维护或硬件兼容保证。[HackBGRT API](https://api.github.com/repos/Metabolix/HackBGRT)、[AnimeBoot API](https://api.github.com/repos/helloyork/AnimeBoot)、[Windows 7 Boot Updater API](https://api.github.com/repos/coderforlife/windows-7-boot-updater)

## HackBGRT：不能把换 Logo 当成换动画

ACPI 6.6 的 BGRT 定义是图像地址、位置和显示状态；Image Type 只定义 Bitmap，格式为 24/32 位位图，没有帧序列和时间轴。微软也把 UEFI BGRT 列为其支持的自定义启动 Logo 路径。这说明静态 Logo 与 Windows 自己绘制的加载转圈是不同机制。[ACPI BGRT 规范 §5.2.23](https://uefi.org/specs/ACPI/6.6/05_ACPI_Software_Programming_Model.html#boot-graphics-resource-table-bgrt)、[微软 Unbranded Boot](https://learn.microsoft.com/en-us/windows/configuration/unbranded-boot/#replace-the-startup-logo)、[微软 BGRT Logo 格式](https://learn.microsoft.com/en-us/windows-hardware/drivers/bringup/boot-screen-components#add-the-logo-to-the-bgrt)

HackBGRT 源码读取一张 BMP，替换 BGRT，然后启动后续引导程序；README 的多个图片配置是每次随机选一张，并非连播。[源码 main.c](https://github.com/Metabolix/HackBGRT/blob/62cc4a8cb7d2c7b7e2797f4c7480f00d0774e768/src/main.c#L303)、[README Images](https://github.com/Metabolix/HackBGRT#images)

**不能沿用“HackBGRT 必须关闭 Secure Boot”的旧结论。**当前项目提供 shim，通过 MOKManager 手动登记指定 HackBGRT 文件的哈希来建立信任；README 没有要求一律关闭 Secure Boot。成功仍取决于设备是否接受其 shim 与信任配置。修改引导链会涉及 TPM 测量，项目明确提示可能影响 BitLocker、PIN 等功能；因此不能把它等同于普通登录后应用。[Secure Boot 说明](https://github.com/Metabolix/HackBGRT#secure-boot)、[shim 指南](https://github.com/Metabolix/HackBGRT/blob/62cc4a8cb7d2c7b7e2797f4c7480f00d0774e768/shim.md)、[TPM 说明](https://github.com/Metabolix/HackBGRT#tpm)

## AnimeBoot：预播放，而非与 Windows 加载并行

主函数先同步执行 `AbPlayFromPackage` / `AbPlayFromLoose`，返回后恢复 GOP 状态，再调用 `AbLaunchNextStage`；后者才 `LoadImage` / `StartImage` 启动 `\\EFI\\Microsoft\\Boot\\bootmgfw.efi`。因此，依据源码推断，动画时长是引导前增加的时间，并不掩盖并行进行的 Windows 内核加载。[UefiMain 顺序](https://github.com/helloyork/AnimeBoot/blob/f1af51ce615a1c16d1cbd4f4ad2f9af73defd2c9/AnimeBootPkg/Application/AnimeBoot/AnimeBoot.c#L454)、[启动下一阶段](https://github.com/helloyork/AnimeBoot/blob/f1af51ce615a1c16d1cbd4f4ad2f9af73defd2c9/AnimeBootPkg/Application/AnimeBoot/AnimeBoot.c#L1813)

“支持 MP4/GIF”是**主机侧预处理输入**：Python 用 Pillow/imageio 解出帧、导出 BMP，再打包为 `.anim`；EFI 端只解释 BGRA 原始像素或不压缩 BMP 帧，通过 GOP 输出，没有在固件里直接解码 MP4/GIF。逐帧未压缩数据也解释了资源体积较大的特点。[主机帧提取](https://github.com/helloyork/AnimeBoot/blob/f1af51ce615a1c16d1cbd4f4ad2f9af73defd2c9/host-tools/abtool/abtool/frames.py#L23)、[打包器](https://github.com/helloyork/AnimeBoot/blob/f1af51ce615a1c16d1cbd4f4ad2f9af73defd2c9/host-tools/abtool/abtool/anim_package.py#L47)、[EFI 帧解码](https://github.com/helloyork/AnimeBoot/blob/f1af51ce615a1c16d1cbd4f4ad2f9af73defd2c9/AnimeBootPkg/Application/AnimeBoot/AnimeBoot.c#L1135)

v0.2.0 修复之前的编译、打包与读取失败，并称已在 Hyper-V Gen 2 完成 220 帧动画后启动 Windows 的端到端验证。这是 PoC 的具体验证证据，仍不足以证明广泛真机成熟度。发布包里的 EFI 二进制是未签名的，不能直接视作可在启用 Secure Boot 的设备上部署；循环次数为 0 且未设总时长会阻止继续启动。这些均为发布说明直接披露的限制。[v0.2.0 发布说明](https://github.com/helloyork/AnimeBoot/releases/tag/v0.2.0)

## Windows 7 Boot Updater：旧系统的二进制补丁路线

作者声明支持 Windows 7 RTM/SP1 各版本；EFI、Windows 8 支持仍列在待办，EFI 启动路径明确不支持。其动画采用 Windows 7 的固定 105 帧、200×200、15 fps 机制，并修改 `winload.exe` / `bootmgr` 等引导文件。由这些明确支持范围及机制可判断，它不是可移植的 Windows 10/11 技术方案。[作者官网支持范围与限制](https://www.coderforlife.com/projects/win7boot/)、[GitHub 初次开源发布](https://github.com/coderforlife/windows-7-boot-updater/releases/tag/v0.0.1.3)

## 对当前方案选择的含义

当前目标需要关注登录后用户会话中的播放器与 Explorer 启动时序。以上三条路线解决的是登录前引导阶段；改变 BGRT、EFI 启动项或系统引导文件并不能控制输入 PIN 后到桌面出现前的动画时机。该结论为上述执行阶段的技术推断。

源码阅读快照保存在 `research/sources/boot-boundaries/`，分别锁定上述 HackBGRT 与 AnimeBoot 提交。未把 README 中的安装、注册密钥、BCD 或分区操作作为本次操作步骤执行。
