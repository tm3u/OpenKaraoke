using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OpenKaraoke.Library;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public sealed class ScoringReferenceService(string root, string dataDirectory, AiRuntime runtime)
{
    public const string AttachmentName = "openkaraoke-score-v1.json";
    public string Model => Path.Combine(root, "tools", "scoring", "swift-f0-0.3.0.onnx");
    private string Ffmpeg => Path.Combine(root, "tools", "media", "ffmpeg.exe");
    private sealed record CachedReference(string MediaHash, ReferenceDocument? Reference);
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    private static async Task<string> HashAsync(string file, CancellationToken token)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    private static string Cache(string data, string hash) => Path.Combine(data, "scoring", hash + ".json");
    private async Task SaveAsync(Song song, ReferenceTrack reference, CancellationToken token)
    {
        string hash = await HashAsync(song.MediaPath, token);
        string path = Cache(dataDirectory, hash), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new CachedReference(hash, reference.Document)), token);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<ReferenceTrack?> LoadCachedAsync(string data, string media, CancellationToken token)
    {
        string hash = await HashAsync(media, token), cache = Cache(data, hash);
        try
        {
            if (!File.Exists(cache) || new FileInfo(cache).Length >= 32 * 1024 * 1024) return null;
            var item = JsonSerializer.Deserialize<CachedReference>(await File.ReadAllTextAsync(cache, token));
            if (item is { Reference: { } document } && item.MediaHash == hash)
                return await Task.Run(() => new ReferenceTrack(document, token), token);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    public async Task<ReferenceTrack?> LoadAsync(Song song, CancellationToken token)
    {
        if (await LoadCachedAsync(dataDirectory, song.MediaPath, token) is { } cached
            && Math.Abs(cached.Document.Duration - song.Duration) <= 1)
            return cached;
        string folder = Path.Combine(dataDirectory, "scoring", "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string attachment = Path.Combine(folder, AttachmentName);
        try
        {
            try
            {
                await ToolProcess.RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-dump_attachment:t:0", attachment,
                    "-i", song.MediaPath, "-map", "0:v:0", "-t", "0", "-f", "null", "-"], token);
            }
            catch (IOException) when (!File.Exists(attachment)) { return null; }
            if (!File.Exists(attachment)) return null;
            ReferenceTrack reference;
            try { reference = await Task.Run(() => ReferenceTrack.Read(attachment, token), token); }
            catch (Exception error) when (error is JsonException or InvalidDataException) { return null; }
            if (Math.Abs(reference.Document.Duration - song.Duration) > 1) return null;
            try { await SaveAsync(song, reference, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return reference;
        }
        finally { Directory.Delete(folder, true); }
    }

    public async Task<ReferenceTrack> BuildAsync(Song song, IProgress<CreationProgress>? progress, CancellationToken token)
    {
        await _buildGate.WaitAsync(token);
        string folder = Path.Combine(dataDirectory, "scoring", "build-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            await runtime.EnsureAsync(progress, token);
            string audio = Path.Combine(folder, "source.wav"), vocals = Path.Combine(folder, "vocals.wav");
            progress?.Report(new("reference", "分離主唱人聲"));
            await ToolProcess.RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", song.MediaPath,
                "-map", $"0:a:{song.OriginalTrack - 1}", "-vn", "-af", "aresample=44100:async=1:first_pts=0", "-ac", "2", "-c:a", "pcm_f32le", audio], token);
            await ToolProcess.RunAsync(runtime.Python, [runtime.Adapter, "--model", runtime.Model, "--input", audio,
                "--output", Path.Combine(folder, "accompaniment.wav"), "--vocals", vocals], token);
            var reference = await AnalyzeVocalsAsync(vocals, folder, progress, token, audio);
            await SaveAsync(song, reference, token); return reference;
        }
        finally { try { if(Directory.Exists(folder))Directory.Delete(folder, true); } finally { _buildGate.Release(); } }
    }

    public async Task<ReferenceTrack> AnalyzeVocalsAsync(string vocals, string folder, IProgress<CreationProgress>? progress, CancellationToken token, string? mix = null)
    {
        string pcm = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".f32");
        string mixPcm = pcm + ".mix";
        try
        {
            progress?.Report(new("reference", "建立評分基準"));
            await ToolProcess.RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", vocals,
                "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_f32le", "-f", "f32le", pcm], token);
            if (mix != null) await ToolProcess.RunAsync(Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", mix,
                "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_f32le", "-f", "f32le", mixPcm], token);
            return await Task.Run(() =>
            {
                using var detector = new PitchDetector(Model); var stream = new PitchStream(detector);
                using var input = File.OpenRead(pcm); byte[] bytes = new byte[16000 * 4 * 4];
                var frames = new List<PitchFrame>(); long total = 0; int read;
                while ((read = input.Read(bytes)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (read % 4 != 0) throw new InvalidDataException("音訊資料不完整。");
                    var audio = new float[read / 4]; Buffer.BlockCopy(bytes, 0, audio, 0, read); total += audio.Length;
                    frames.AddRange(stream.Push(audio));
                    progress?.Report(new("reference", "建立評分基準", 100d * input.Position / input.Length));
                }
                frames.AddRange(stream.Push([], final: true));
                float[] Energy(string file)
                {
                    using var source = File.OpenRead(file); var levels = new List<float>(); byte[] block = new byte[PitchDetector.Hop * 4];
                    int count;
                    while ((count = source.ReadAtLeast(block, block.Length, throwOnEndOfStream: false)) > 0)
                    {
                        token.ThrowIfCancellationRequested(); double sum = 0;
                        for (int i = 0; i + 4 <= count; i += 4) { double sample = BitConverter.ToSingle(block, i); sum += sample * sample; }
                        levels.Add((float)Math.Sqrt(sum / (count / 4)));
                    }
                    return levels.ToArray();
                }
                var vocalRms = Energy(pcm);
                var filtered = VocalActivity.Filter(frames.ToArray(), vocalRms, mix == null ? null : Energy(mixPcm));
                var loudness = vocalRms.Take(filtered.Length).Select(rms => (float)(20 * Math.Log10(Math.Max(rms, 1e-6)))).ToArray();
                return ReferenceTrack.FromFrames(filtered, total / (double)PitchDetector.SampleRate, loudness, token);
            }, token);
        }
        finally { if (File.Exists(pcm)) File.Delete(pcm); if (File.Exists(mixPcm)) File.Delete(mixPcm); }
    }
}
