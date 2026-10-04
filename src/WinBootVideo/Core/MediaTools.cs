using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinBootVideo.Core;

public sealed class VideoInfo
{
    public string Codec { get; set; } = "";
    public string PixelFormat { get; set; } = "";
    public string AudioCodec { get; set; } = "无音轨";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Duration { get; set; }
    public double Fps { get; set; }
    public bool Hdr { get; set; }
    public bool Recommended => Codec == "h264" && PixelFormat == "yuv420p" && !Hdr && (AudioCodec == "aac" || AudioCodec == "无音轨");
    public string Summary => string.Format(CultureInfo.InvariantCulture, "{0} × {1}  ·  {2:0.##} fps  ·  {3:0.0} 秒\n{4} / {5}  ·  音频 {6}{7}", Width, Height, Fps, Duration, Codec, PixelFormat, AudioCodec, Hdr ? "  ·  HDR" : "");
}

public static class MediaTools
{
    public static string? FindTool(string name, string configured = "")
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe"), Path.Combine(AppContext.BaseDirectory, name + ".exe") };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.Combine(x.Trim('"'), name + ".exe")));
        return candidates.FirstOrDefault(File.Exists);
    }
    public static async Task<VideoInfo> ProbeAsync(string path, string tool, CancellationToken token)
    {
        Settings.ValidateLocalVideo(path);
        var result = await RunAsync(tool, new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path }, TimeSpan.FromSeconds(20), token);
        if (result.ExitCode != 0) throw new InvalidDataException("视频检查失败：" + Tail(result.Error));
        return ParseProbe(result.Output);
    }
    public static VideoInfo ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(x => Get(x, "codec_type") == "video" && !(x.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1));
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("文件没有可播放的视频轨道。");
        var audio = streams.FirstOrDefault(x => Get(x, "codec_type") == "audio");
        string duration = Get(video, "duration");
        if (!double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && doc.RootElement.TryGetProperty("format", out var format))
            double.TryParse(Get(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        string[] rate = Get(video, "avg_frame_rate").Split('/');
        double fps = 0;
        if (rate.Length == 2 && double.TryParse(rate[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) && double.TryParse(rate[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) && denominator > 0) fps = numerator / denominator;
        return new VideoInfo { Codec = Get(video, "codec_name"), PixelFormat = Get(video, "pix_fmt"), Width = video.GetProperty("width").GetInt32(), Height = video.GetProperty("height").GetInt32(), Duration = seconds, Fps = fps,
            AudioCodec = audio.ValueKind == JsonValueKind.Undefined ? "无音轨" : Get(audio, "codec_name"), Hdr = Get(video, "color_transfer") is "smpte2084" or "arib-std-b67" };
    }
    private static string Get(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.ToString() : "";

    public static string[] ConversionArguments(string input, string output, double seconds, bool mute, bool hdr)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 80) throw new ArgumentOutOfRangeException(nameof(seconds), "转码时长须大于 0 且不超过 80 秒。");
        string toneMap = hdr ? "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv," : "";
        string filter = toneMap + "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p";
        var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-i", input, "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-map", "0:V:0" };
        if (!mute) args.AddRange(new[] { "-map", "0:a:0?" });
        args.AddRange(new[] { "-vf", filter, "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-profile:v", "high", "-level:v", "4.1", "-pix_fmt", "yuv420p" });
        if (mute) args.Add("-an"); else args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2" });
        args.AddRange(new[] { "-movflags", "+faststart", "-metadata:s:v:0", "rotate=0", output });
        return args.ToArray();
    }
    public static async Task ConvertAsync(string tool, string input, string output, double seconds, bool mute, bool hdr, CancellationToken token)
    {
        Settings.ValidateLocalVideo(input);
        if (File.Exists(output)) throw new IOException("输出文件已存在；请换一个文件名。");
        try
        {
            var result = await RunAsync(tool, ConversionArguments(input, output, seconds, mute, hdr), TimeSpan.FromMinutes(10), token);
            if (result.ExitCode != 0) throw new InvalidDataException("转码失败。HDR 素材需要含 zscale 的 FFmpeg。\n" + Tail(result.Error));
        }
        catch { if (File.Exists(output)) File.Delete(output); throw; }
    }
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("无法启动工具。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            if (!token.IsCancellationRequested) throw new TimeoutException("工具等待超时。");
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
    public static string Tail(string text) => text.Length > 1800 ? text.Substring(text.Length - 1800) : text;
}
public sealed record ProcessResult(int ExitCode, string Output, string Error);
