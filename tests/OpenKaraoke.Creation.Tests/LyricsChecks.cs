using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using OpenKaraoke.Library;
using OpenKaraoke.Player;
using OpenKaraoke.Prototype;

internal static class LyricsChecks
{
    internal const string TimedText = """
        [00:05.00]<00:05.00>今<00:05.50>天<00:06.00>一<00:06.50>起<00:07.00>唱<00:07.50>歌<00:08.00>
        [00:09.00]<00:09.00>跟<00:09.50>著<00:10.00>節<00:10.50>奏<00:11.00>向<00:11.50>前<00:12.00>
        [00:13.00]<00:13.00>讓<00:13.50>聲<00:14.00>音<00:14.50>傳<00:15.00>得<00:15.50>更<00:16.00>遠<00:16.50>
        [00:18.00]<00:18.00>把<00:18.50>這<00:19.00>一<00:19.50>刻<00:20.00>留<00:20.50>下<00:21.00>
        [00:23.00]<00:23.00>下<00:23.50>一<00:24.00>首<00:24.50>繼<00:25.00>續<00:25.50>唱<00:26.00>
        """;

    internal static async Task RunAsync(string root, string folder, Action<string, bool> check)
    {
        Directory.CreateDirectory(folder);
        bool Reject(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
        }
        var line = new SongLyrics("逐行.lrc", "[00:01.00]今天一起唱歌\n[00:04.00]跟著節奏向前\n[00:07.00]", "scroll");
        line.Validate(10);
        check("逐行 LRC 可供滾動模式使用，不能冒充逐字時間", Reject(() => (line with { Mode = "karaoke" }).Validate(10)));
        var timed = new SongLyrics("逐字.lrc", TimedText, "karaoke");
        timed.Validate(120);
        check("逐字 LRC 保留每個字的原始時間", timed.ToAss(120).Contains(@"{\kt50\kf50}天"));
        check("兩種模式各自使用雙行填色與多行捲動", timed.ToAss(120).Contains(@"\pos(960,860)")
            && timed.ToAss(120).Contains(@"\pos(960,950)") && (timed with { Mode = "scroll" }).ToAss(120).Contains(@"\move(")
            && !(timed with { Mode = "scroll" }).ToAss(120).Contains(@"\kf"));
        var repeated = new SongLyrics("重複.lrc", "[00:01.00][00:04.00]同一句再唱一次", "karaoke");
        check("同句多個播放時間不會誤判為逐字時間", Reject(() => repeated.Validate(10)));
        var partial = timed with { Content = TimedText + "\n[00:30.00]只有逐行時間" };
        check("部分逐字資料會阻擋 KTV 模式，仍可選滾動模式", Reject(() => partial.Validate(120)) && (partial with { Mode = "scroll" }).ToAss(120).Contains("只有逐行時間"));
        check("無時間、時間倒序及超出歌曲範圍均拒絕", Reject(() => new SongLyrics("無時間.lrc", "只有文字", "scroll").Validate())
            && Reject(() => new SongLyrics("倒序.lrc", "[00:02.00]<00:02.00>一<00:01.00>起", "karaoke").Validate())
            && Reject(() => timed.Validate(6)));
        check("最後一句沒有結束標記時仍檢查逐字時間範圍", Reject(() => new SongLyrics("越界.lrc", "[00:01.00]<00:01.00>一<02:30.00>起", "karaoke").Validate(120)));
        check("沒有選擇模式時不能提交歌詞", Reject(() => (timed with { Mode = "" }).Validate()));
        var ass = new SongLyrics("逐字.ass", """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,{\k50}一{\kf50}起{\kf100}唱
            """, "karaoke");
        check("ASS 卡拉 OK 音節時間以原始時長解析", ass.ToAss(10).Contains(@"{\kt100\kf100}唱"));
        var offset = new SongLyrics("偏移.lrc", "[offset:500]\n[00:01.00]一起唱\n[00:03.00]", "scroll");
        check("匯入尊重歌詞檔內既有的整體時間偏移", offset.ToAss(10).Contains("0:00:01.50,0:00:03.50"));
        check("滾動 LRC 與逐字 ASS 同格式時保留原始內容", line.ToSidecar(10) == line.Content && ass.ToSidecar(10) == ass.Content
            && line.SidecarExtension == ".lrc" && ass.SidecarExtension == ".ass");
        string convertedAss = timed.ToSidecar(120);
        var restoredAss = new SongLyrics("轉換.ass", convertedAss, "karaoke");
        restoredAss.Validate(120);
        check("逐字 LRC 轉成真正可重新匯入的 ASS，保存逐字時間且不混入預覽行", restoredAss.ToAss(120) == timed.ToAss(120)
            && convertedAss.Contains(@"{\kt50\kf50}天") && !convertedAss.Contains(@"{\1c&HFFFFFF&}"));
        string convertedLrc = (ass with { Mode = "scroll" }).ToSidecar(10);
        var restoredLrc = new SongLyrics("轉換.lrc", convertedLrc, "scroll");
        restoredLrc.Validate(10);
        check("滾動 ASS 轉成真正 LRC，保留原有句首、逐字與結束時間", restoredLrc.ToAss(10) == (ass with { Mode = "scroll" }).ToAss(10)
            && convertedLrc.Contains("[00:01.000]<00:01.000>一<00:01.500>起<00:02.000>唱<00:03.000>") && convertedLrc.Contains("[00:03.000]"));
        var gaps = ass with { Content = ass.Content.Replace(@"{\k50}一{\kf50}起{\kf100}唱", @"{\k50}一{\kt100\kf50}起{\kt175\kf10}唱"), Mode = "scroll" };
        var gapLrc = new SongLyrics("間隔.lrc", gaps.ToSidecar(10), "karaoke");
        check("ASS 轉 LRC 保留字間空檔與最後一字唱完後的句尾停留", gapLrc.ToAss(10) == (gaps with { Mode = "karaoke" }).ToAss(10));
        var escaped = new SongLyrics("符號.lrc", "[00:01.00]<00:01.00>一{<00:02.00>起}\\N<00:03.00>", "karaoke");
        check("逐字格式往返保留大括號與文字中的反斜線", new SongLyrics("符號.ass", escaped.ToSidecar(10), "karaoke").ToAss(10) == escaped.ToAss(10));

        string lyricFile = Path.Combine(folder, "原始歌詞.lrc");
        await File.WriteAllTextAsync(lyricFile, TimedText);
        var snapshot = SongLyrics.Read(lyricFile) with { Mode = "karaoke" };
        File.Delete(lyricFile);
        check("選檔後保存歌詞內容，不依賴原檔持續存在", snapshot.Content == TimedText && snapshot.ToAss(120).Contains("今"));

        string queueFolder = Path.Combine(folder, "queue");
        await using (var queue = new SongProcessingQueue(queueFolder, async (_, _, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }))
        {
            queue.AddCreate(new("https://www.youtube.com/watch?v=jNQXAC9IVRw", "測試", 120), "歌詞工作", "測試歌手", snapshot);
        }
        var resumed = new TaskCompletionSource<SongLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var queue = new SongProcessingQueue(queueFolder, (item, _, _, _) =>
        {
            resumed.TrySetResult(item.Lyrics);
            return Task.FromResult(new Song(item.Id, item.Title, item.Artist, "fixture", 120, 1, 2));
        }))
        {
            queue.Start();
            var restored = await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check("重新啟動處理清單仍保留當次歌詞與模式", restored == snapshot);
        }

