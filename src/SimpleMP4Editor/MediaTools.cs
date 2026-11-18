using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SimpleMP4Editor;

internal static class MediaTools
{
    internal static async Task<byte[]> RunAsync(string name, IEnumerable<string> args, CancellationToken token,
        Action<string>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", name + ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException($"{name} を起動できません。");
        using var stop = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        using var result = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync();
        if (progress == null)
            await process.StandardOutput.BaseStream.CopyToAsync(result).ConfigureAwait(false);
        else
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) progress(line);
        await process.WaitForExitAsync().ConfigureAwait(false);
        string message = (await errors.ConfigureAwait(false)).Trim();
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new IOException(message.Length > 1600 ? message[^1600..] : message.Length > 0 ? message : $"{name} が終了コード {process.ExitCode} を返しました。");
        return result.ToArray();
    }

    private static double Value(JsonElement node, string key, double fallback = 0) =>
        node.TryGetProperty(key, out var item) && double.TryParse(item.ToString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;

    private static double Ratio(string? text, char separator, double fallback)
    {
        string[]? parts = text?.Split(separator);
        return parts?.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var b) && a > 0 && b > 0 ? a / b : fallback;
    }

    internal static async Task<MediaInfo> ProbeAsync(string path, CancellationToken token)
    {
        byte[] data = await RunAsync("ffprobe", ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], token);
        using var json = JsonDocument.Parse(data);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video"
            && (!s.TryGetProperty("disposition", out var d) || Value(d, "attached_pic") == 0));
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("映像トラックがありません。");
        var audio = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        var format = json.RootElement.GetProperty("format");
        double offset = Value(video, "start_time") - Value(format, "start_time");
        double duration = Value(video, "duration", Value(format, "duration") - offset);
        int width = (int)Value(video, "width"), height = (int)Value(video, "height");
        double sar = Ratio(video.TryGetProperty("sample_aspect_ratio", out var ratio) ? ratio.GetString() : null, ':', 1);
        width = (int)Math.Round(width * sar);
        int angle = 0;
        if (video.TryGetProperty("side_data_list", out var side))
            foreach (var entry in side.EnumerateArray())
                if (entry.TryGetProperty("rotation", out _)) angle = (int)Math.Round(Value(entry, "rotation"));
        if (Math.Abs(angle) % 180 == 90) (width, height) = (height, width);
        if (width < 1 || height < 1 || duration <= 0) throw new InvalidDataException("映像のサイズまたは長さを取得できません。");
        double fps = Ratio(video.GetProperty("avg_frame_rate").GetString(), '/', 30);
        bool hasAudio = audio.ValueKind != JsonValueKind.Undefined;
        return new(Path.GetFullPath(path), width, height, duration, Math.Clamp(fps, 1, 240), hasAudio,
            angle != 0 || sar != 1 || Math.Abs(offset) > 0.001 || video.GetProperty("codec_name").GetString() != "h264",
            (int)Value(video, "index"), hasAudio ? (int)Value(audio, "index") : -1, offset);
    }

    // FFmpeg autorotates before this filter. All editing coordinates use square display pixels.
    private static string DisplayScale(MediaInfo m) => $"scale={m.Width}:{m.Height},setsar=1";

    internal static async Task<byte[]> StillAsync(MediaInfo media, double position, int maxSide, CancellationToken token)
    {
        double seek = Math.Clamp(position, 0, Math.Max(0, media.Duration - 1 / media.Fps));
        string filter = $"{DisplayScale(media)},scale='min(iw,{maxSide})':'min(ih,{maxSide})':force_original_aspect_ratio=decrease";
        return await RunAsync("ffmpeg", ["-v", "error", "-nostdin", "-ss", Output.Number(Math.Max(0, seek + media.VideoOffset)),
            "-i", media.Path, "-map", $"0:{media.VideoIndex}", "-frames:v", "1", "-vf", filter, "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "3", "pipe:1"], token);
    }

    internal static async Task MakeProxyAsync(MediaInfo media, string file, CancellationToken token)
    {
        string filter = $"{DisplayScale(media)},scale='min(iw,1280)':'min(ih,1280)':force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1";
        var args = new List<string> { "-v", "error", "-nostdin", "-y", "-ss", Output.Number(Math.Max(0, media.VideoOffset)),
            "-i", media.Path, "-map", $"0:{media.VideoIndex}" };
        if (media.HasAudio) args.AddRange(["-map", $"0:{media.AudioIndex}"]);
        args.AddRange(["-t", Output.Number(media.Duration), "-vf", filter, "-c:v", "libx264", "-preset", "ultrafast",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", file]);
        await RunAsync("ffmpeg", args, token);
    }

    internal static async Task ExportAsync(IReadOnlyList<Clip> clips, bool contain, string destination,
        IProgress<double>? progress, CancellationToken token, bool overwrite = false)
    {
        if (clips.Count == 0) throw new InvalidOperationException("クリップを追加してください。");
        destination = Path.GetFullPath(destination);
        if (clips.Any(c => string.Equals(c.Media.Path, destination, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("入力動画と異なる保存先を選んでください。");
        string temp = Path.Combine(Path.GetDirectoryName(destination)!, $".simplemp4-{Guid.NewGuid():N}.mp4");
        try
        {
            var (width, height) = Output.Size(clips[0]);
            double fps = clips[0].Media.Fps;
            string rate = Output.Number(fps);
            bool audio = clips.Any(c => c.Media.HasAudio);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-progress", "pipe:1" };
            var filters = new List<string>();
            var join = new StringBuilder();
            long totalFrames = 0;
            for (int i = 0; i < clips.Count; i++)
            {
                Clip c = clips[i];
                var m = c.Media;
                // Keep a frame before the cut, including cuts made inside the last frame.
                double from = Math.Max(0, c.Start - Math.Max(1, 2 / m.Fps));
                double seek = Math.Max(0, from + m.VideoOffset);
                double lead = c.Start + m.VideoOffset - seek;
                args.AddRange(["-ss", Output.Number(seek), "-t", Output.Number(lead + c.Length + 1 / m.Fps), "-i", m.Path]);
                long frames = Math.Max(1, (long)Math.Round(c.Length * fps));
                long samples = (long)Math.Round((totalFrames + frames) * 48000 / fps) - (long)Math.Round(totalFrames * 48000 / fps);
                totalFrames += frames;
                string scale = contain ? $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2"
                    : $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}:exact=1";
                var r = c.Crop;
                // Crop in full chroma resolution so odd border positions are respected.
                filters.Add($"[{i}:{m.VideoIndex}]{DisplayScale(m)},format=yuv444p,crop={r.Width}:{r.Height}:{r.Left}:{r.Top}:exact=1," +
                    $"{scale},setsar=1,setpts=PTS-{Output.Number(lead)}/TB,tpad=stop_mode=clone:stop_duration=1," +
                    $"fps={rate}:start_time=0:round=near,trim=end_frame={frames},setpts=N/({rate}*TB),format=yuv420p[v{i}]");
                join.Append($"[v{i}]");
                if (audio)
                {
                    string source = m.HasAudio
                        ? $"[{i}:{m.AudioIndex}]asetpts=PTS-{Output.Number(lead)}/TB,aresample=48000:async=1:first_pts=0,aformat=channel_layouts=stereo,apad"
                        : "anullsrc=r=48000:cl=stereo";
                    filters.Add($"{source},atrim=end_sample={samples},asetpts=N/SR/TB[a{i}]");
                    join.Append($"[a{i}]");
                }
            }
            filters.Add($"{join}concat=n={clips.Count}:v=1:a={(audio ? 1 : 0)}[video]{(audio ? "[audio]" : "")}");
            args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[video]"]);
            if (audio) args.AddRange(["-map", "[audio]", "-c:a", "aac", "-b:a", "192k"]);
            args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
                "-r", rate, "-movflags", "+faststart", temp]);
            double duration = totalFrames / fps;
            await RunAsync("ffmpeg", args, token, line =>
            {
                if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out long us))
                    progress?.Report(Math.Clamp(us / 1e6 / duration, 0, 1));
            });
            token.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite);
        }
        finally { File.Delete(temp); }
    }
}
