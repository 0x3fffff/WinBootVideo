# 开机 Logo 集成审核（2026-10-05）

目标是独立的 Windows UEFI BGRT 静态图片功能，保留现有登录视频。用户选择受保护的官方流程，并要求图片转换、BIOS 引导和恢复设计。默认先单次试启动，软件不自动接管默认启动。

## 固定来源

- [HackBGRT 2.6.0 发布](https://github.com/Metabolix/HackBGRT/releases/tag/v2.6.0)，2026-04-02；提交 `62cc4a8cb7d2c7b7e2797f4c7480f00d0774e768`。
- 官方 GitHub assets API 与下载 ZIP 的 SHA-256 均为 `6204911d777ac03e514b90126568ef6faa1d477779b31c536b142dba92f4a2c0`。ZIP 内置为资源，使用前再次校验；完整保留原版文件和许可证，未改动官方二进制。
- [README](https://github.com/Metabolix/HackBGRT/blob/v2.6.0/README.md)、[配置](https://github.com/Metabolix/HackBGRT/blob/v2.6.0/config.txt)、[shim](https://github.com/Metabolix/HackBGRT/blob/v2.6.0/shim.md)、[Setup](https://github.com/Metabolix/HackBGRT/blob/v2.6.0/src/Setup.cs)、[EFI 项](https://github.com/Metabolix/HackBGRT/blob/v2.6.0/src/EfiBootEntries.cs)。

HackBGRT 在运行时更新 BGRT 并继续引导 Windows，不能消除它启动前的厂商图。官方明确提示不可启动风险。Secure Boot 可通过 shim 手动信任文件哈希；GUI 不清空密钥或导入整张证书。初版阻止系统盘加密，不照搬广泛禁用 TPM 相关安全功能的建议。

## 调用边界

原版 EnableBCDEdit 的若干 /set 命令没有检查失败返回；DisableBCDEdit 按输出是否含 HackBGRT 判断归属，无法满足本应用的准确凭据要求。因此不调用 enable-*、disable-*、uninstall、覆盖引导器或 allow-* 参数，只调用固定的 `batch is-elevated install arch=x64 esp=<已确认卷 GUID>` 文件安装。

独立项遵循原版 MakeOwnEntry 思路：严格解析当前活动 Windows GPT 设备路径，核对系统分区 GUID，复制节点、替换为 `\EFI\HackBGRT\loader.efi`，清除 Microsoft 加载参数以适配 shim，原项字节不变。确认槽位不存在、保存原状态后写新项并读回比对，再追加到 BootOrder 末尾并读回比对。未知或额外启动项、BootNext、多 ESP、非当前默认 Windows、非 x64 或无法检查加密均停止。

完整备份和计划先 flush 到磁盘，再允许文件复制。核验官方 loader、shim、MOKManager、证书、配置、图像像素及尺寸后才能注册。普通 GUI 和播放器不提权。辅助进程通过 UAC 操作受管理员保护的 ProgramData 记录，撤销核对原 Windows 项和准确的新项，不导入旧 BootOrder，不写 Windows 启动器。

恢复先移除自己的 BootOrder 引用再删除自己的 Boot####；后来添加的外来项和相对顺序保留。BootNext 引用自己、槽位被占、Windows 项变化或记录损坏均停止，EFI 文件仍保留。原启动器备份用于人工参考，不自动覆盖当前文件。重装只接受归属和哈希一致的已恢复目录。

## 图片与证据

直接编码 54 字节头、24 位、BI_RGB、正高度、DWORD 行对齐的 BMP3，画布最大 600×360。输入静态 PNG / JPEG / BMP，16 MB、2000 万像素，透明合成到黑底。保留原固件显示模式，不强制切换 GOP 分辨率。

官方 InstallFiles 方法在普通临时目录组成的模拟 ESP 中运行，没有调用 Main/Run、ESP 探测或固件 API。发现 GDI 写 biSizeImage=0，生产校验曾误拒绝；按微软 [BITMAPINFOHEADER](https://learn.microsoft.com/windows/win32/api/wingdi/ns-wingdi-bitmapinfoheader) 的合法 BI_RGB 零值规则修复，仍核验实际长度、尺寸和像素。六个实际官方输出通过生产校验，改坏 EFI 文件立即拒绝注册。

[微软 BitLocker 恢复说明](https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview) 将启动管理器变化列为恢复触发条件；[Suspend-BitLocker](https://learn.microsoft.com/powershell/module/bitlocker/suspend-bitlocker) 说明暂停不会解密。保护 Off 不能证明未加密；使用 CIM GetConversionStatus 的转换状态与加密百分比，未知停止，不读取密钥。

没有虚拟 / 真实固件首次启动测试，临时目录复制和模拟 NVRAM 不等于 UEFI 运行验证。原版 EFI 应用、shim、设备固件和断电行为仍有验证边界。先单次试启动、保留 Windows 默认与恢复 U 盘是初版约束。
