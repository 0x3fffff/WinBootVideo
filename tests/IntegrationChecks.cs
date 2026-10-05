using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WinBootVideo.Core;

internal static class IntegrationChecks
{
    public static async Task Run(string bundle, string tools, string fixture, Action<bool, string> check)
    {
        string work = Path.Combine(Path.GetTempPath(), "WinBootVideo-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string input = Path.Combine(work, "源 video & test.mp4");
            string output = Path.Combine(work, "兼容 video.mp4");
            string ffmpeg = Path.Combine(tools, "ffmpeg.exe");
            string probe = Path.Combine(tools, "ffprobe.exe");
            var generated = await MediaTools.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-n", "-f", "lavfi", "-i", "color=c=blue:s=640x480:r=24:d=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=12", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input }, TimeSpan.FromSeconds(30), CancellationToken.None);
            check(generated.ExitCode == 0, "生成独立测试素材");
            await MediaTools.ConvertAsync(ffmpeg, input, output, 2, true, false, CancellationToken.None);
            var metadata = await MediaTools.ProbeAsync(output, probe, CancellationToken.None);
            check(metadata.Recommended && metadata.Width == 1920 && metadata.Height == 1080 && metadata.Fps == 30 && metadata.Duration <= 2.1 && metadata.AudioCodec == "无音轨", "真实转码：1080p、30 fps、H.264、SDR、限长、移除音轨");
            string withAudio = Path.Combine(work, "audio-compatible.mp4");
            await MediaTools.ConvertAsync(ffmpeg, input, withAudio, 1, false, false, CancellationToken.None);
            var audioProbe = await MediaTools.RunAsync(probe, new[] { "-v", "error", "-show_streams", "-of", "json", withAudio }, TimeSpan.FromSeconds(10), CancellationToken.None);
            using (var audioDoc = JsonDocument.Parse(audioProbe.Output))
            {
                var audio = audioDoc.RootElement.GetProperty("streams").EnumerateArray().First(x => x.GetProperty("codec_type").GetString() == "audio");
                check(audio.GetProperty("codec_name").GetString() == "aac" && audio.GetProperty("channels").GetInt32() == 2 && audio.GetProperty("sample_rate").GetString() == "48000", "真实音频转换：AAC、双声道、48 kHz");
            }
            string hdr = Path.Combine(work, "pq-hdr.mp4");
            var hdrGeneration = await MediaTools.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-n", "-f", "lavfi", "-i", "color=c=gray:s=640x360:r=24:d=1", "-vf", "zscale=pin=bt709:tin=bt709:min=bt709:rin=limited:p=bt2020:t=smpte2084:m=bt2020nc:r=limited,format=yuv420p10le", "-c:v", "libx265", "-preset", "ultrafast", "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", hdr }, TimeSpan.FromSeconds(30), CancellationToken.None);
            if (hdrGeneration.ExitCode != 0) throw new Exception("HDR fixture: " + MediaTools.Tail(hdrGeneration.Error));
            check((await MediaTools.ProbeAsync(hdr, probe, CancellationToken.None)).Hdr, "真实 10 位 PQ 素材被识别为 HDR");
            string sdr = Path.Combine(work, "hdr-to-sdr.mp4");
            await MediaTools.ConvertAsync(ffmpeg, hdr, sdr, 1, true, true, CancellationToken.None);
            check((await MediaTools.ProbeAsync(sdr, probe, CancellationToken.None)).Recommended, "实际 HDR 色调映射输出 H.264 / 8 位 / SDR");
            bool refused = false;
            try { await MediaTools.ConvertAsync(ffmpeg, input, output, 2, true, false, CancellationToken.None); } catch (IOException) { refused = true; }
            check(refused && File.Exists(output), "转码不覆盖已有输出");

            var paths = new AppPaths(Path.Combine(work, "player-data"));
            var settings = new Settings { VideoPath = output, VideoSha256 = await SettingsStore.HashAsync(output, CancellationToken.None), Volume = 0, FadeOutMilliseconds = 100, StartupTimeoutSeconds = 3, MaxDurationSeconds = 10, CoverOtherScreens = false };
            new SettingsStore(paths).Write(settings);
            string receipt = Path.Combine(paths.DataDirectory, "receipt.json");
            string host = Path.Combine(bundle, "WinBootVideo.Host.exe");
            string[] arguments = { "--preview", "--windowed", "--data-dir", paths.DataDirectory, "--receipt", receipt };
            var playback = await MediaTools.RunAsync(host, arguments, TimeSpan.FromSeconds(15), CancellationToken.None);
            var report = JsonSerializer.Deserialize<WinBootVideo.PreviewReceipt>(File.ReadAllText(receipt), Settings.JsonOptions)!;
            check(playback.ExitCode == 0 && report.Opened && report.PlaybackAdvanced && report.Succeeded, "真实 WPF 视频加载、播放与退出");

            await VisibilityGateChecks.RunIsolated(output, check);

            string corrupt = Path.Combine(work, "broken.mp4"); File.WriteAllText(corrupt, "This is not a video.");
            settings.VideoPath = corrupt; new SettingsStore(paths).Write(settings);
            File.Delete(receipt);
            var broken = await MediaTools.RunAsync(host, arguments, TimeSpan.FromSeconds(12), CancellationToken.None);
            report = File.Exists(receipt) ? JsonSerializer.Deserialize<WinBootVideo.PreviewReceipt>(File.ReadAllText(receipt), Settings.JsonOptions)! : new();
            check(broken.ExitCode != 0 && !report.Succeeded, "损坏视频不能通过预览验证，播放器按时退出");

            settings.VideoPath = input; settings.MaxDurationSeconds = 5; settings.StartupTimeoutSeconds = 2;
            new SettingsStore(paths).Write(settings); File.Delete(receipt);
            var deadline = Stopwatch.StartNew();
            var limited = await MediaTools.RunAsync(host, arguments, TimeSpan.FromSeconds(9), CancellationToken.None);
            check(limited.ExitCode == 31 && deadline.Elapsed.TotalSeconds < 8, "长视频在总上限内截断，不获得成功验证");

            string dummyBundle = Path.Combine(work, "hung-fixture"); Directory.CreateDirectory(dummyBundle);
            File.Copy(host, Path.Combine(dummyBundle, "WinBootVideo.Host.exe"));
            File.Copy(host + ".config", Path.Combine(dummyBundle, "WinBootVideo.Host.exe.config"));
            File.Copy(fixture, Path.Combine(dummyBundle, "WinBootVideo.exe"));
            foreach (bool ready in new[] { false, true })
            {
                var info = new ProcessStartInfo(Path.Combine(dummyBundle, "WinBootVideo.Host.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string arg in arguments) info.ArgumentList.Add(arg);
                string pidFile = Path.Combine(work, "fixture-pid.txt");
                info.Environment["WINBOOTVIDEO_FIXTURE_PID"] = pidFile;
                info.Environment["WINBOOTVIDEO_FIXTURE_READY"] = ready ? "1" : "0";
                var timer = Stopwatch.StartNew();
                using var parent = Process.Start(info)!;
                double target = ready ? 5 : WinBootVideo.Shared.StartupLimits.ColdStartMilliseconds / 1000d;
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(target + 5));
                try { await parent.WaitForExitAsync(limit.Token); }
                catch { parent.Kill(true); throw; }
                int childId = int.Parse(File.ReadAllText(pidFile));
                bool childExited;
                try { using var child = Process.GetProcessById(childId); childExited = child.HasExited; } catch (ArgumentException) { childExited = true; }
                check(parent.ExitCode == 31 && childExited && timer.Elapsed.TotalSeconds >= target && timer.Elapsed.TotalSeconds < target + 3,
                    ready ? "就绪后卡死：独立宿主按总上限终止自己启动的子进程" : "就绪前卡死：独立宿主按可见后的冷启动期限终止自己启动的子进程");
            }

            settings.VideoPath = input; settings.MaxDurationSeconds = 15; settings.StartupTimeoutSeconds = 4;
            new SettingsStore(paths).Write(settings);
            string log = Path.Combine(paths.LogDirectory, "host.log"); File.Delete(log);
            var guardianInfo = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in arguments) guardianInfo.ArgumentList.Add(arg);
            using (var parent = Process.Start(guardianInfo)!)
            {
                try
                {
                    var limit = Stopwatch.StartNew();
                    string? line = null;
                    while (line == null && limit.Elapsed.TotalSeconds < 5)
                    {
                        if (File.Exists(log)) line = File.ReadAllLines(log).FirstOrDefault(x => x.Contains("player-start: "));
                        if (line == null) await Task.Delay(100);
                    }
                    if (line == null) throw new Exception("Cannot observe own child PID.");
                    int childId = int.Parse(line.Split("player-start: ")[1]);
                    using var child = Process.GetProcessById(childId);
                    await Task.Delay(900); parent.Kill(); await parent.WaitForExitAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    await child.WaitForExitAsync(timeout.Token);
                    check(child.ExitCode == 31, "宿主意外退出后播放器守护线程主动退出，避免遮罩遗留");
                }
                finally { if (!parent.HasExited) parent.Kill(true); }
            }
            File.Delete(Path.Combine(dummyBundle, "WinBootVideo.exe"));
            var missing = await MediaTools.RunAsync(Path.Combine(dummyBundle, "WinBootVideo.Host.exe"), arguments, TimeSpan.FromSeconds(4), CancellationToken.None);
            check(missing.ExitCode == 30, "播放器文件缺失时轻量宿主仍可运行并退出");
        }
        finally { Directory.Delete(work, true); }
    }
}
