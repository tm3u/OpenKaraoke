using System.Diagnostics;
using System.IO;
using OpenKaraoke.Library;
using OpenKaraoke.Player;

namespace OpenKaraoke.Prototype;

public sealed class MediaImportService(string toolsRoot, LibraryStore store)
{
    public async Task<PlaybackState> ProbeAsync(string file)
    {
        await using var probe = new MpvPlayer(_ => { });
        await probe.StartAsync(Path.Combine(toolsRoot,"tools","media","mpv.exe"), 0, headless:true);
        await probe.LoadAsync(file, play:false);
        return probe.State;
    }

    public async Task<Song> ImportAsync(string title, string artist, string original, string? instrumental,
        int originalTrack, int instrumentalTrack, IProgress<string>? progress, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) throw new ArgumentException("請填寫歌名與歌手。");
        progress?.Report("檢查歌曲…");
        var source = await ProbeAsync(original);
        cancellation.ThrowIfCancellationRequested();
        if (!source.HasVideo || !double.IsFinite(source.Duration) || source.Duration <= 0) throw new InvalidOperationException("導唱檔必須包含可播放的影片與音訊。");
        if (!source.Tracks.Any(t => t.Id == originalTrack)) throw new InvalidOperationException("請選擇導唱音軌。");
        bool embedded = instrumental == null;
        if (embedded && (originalTrack == instrumentalTrack || !source.Tracks.Any(t => t.Id == instrumentalTrack)))
            throw new InvalidOperationException("導唱與伴奏必須使用兩條不同音軌。");
        if (!embedded)
        {
            var accompaniment = await ProbeAsync(instrumental!);
            cancellation.ThrowIfCancellationRequested();
            if (accompaniment.Tracks.Length == 0) throw new InvalidOperationException("伴奏檔沒有可播放的音訊。");
            if (Math.Abs(source.Duration - accompaniment.Duration) > 2)
                throw new InvalidOperationException("導唱與伴奏長度相差超過 2 秒，請使用同步的同一首歌曲。");
        }
        string id = Guid.NewGuid().ToString("N");
        string extension = embedded ? Path.GetExtension(original) : ".mkv";
        string final = Path.Combine(store.MediaDirectory, id + extension);
        string temporary = Path.Combine(store.MediaDirectory, id + ".importing" + extension);
        bool committed = false;
        try
        {
            progress?.Report(embedded ? "複製歌曲…" : "整合導唱與伴奏…");
            if (embedded)
            {
                await using var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, 1024*1024, true);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024*1024, true);
                await input.CopyToAsync(output, cancellation);
            }
            else
            {
                var start = new ProcessStartInfo(Path.Combine(toolsRoot,"tools","media","ffmpeg.exe"))
                    { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true };
                int ordinal = Array.FindIndex(source.Tracks, t => t.Id == originalTrack);
                foreach (string arg in new[] { "-hide_banner","-nostdin","-v","error","-n","-i",original,"-i",instrumental!,
                    "-map","0:v:0","-map",$"0:a:{ordinal}","-map","1:a:0","-c:v","copy","-c:a","aac","-b:a","256k",
                    "-metadata:s:a:0","title=原唱","-metadata:s:a:1","title=伴奏", temporary }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("無法啟動音軌整合工具。");
                Task<string> errors = process.StandardError.ReadToEndAsync();
                using var registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                await process.WaitForExitAsync();
                string details = await errors;
                cancellation.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new IOException("歌曲整合失敗：" + details[..Math.Min(details.Length, 500)]);
                originalTrack = 1; instrumentalTrack = 2;
            }
            cancellation.ThrowIfCancellationRequested();
            progress?.Report("驗證音軌…");
            var result = await ProbeAsync(temporary);
            if (!result.HasVideo || result.Tracks.Length < 2 || !result.Tracks.Any(t=>t.Id==originalTrack) || !result.Tracks.Any(t=>t.Id==instrumentalTrack))
                throw new InvalidOperationException("歌曲必須同時包含導唱與伴奏。");
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, final);
            var song = new Song(id, title.Trim(), artist.Trim(), final, result.Duration, originalTrack, instrumentalTrack);
            store.Add(song);
            committed = true;
            return song;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (!committed && File.Exists(final)) File.Delete(final);
        }
    }
}
