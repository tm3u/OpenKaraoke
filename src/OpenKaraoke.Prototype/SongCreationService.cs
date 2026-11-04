using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Text;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public sealed record YouTubeVideo(string Url, string Title, double Duration);
public sealed record CreationResult(string Package, string Title, string Artist, double Duration);

public sealed class SongCreationService(string root, AiRuntime runtime, SongPackageService packages)
{
    private static readonly HttpClient ClipClient = new() { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    public static string NormalizeYouTubeUrl(string value)
    {
        value=value.Trim();
        if (!value.Contains("://")) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0)
            throw new ArgumentException("請輸入有效的 YouTube 影片網址。");
        string id = "";
        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host == "youtu.be" && segments.Length == 1) id = segments[0];
        if (new[] { "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com" }.Contains(uri.Host))
        {
            if (segments.Length == 2 && segments[0] == "clip" && Regex.IsMatch(segments[1], "^[A-Za-z0-9_-]{10,128}$"))
                return "https://www.youtube.com/clip/" + segments[1];
            if (segments.Length == 2 && segments[0] is "shorts" or "live" or "embed") id = segments[1];
            if (uri.AbsolutePath == "/watch")
                id = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == "v").Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault() ?? "";
        }
        if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$")) throw new ArgumentException("請輸入單一 YouTube 影片網址。");
        return "https://www.youtube.com/watch?v=" + id;
    }
    public static string FullVideoFromClipPage(string html)
    {
        var endpoint = Regex.Match(html, "\"currentVideoEndpoint\"\\s*:");
        if (!endpoint.Success) throw new InvalidOperationException("無法取得片段的完整影片，請改貼完整影片網址。");
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html[(endpoint.Index + endpoint.Length)..]));
            using var document = JsonDocument.ParseValue(ref reader);
            string id = document.RootElement.GetProperty("watchEndpoint").GetProperty("videoId").GetString()!;
            return NormalizeYouTubeUrl("https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new InvalidOperationException("無法取得片段的完整影片，請改貼完整影片網址。", error); }
    }
    public static async Task<string> ResolveFullVideoUrlAsync(string value, CancellationToken cancellation = default)
    {
        string normalized = NormalizeYouTubeUrl(value);
        if (!new Uri(normalized).AbsolutePath.StartsWith("/clip/", StringComparison.Ordinal)) return normalized;
        using var request = new HttpRequestMessage(HttpMethod.Get, normalized);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        using var response = await ClipClient.SendAsync(request, cancellation); response.EnsureSuccessStatusCode();
        return FullVideoFromClipPage(await response.Content.ReadAsStringAsync(cancellation));
    }

    private string[] DownloadArguments => ["--ignore-config", "--no-plugin-dirs", "--no-playlist", "--no-colors", "--socket-timeout", "20", "--retries", "2", "--extractor-retries", "1",
        "--js-runtimes", "deno:" + runtime.Deno, "--ffmpeg-location", runtime.MediaDirectory];

    public async Task<YouTubeVideo> ReadVideoAsync(string url, IProgress<CreationProgress>? progress, CancellationToken cancellation)
    {
        string normalized = await ResolveFullVideoUrlAsync(url, cancellation);
        await runtime.EnsureDownloadToolsAsync(progress, cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        string json;
        try { json = await ToolProcess.RunAsync(runtime.Downloader, [.. DownloadArguments, "--dump-single-json", "--skip-download", "--no-warnings", "--", normalized], timeout.Token); }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){throw new TimeoutException("讀取影片逾時，請稍後重試。");}
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        if (item.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("目前不支援直播影片，請使用已結束的影片。");
        if (!item.TryGetProperty("duration", out var duration) || !duration.TryGetDouble(out double seconds) || seconds <= 0 || !double.IsFinite(seconds))
            throw new InvalidOperationException("無法讀取影片長度，請使用一般 YouTube 影片。");
        return new(normalized, item.GetProperty("title").GetString() ?? "YouTube 影片", seconds);
    }

    public async Task<CreationResult> CreateAsync(YouTubeVideo video, string title, string artist, string folder,
        IProgress<CreationProgress>? progress, CancellationToken cancellation)
    {
        SongPackageService.ValidateNames(title, artist);
        string normalized = await ResolveFullVideoUrlAsync(video.Url, cancellation);
        Directory.CreateDirectory(folder);
        await runtime.EnsureAsync(progress, cancellation);
        progress?.Report(new("download", "下載影片", 0));
        await ToolProcess.RunAsync(runtime.Downloader, [.. DownloadArguments, "--newline", "--progress", "--progress-template", "download:OPENKARAOKE_DOWNLOAD:%(progress._percent_str)s",
            "-f", "bv*+ba/b", "--merge-output-format", "mkv", "--remux-video", "mkv", "-o", Path.Combine(folder, "source.%(ext)s"), "--", normalized], cancellation,
            line => { var match = Regex.Match(line, @"OPENKARAOKE_DOWNLOAD:\s*([\d.]+)%"); if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent)) progress?.Report(new("download", "下載影片", percent)); });
        string source = Path.Combine(folder, "source.mkv");
        if (!File.Exists(source)) throw new IOException("影片下載後找不到完整檔案。");
        return await SeparateAndPackageAsync(source, title, artist, folder, progress, cancellation);
    }

    public async Task<CreationResult> SeparateAndPackageAsync(string source, string title, string artist, string folder,
        IProgress<CreationProgress>? progress, CancellationToken cancellation)
    {
        SongPackageService.ValidateNames(title, artist);
        await runtime.EnsureAsync(progress, cancellation);
        Directory.CreateDirectory(folder);
        string audio = Path.Combine(folder, "original.wav"), accompaniment = Path.Combine(folder, "accompaniment.wav"), vocals = Path.Combine(folder, "vocals.wav");
        string referenceFile = Path.Combine(folder, ScoringReferenceService.AttachmentName);
        string output = Path.Combine(folder, SongPackageService.Filename(title, artist));
        try
        {
            progress?.Report(new("separation", "準備音訊"));
            await ToolProcess.RunAsync(Path.Combine(root, "tools", "media", "ffmpeg.exe"),
                ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", source, "-map", "0:a:0", "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_f32le", audio], cancellation);
            progress?.Report(new("separation", "分離人聲"));
            await ToolProcess.RunAsync(runtime.Python, [runtime.Adapter, "--model", runtime.Model, "--input", audio, "--output", accompaniment, "--vocals", vocals], cancellation,
                line =>
                {
                    var match = Regex.Match(line, @"^\s*(\d+)%\|");
                    if (match.Success) progress?.Report(new("separation", "分離人聲", int.Parse(match.Groups[1].Value)));
                });
            var scoring = new ScoringReferenceService(root, folder, runtime);
            (await scoring.AnalyzeVocalsAsync(vocals, folder, progress, cancellation, audio)).Write(referenceFile);
            string[] attachment = ["-attach", referenceFile, "-metadata:s:t:0", "mimetype=application/json", "-metadata:s:t:0", "filename=" + ScoringReferenceService.AttachmentName];
            progress?.Report(new("merge", "整合影片"));
            await ToolProcess.RunAsync(Path.Combine(root, "tools", "media", "ffmpeg.exe"),
                ["-hide_banner", "-nostdin", "-v", "error", "-n", "-i", source, "-i", accompaniment,
                "-map", "0:v:0", "-map", "0:a:0", "-map", "1:a:0", "-map_metadata", "-1", "-c:v", "copy", "-c:a:0", "copy", "-c:a:1", "aac", "-b:a:1", "256k",
                "-metadata", "OPENKARAOKE_FORMAT=1", "-metadata", "title=" + title.Trim(), "-metadata", "artist=" + artist.Trim(),
                "-metadata:s:a:0", "title=原唱", "-metadata:s:a:1", "title=伴奏", .. attachment, "-shortest", "-f", "matroska", output], cancellation);
            var info = await packages.InspectAsync(output, cancellation);
            await ToolProcess.RunAsync(Path.Combine(root,"tools","media","ffmpeg.exe"),
                ["-hide_banner","-nostdin","-v","error","-y","-i",output,"-filter_complex","[0:a:1]aformat=channel_layouts=mono,showwavespic=s=480x52:colors=0x64DDC4,format=rgba,colorkey=black:0.01:0.0[out]","-map","[out]","-frames:v","1",Path.ChangeExtension(output,"waveform.png")],cancellation);
            progress?.Report(new("complete", "伴奏已完成", 100));
            return new(output, title.Trim(), artist.Trim(), info.Duration);
        }
        catch { if (File.Exists(output)) File.Delete(output); throw; }
        finally
        {
            if (File.Exists(audio)) File.Delete(audio);
            if (File.Exists(accompaniment)) File.Delete(accompaniment);
            if (File.Exists(vocals)) File.Delete(vocals);
        }
    }
}