        var store = new LibraryStore(Path.Combine(folder, "library"));
        var packages = new SongPackageService(root, store);
        string source = Path.Combine(root, "fixtures", "dual-track-test.mp4");
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        foreach (string mode in new[] { "karaoke", "scroll" })
        {
            string package = Path.Combine(folder, mode + ".opok");
            await packages.ExportAsync(source, 1, 2, mode, "測試歌手", package, lyrics: timed with { Mode = mode });
            var info = await packages.InspectAsync(package);
            check(mode + " 歌詞實際封裝為可選取的內嵌字幕", info.LyricsMode == mode && Math.Abs(info.Duration - 120) < .1);
            if (mode == "karaoke")
            {
                string legacy = Path.ChangeExtension(package, ".openkaraoke");
                File.Move(package, legacy); package = legacy;
            }
            var song = await packages.ImportAsync(package, mode + "重新匯入", "測試歌手");
            string sidecar = Path.ChangeExtension(song.MediaPath, mode == "scroll" ? ".lrc" : ".ass");
            var savedLyrics = SongLyrics.Read(sidecar) with { Mode = mode };
            savedLyrics.Validate(120);
            check(mode + " 單獨匯入新舊歌曲檔也會建立同資料夾、同名的有效歌詞副本", File.Exists(sidecar)
                && Path.GetFileName(sidecar) == mode + "重新匯入_測試歌手" + (mode == "scroll" ? ".lrc" : ".ass")
                && Directory.GetFiles(Path.GetDirectoryName(song.MediaPath)!).Length == 2);
            check(mode + " 從內嵌歌詞還原不重複預覽或相鄰句", savedLyrics.ToAss(120) == (timed with { Mode = mode }).ToAss(120));
            var secondCopy = await packages.ImportAsync(song.MediaPath, mode + "再次匯入", "測試歌手", lyrics: savedLyrics);
            check(mode + " 歌詞副本可重新匯入，歌曲與歌詞使用新的同名副本", (await packages.InspectAsync(secondCopy.MediaPath)).LyricsMode == mode
                && File.ReadAllText(Path.ChangeExtension(secondCopy.MediaPath, savedLyrics.SidecarExtension)) == savedLyrics.Content);
            File.Delete(package);
            await using var player = new MpvPlayer(_ => { });
            await player.SetSubtitlesAsync(false, 125);
            await player.StartAsync(Path.Combine(root, "tools", "media", "mpv.exe"), 0, headless: true);
            await player.LoadAsync(song.MediaPath, play: false);
            await using var connection = new MpvConnection(player.PipeName);
            await connection.ConnectAsync();
            var selected = await connection.GetAsync("sid");
            check(mode + " 啟動前設定的字幕偏好會帶入播放器", !(await connection.GetAsync("sub-visibility")).GetBoolean()
                && Math.Abs((await connection.GetAsync("sub-scale")).GetDouble() - 1.25) < .001);
            check(mode + " 重新匯入後模式保留，播放器實際選取歌詞軌", player.State.LyricsMode == mode && selected.ValueKind == System.Text.Json.JsonValueKind.Number);
            string extracted = Path.Combine(folder, mode + "-embedded.ass");
            var start = new ProcessStartInfo(Path.Combine(root, "tools", "media", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-hide_banner", "-nostdin", "-v", "error", "-n", "-i", song.MediaPath, "-map", "0:s:0", "-c:s", "copy", extracted }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (process.ExitCode != 0) throw new IOException(await errors);
            string embedded = await File.ReadAllTextAsync(extracted);
            check(mode + " 內嵌歌詞保留文字與所選模式的呈現內容", embedded.Contains("今天一起唱歌")
                && embedded.Contains(mode == "scroll" ? @"\move(" : @"\kf50"));
            await player.UnloadAsync();
            check(mode + " 卸載歌曲後保留字幕偏好", !player.State.SubtitlesEnabled && player.State.SubtitleFontPercent == 125);
        }
        byte[] after = SHA256.HashData(await File.ReadAllBytesAsync(source));
        check("加入歌詞不修改來源影片", sourceHash.SequenceEqual(after));
        foreach (var lyrics in new[] { line, timed, ass, ass with { Mode = "scroll" } })
        {
            string title = "獨立歌詞" + lyrics.Mode + Path.GetExtension(lyrics.Filename);
            string package = Path.Combine(folder, title + ".opok");
            string original = Path.Combine(folder, "來源-" + title + Path.GetExtension(lyrics.Filename));
            await File.WriteAllTextAsync(original, lyrics.Content);
            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(original));
            await packages.ExportAsync(source, 1, 2, title, "測試歌手", package);
            var song = await packages.ImportAsync(package, title, "測試歌手", lyrics: SongLyrics.Read(original) with { Mode = lyrics.Mode });
            string sidecar = Path.ChangeExtension(song.MediaPath, lyrics.SidecarExtension);
            check(title + " 依選定模式保存實際格式，不由原始副檔名決定", File.ReadAllText(sidecar) == lyrics.ToSidecar(120)
                && Directory.GetFiles(Path.GetDirectoryName(song.MediaPath)!).Length == 2);
            var afterHash = SHA256.HashData(await File.ReadAllBytesAsync(original));
            check(title + " 原始歌詞維持原位置、檔名與內容", File.Exists(original)
                && originalHash.SequenceEqual(afterHash));
            int foldersBefore = Directory.GetDirectories(store.MediaDirectory).Length;
            bool rejected = false;
            try { await packages.ImportAsync(package, title, "測試歌手", lyrics: lyrics); }
            catch (InvalidOperationException) { rejected = true; }
            check(title + " 入庫失敗時一併清理歌曲、歌詞與暫存檔", rejected && Directory.GetDirectories(store.MediaDirectory).Length == foldersBefore);
        }
    }
}
