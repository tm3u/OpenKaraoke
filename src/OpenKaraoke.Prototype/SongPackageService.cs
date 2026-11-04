using System.IO;
using OpenKaraoke.Library;
using OpenKaraoke.Player;

namespace OpenKaraoke.Prototype;

public sealed record PackageInfo(string File, double Duration, string Title, string Artist, string LyricsMode = "");

public sealed class SongPackageService(string toolsRoot, LibraryStore store)
{
    public const string Extension = ".opok";
    public static bool IsPackageFile(string path) => Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".openkaraoke", StringComparison.OrdinalIgnoreCase);

    public static (string Title, string Artist) ParseFilename(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        var parts = stem.Split('_');
        return parts.Length == 2 && parts.All(p => !string.IsNullOrWhiteSpace(p))
            ? (parts[0].Trim(), parts[1].Trim()) : (stem.Trim(), "");
    }

    public static string Filename(string title, string artist)
    {
        ValidateNames(title, artist);
        string Safe(string text)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(text.Trim().Select(c => invalid.Contains(c) ? '＿' : c));
        }
        return Safe(title) + "_" + Safe(artist) + Extension;
    }

    public static void ValidateNames(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            throw new ArgumentException("請填寫歌名與歌手。");
        if (title.Trim().Length > 120 || artist.Trim().Length > 120)
            throw new ArgumentException("歌名與歌手各限 120 個字。");
    }

    public async Task<PackageInfo> InspectAsync(string path, CancellationToken cancellation = default)
    {
        if (!IsPackageFile(path))
            throw new InvalidOperationException("只能匯入 .opok 或 .openkaraoke 歌曲檔。");
        await using (var file = File.OpenRead(path))
        {
            byte[] header = new byte[4];
            if (await file.ReadAsync(header, cancellation) != 4 || !header.SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }))
                throw new InvalidOperationException("不是有效的 .opok 歌曲檔。");
        }
        cancellation.ThrowIfCancellationRequested();
        await using var probe = new MpvPlayer(_ => { });
        await probe.StartAsync(Path.Combine(toolsRoot, "tools", "media", "mpv.exe"), 0, headless: true);
        await probe.LoadAsync(path, play: false);
        var info = probe.State;
        var tags = await probe.ReadMetadataAsync();
        cancellation.ThrowIfCancellationRequested();
        if (!tags.TryGetValue("OPENKARAOKE_FORMAT", out string? version) || version != "1")
            throw new InvalidOperationException("歌曲檔格式版本不受支援，請使用 OpenKaraoke 匯出的歌曲檔。");
        string title = tags.GetValueOrDefault("TITLE", ""), artist = tags.GetValueOrDefault("ARTIST", "");
        ValidateNames(title, artist);
        if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0 || info.Tracks.Length != 2
            || info.Tracks[0].Title != "原唱" || info.Tracks[1].Title != "伴奏")
            throw new InvalidOperationException("歌曲檔必須包含影片，以及導唱與伴奏兩條音軌。");
        return new(path, info.Duration, title, artist, info.LyricsMode);
    }

    public async Task ExportAsync(string media, int originalTrack, int instrumentalTrack,
        string title, string artist, string destination, CancellationToken cancellation = default, SongLyrics? lyrics = null)
    {
        ValidateNames(title, artist);
        if (!Path.GetExtension(destination).Equals(Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("請使用 .opok 副檔名。");
        if (Path.GetFullPath(media).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("輸出位置不可與來源相同。");
        var probe = new MediaImportService(toolsRoot, store);
        var info = await probe.ProbeAsync(media);
        int original = Array.FindIndex(info.Tracks, t => t.Id == originalTrack);
        int instrumental = Array.FindIndex(info.Tracks, t => t.Id == instrumentalTrack);
        if (!info.HasVideo || original < 0 || instrumental < 0 || original == instrumental)
            throw new InvalidOperationException("歌曲必須具有導唱與伴奏兩條不同音軌。");
        lyrics?.Validate(info.Duration);
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + Extension);
        string referenceFile = temporary + ".json";
        string subtitleFile = temporary + ".ass";
        try
        {
            string[] lyricInput = [], subtitles = ["-map", "0:s?"];
            if (lyrics != null)
            {
                await File.WriteAllTextAsync(subtitleFile, lyrics.ToAss(info.Duration), cancellation);
                lyricInput = ["-i", subtitleFile];
                subtitles = ["-map", "1:s:0", "-metadata:s:s:0", "title=" + MpvPlayer.LyricsTrackPrefix + lyrics.Mode,
                    "-metadata:s:s:0", "language=und", "-disposition:s:0", "default"];
            }
            var reference = await ScoringReferenceService.LoadCachedAsync(store.DataDirectory, media, cancellation);
            string[] attachments;
            if(reference!=null)
            {
                reference.Write(referenceFile);
                attachments=["-attach",referenceFile,"-metadata:s:t:0","mimetype=application/json","-metadata:s:t:0","filename="+ScoringReferenceService.AttachmentName];
            }
            else attachments=["-map","0:t?"];
            await ToolProcess.RunAsync(Path.Combine(toolsRoot, "tools", "media", "ffmpeg.exe"),
                ["-hide_banner", "-nostdin", "-v", "error", "-n", "-i", media, ..lyricInput,
                "-map", "0:v:0", "-map", $"0:a:{original}", "-map", $"0:a:{instrumental}", ..subtitles, "-map_metadata:g", "-1",
                "-c", "copy", "-metadata", "OPENKARAOKE_FORMAT=1", "-metadata", "title=" + title.Trim(), "-metadata", "artist=" + artist.Trim(),
                "-metadata:s:a:0", "title=原唱", "-metadata:s:a:1", "title=伴奏", ..attachments,"-f", "matroska", temporary], cancellation);
            var result = await InspectAsync(temporary, cancellation);
            if (lyrics != null && result.LyricsMode != lyrics.Mode || lyrics == null && result.LyricsMode != info.LyricsMode)
                throw new InvalidDataException("歌曲的歌詞未正確保存。");
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary);if(File.Exists(referenceFile))File.Delete(referenceFile);if(File.Exists(subtitleFile))File.Delete(subtitleFile); }
    }

    public async Task<Song> ImportAsync(string source, string title, string artist, CancellationToken cancellation = default, string? operationId = null, SongLyrics? lyrics = null)
    {
        ValidateNames(title, artist);
        if(operationId!=null&&!Guid.TryParseExact(operationId,"N",out _))throw new ArgumentException("匯入工作編號無效。");
        if(operationId!=null&&store.FindById(operationId) is {} existing)return existing;
        await InspectAsync(source, cancellation);
        string id = operationId ?? Guid.NewGuid().ToString("N");
        string folder = Path.Combine(store.MediaDirectory, id);
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, Filename(title, artist));
        bool committed = false;
        try
        {
            await ExportAsync(source, 1, 2, title, artist, destination, cancellation, lyrics);
            var info = await InspectAsync(destination, cancellation);
            if (info.LyricsMode.Length > 0)
            {
                var savedLyrics = lyrics ?? await ReadEmbeddedLyricsAsync(destination, info.LyricsMode, cancellation);
                string sidecar = Path.ChangeExtension(destination, savedLyrics.SidecarExtension);
                await File.WriteAllTextAsync(sidecar, savedLyrics.ToSidecar(info.Duration), cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            var song = new Song(id, title.Trim(), artist.Trim(), destination, info.Duration, 1, 2);
            store.Add(song);
            committed = true;
            return song;
        }
        finally
        {
            if (!committed && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private async Task<SongLyrics> ReadEmbeddedLyricsAsync(string file, string mode, CancellationToken cancellation)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(file)!, "." + Guid.NewGuid().ToString("N") + ".ass");
        try
        {
            await ToolProcess.RunAsync(Path.Combine(toolsRoot, "tools", "media", "ffmpeg.exe"),
                ["-hide_banner", "-nostdin", "-v", "error", "-n", "-i", file,
                "-map", "0:s:m:title:" + MpvPlayer.LyricsTrackPrefix.Replace(":", @"\:") + mode, "-c:s", "ass", "-f", "ass", temporary], cancellation);
            return SongLyrics.FromEmbeddedAss(await File.ReadAllTextAsync(temporary, cancellation), mode);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
