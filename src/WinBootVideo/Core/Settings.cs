using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace WinBootVideo.Core;

public sealed class Settings
{
    public int SchemaVersion { get; set; } = 1;
    public string VideoPath { get; set; } = "";
    public string VideoSha256 { get; set; } = "";
    public double Volume { get; set; } = 0.35;
    public int FadeOutMilliseconds { get; set; } = 650;
    public int MaxDurationSeconds { get; set; } = 45;
    public int StartupTimeoutSeconds { get; set; } = 8;
    public bool CoverOtherScreens { get; set; } = true;
    public string ScreenDevice { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string VerifiedVideoSha256 { get; set; } = "";
    public Settings Copy() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public void Validate(bool requireVideo = true)
    {
        if (SchemaVersion != 1) throw new InvalidDataException("配置版本不受支持。");
        if (!double.IsFinite(Volume) || Volume < 0 || Volume > 1) throw new InvalidDataException("音量须在 0–100% 之间。");
        if (FadeOutMilliseconds < 0 || FadeOutMilliseconds > 3000) throw new InvalidDataException("淡出须在 0–3000 毫秒之间。");
        if (MaxDurationSeconds < 5 || MaxDurationSeconds > 90) throw new InvalidDataException("总等待上限须在 5–90 秒之间。");
        if (StartupTimeoutSeconds < 2 || StartupTimeoutSeconds > 15 || StartupTimeoutSeconds >= MaxDurationSeconds)
            throw new InvalidDataException("初始化等待须在 2–15 秒之间，并小于总等待上限。");
        if (requireVideo) ValidateLocalVideo(VideoPath);
    }
    public static void ValidateLocalVideo(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("请选择本地磁盘上的视频文件。");
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType == DriveType.Network) throw new InvalidDataException("登录动画不使用网络磁盘。");
        if (!File.Exists(path)) throw new FileNotFoundException("视频文件不存在。", path);
        var size = new FileInfo(path).Length;
        if (size <= 0 || size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("视频须为非空文件，初版最大支持 2 GB。");
    }
}

public sealed class AppPaths
{
    public string DataDirectory { get; }
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string BackupFile => Path.Combine(DataDirectory, "shell-backup.json");
    public string AppDirectory => Path.Combine(DataDirectory, "app");
    public string MediaDirectory => Path.Combine(DataDirectory, "media");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public AppPaths(string? dataDirectory = null) => DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinBootVideo"));
}

public sealed class SettingsStore
{
    private readonly AppPaths paths;
    public SettingsStore(AppPaths paths) => this.paths = paths;
    public Settings Load() => File.Exists(paths.SettingsFile) ? (JsonSerializer.Deserialize<Settings>(File.ReadAllText(paths.SettingsFile), Settings.JsonOptions) ?? throw new InvalidDataException("配置为空。")) : new Settings();
    public void Write(Settings settings) { settings.Validate(); AtomicJson.Write(paths.SettingsFile, settings); }
    public async Task<Settings> ImportAndSaveAsync(Settings draft, CancellationToken token)
    {
        draft.Validate();
        Directory.CreateDirectory(paths.MediaDirectory);
        string hash = await HashAsync(draft.VideoPath, token);
        string extension = Path.GetExtension(draft.VideoPath).ToLowerInvariant();
        if (extension.Length > 12 || extension.Length < 2) extension = ".mp4";
        string target = Path.Combine(paths.MediaDirectory, hash + extension);
        if (!string.Equals(Path.GetFullPath(draft.VideoPath), target, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var input = new FileStream(draft.VideoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, token);
                if (await HashAsync(temporary, token) != hash) throw new IOException("复制期间视频发生变化，请重新选择。");
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        var saved = draft.Copy();
        saved.VideoPath = target;
        saved.VideoSha256 = hash;
        if (saved.VerifiedVideoSha256 != hash) saved.VerifiedVideoSha256 = "";
        Write(saved);
        return saved;
    }
    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
}

public static class AtomicJson
{
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Settings.JsonOptions));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
