namespace WinBootVideo;

internal static class ApplicationGuide
{
    internal const string Overview = """
        WinBootVideo 使用说明
        作者：0x3fffff

        运行环境
        Windows 11 x64。绿色便携版需安装 x64 .NET 8 Desktop Runtime；仅安装 .NET Runtime 不包含桌面组件。
        解压后运行 WinBootVideo.exe，保留同目录的文件。检查或转换视频需要另备 FFmpeg / ffprobe，直接预览不需要。
        软件不会自动下载工具、修改密码或自动重启电脑。

        登录动画：选择视频 → 播放设置 → 预览与启用
        1. 首页选择“登录动画”，选取或拖入本地视频。
        2. 推荐 MP4 / H.264、8 位 yuv420p、SDR、1080p / 30 fps、5–15 秒，音轨为 AAC 或无音轨。
        3. 格式检查可选；不兼容时展开转换工具，保存为新文件。画面保留比例并加黑边，透明视频、HEVC、AV1、10 位和 HDR 素材建议先处理。
        4. 调整屏幕、音量；淡出和等待上限在“高级设置”中。
        5. 保存并全屏预览。按 Esc / 空格可跳过；加载失败或超时不会解锁启用。
        6. 确认效果后启用。仅设置当前用户，下次登录播放；锁屏解锁不重复。
        保存预览会复制视频并暂停已有的动画，成功后须重新启用。原文件可删除；应用数据目录中的媒体、副本程序和配置须保留。

        登录动画恢复
        在向导底部点击“恢复普通登录”。如果登录后无法进入桌面：
        按 Ctrl + Shift + Esc 打开任务管理器 → 运行新任务 → 执行：
        %LOCALAPPDATA%\WinBootVideo\恢复普通登录.cmd
        正常播放结束后宿主与播放器退出，设置窗口可关闭。真实冷启动时序仍需在本机验证；预览成功不代表所有设备的登录时序相同。

        开机 Logo：检查电脑 → 准备图片 → 确认与安装 → 首次启动与生效
        先运行管理员只读检查。支持范围和阻止原因显示在界面中；检查不通过时，不要删除启动项或关闭安全功能绕过。
        PNG / JPEG / BMP 会自动转为黑底、保留比例的 24 位 BMP3。导出准备包，准备恢复 U 盘、帐户密码及另一设备上的恢复说明。
        安装创建独立 HackBGRT 启动项，Windows Boot Manager 保持默认；不刷 BIOS。
        安装成功不代表每次启动已生效。先从固件 Boot Menu 单次选择 HackBGRT，完成必要的 shim 文件哈希确认，确认 Logo 与登录正常，再在 BIOS 中将 HackBGRT 设为第一启动项并保存。
        保留 Secure Boot、TPM、UEFI / CSM 和 SATA 设置。首次蓝色 Verification failed 提示的完整按键步骤在 Logo 向导第四步中。
        Logo 已保存到 EFI，不依赖软件常驻。验证成功后可删除原图和准备包；保留 ProgramData 中的恢复资料。
        恢复时先将 Windows Boot Manager 放回第一位，进入 Windows 后点击“恢复原 Logo”。

        数据位置
        登录动画：%LOCALAPPDATA%\WinBootVideo
        Logo 恢复资料：%ProgramData%\WinBootVideo-BootLogo
        启用期间不要删除这些目录。上传故障报告前检查是否包含个人路径和信息。

        兼容性
        软件不会自动更改 BIOS 默认顺序。不同固件、加密系统、多系统和游戏反作弊环境各有兼容性限制；详细支持范围见 README.md，验证记录见 QA.md。
        """;
}
