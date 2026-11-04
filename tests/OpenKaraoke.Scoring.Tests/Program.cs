using System.Text.Json;
using NAudio.Wave;
using OpenKaraoke.Library;
using OpenKaraoke.Player;
using OpenKaraoke.Prototype;
using OpenKaraoke.Scoring;

string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string folder = Path.Combine(root, "artifacts", "scoring-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(folder);
var checks = new List<object>();
void Check(string name, bool passed, object? evidence = null)
{
    checks.Add(new { Name = name, Passed = passed, Evidence = evidence });
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name} {JsonSerializer.Serialize(evidence)}");
    if (!passed) throw new InvalidOperationException(name);
}
float[] Tone(double seconds, double hertz) => Enumerable.Range(0, (int)(seconds * 16000)).Select(i => (float)(.2 * Math.Sin(2 * Math.PI * hertz * i / 16000) + .08 * Math.Sin(4 * Math.PI * hertz * i / 16000))).ToArray();
try
{
    if(args.Contains("--creation-smoke"))
    {
        var storeSmoke = new LibraryStore(Path.Combine(folder,"data"));
        var packagesSmoke = new SongPackageService(root,storeSmoke);
        var runtimeSmoke = new AiRuntime(root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenKaraoke","ai"));
        var referenceSmoke = new ScoringReferenceService(root,storeSmoke.DataDirectory,runtimeSmoke);
        string clip=Path.Combine(folder,"clip.mkv");
        async Task Ffmpeg(params string[] arguments)
        {
            var info=new System.Diagnostics.ProcessStartInfo(Path.Combine(root,"tools","media","ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true};
            foreach(var arg in new[]{"-hide_banner","-nostdin","-v","error"}.Concat(arguments))info.ArgumentList.Add(arg);
            using var process=System.Diagnostics.Process.Start(info)!;await process.WaitForExitAsync();
            if(process.ExitCode!=0)throw new IOException("片段封裝失敗。");
        }
        int sourceIndex=Array.IndexOf(args,"--creation-smoke");string source=args[sourceIndex+1];
        var serviceSmoke=new SongCreationService(root,runtimeSmoke,packagesSmoke);
        CreationResult created;
        if(SongPackageService.IsPackageFile(source))
        {
            var existing=await packagesSmoke.InspectAsync(source);created=new(source,existing.Title,existing.Artist,existing.Duration);
        }
        else
        {
            await Ffmpeg("-ss","45","-i",source,"-t","12","-map","0:v:0","-map","0:a:0","-c:v","libx264","-preset","ultrafast","-crf","28","-c:a","aac",clip);
            created=await serviceSmoke.SeparateAndPackageAsync(clip,"片段驗收","測試歌手",Path.Combine(folder,"creation"),new Progress<CreationProgress>(p=>Console.WriteLine(p.Stage+" "+p.Detail)),CancellationToken.None);
        }
        var processor=new SongProcessingService(storeSmoke,serviceSmoke,packagesSmoke,referenceSmoke);
        var stages=new List<CreationProgress>();
        var progress=new ImmediateProgress<CreationProgress>(p=>stages.Add(p));
        var importedSmoke=await processor.ProcessAsync(new(Guid.NewGuid().ToString("N"),"import","已含基準","測試歌手",created.Package),Path.Combine(folder,"import"),progress,CancellationToken.None);
        var createdReference=await referenceSmoke.LoadAsync(importedSmoke,CancellationToken.None);
        Check("實際 AI 製作同時產生可攜式旋律資料",createdReference!=null,new{created.Duration,notes=createdReference?.Notes.Length});
        Check("匯入已有基準的歌曲不重複執行 AI 分離",!stages.Any(p=>p.Detail=="分離主唱人聲")&&stages.Any(p=>p.Detail=="加入歌曲庫"));
        var sameVoice=new ScoreSession(createdReference!);
        for(int i=0;i<createdReference!.Document.PitchHz.Length;i++)sameVoice.Add(new(i*.016,createdReference.Document.PitchHz[i],createdReference.Document.Confidence[i]),0);
        var selfScore=sameVoice.Result(createdReference.Document.Duration,true);
        Check("真人主唱參考與相同人聲的音準一致",selfScore.Pitch>=98,selfScore);
        string withoutReference=Path.Combine(folder,"缺少基準_測試歌手.opok");
        await Ffmpeg("-i",created.Package,"-map","0:v:0","-map","0:a:0","-map","0:a:1","-c","copy","-f","matroska",withoutReference);
        var sourceInfo=await packagesSmoke.InspectAsync(withoutReference);
        Check("缺少評分附件的歌曲不會產生虛構旋律",await referenceSmoke.LoadAsync(new("withoutReference","缺少基準","測試歌手",withoutReference,sourceInfo.Duration,1,2),CancellationToken.None)==null);
        byte[] originalBytes=await File.ReadAllBytesAsync(withoutReference);stages.Clear();
        bool insertedEarly=false;
        var importProgress=new ImmediateProgress<CreationProgress>(p=>{stages.Add(p);if(p.Stage=="reference")insertedEarly|=storeSmoke.ContainsSong("缺少基準","測試歌手");Console.WriteLine(p.Detail);});
        var processed=await processor.ProcessAsync(new(Guid.NewGuid().ToString("N"),"import","缺少基準","測試歌手",withoutReference),Path.Combine(folder,"withoutReference-import"),importProgress,CancellationToken.None);
        var rebuilt=await referenceSmoke.LoadAsync(processed,CancellationToken.None);
        Check("缺少基準的匯入自動執行實際 AI 並在完成後入庫",rebuilt?.Notes.Length>0&&!insertedEarly&&stages.Any(p=>p.Detail=="建立評分基準")&&storeSmoke.ContainsSong("缺少基準","測試歌手"));
        byte[] afterImport=await File.ReadAllBytesAsync(withoutReference);
        Check("匯入自動建立基準保留原始檔及指定的新檔名",originalBytes.SequenceEqual(afterImport)&&Path.GetFileName(processed.MediaPath)=="缺少基準_測試歌手.opok");
        var portableStore=new LibraryStore(Path.Combine(folder,"portable-data"));var portableReferences=new ScoringReferenceService(root,portableStore.DataDirectory,runtimeSmoke);
        Check("匯入完成的歌曲檔內嵌基準，可在沒有快取的資料夾載入",await portableReferences.LoadAsync(processed,CancellationToken.None)!=null);
        var cancelledId=Guid.NewGuid().ToString("N");bool cancelled=false;
        try{await processor.ProcessAsync(new(cancelledId,"import","取消匯入","測試歌手",withoutReference),Path.Combine(folder,"cancel-import"),progress,new CancellationToken(true));}catch(OperationCanceledException){cancelled=true;}
        Check("取消自動建立流程不留下入庫資料或歌曲副本",cancelled&&storeSmoke.FindById(cancelledId)==null&&!Directory.Exists(Path.Combine(storeSmoke.MediaDirectory,cancelledId)));
        string exported=Path.Combine(folder,"補建匯出_測試歌手.opok");
        await packagesSmoke.ExportAsync(processed.MediaPath,1,2,processed.Title,processed.Artist,exported);
        var anotherStore=new LibraryStore(Path.Combine(folder,"another-data"));var anotherPackages=new SongPackageService(root,anotherStore);
        var transferred=await anotherPackages.ImportAsync(exported,processed.Title,processed.Artist);
        var anotherReferences=new ScoringReferenceService(root,anotherStore.DataDirectory,runtimeSmoke);
        Check("補建後匯出與重新匯入也保留旋律",await anotherReferences.LoadAsync(transferred,CancellationToken.None)!=null);
        await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(folder);return;
    }
    using var detector = new PitchDetector(Path.Combine(root, "tools", "scoring", "swift-f0-0.3.0.onnx"));
    var tone = Tone(2, 440); var detected = detector.Detect(tone);
    Check("實際 ONNX 模型辨識 440 Hz", detected[20..^20].All(f => f.Voiced && Math.Abs(f.Midi - 69) < .25), detected[30]);
    Check("靜音不產生有聲音高", detector.Detect(new float[16000]).All(f => !f.Voiced));
    var stream = new PitchStream(detector); var streamed = new List<PitchFrame>();
    foreach (var block in tone.Chunk(PitchDetector.Hop)) streamed.AddRange(stream.Push(block));
    streamed.AddRange(stream.Push([], true));
    Check("串流與整段推論時間一致且不遺失尾端", streamed.Count == detected.Length && streamed.Select((v, i) => Math.Abs(v.Time - detected[i].Time)).Max() < 1e-9);
    Check("串流音高與整段推論一致", streamed.Zip(detected).Max(p => Math.Abs(p.First.Hertz - p.Second.Hertz)) < .1);
    var referenceFrames = Enumerable.Range(0, 400).Select(i => new PitchFrame(i * .016, i is >= 20 and < 120 or >= 150 and < 250 or >= 280 and < 380 ? 440 : 0, i is >= 20 and < 120 or >= 150 and < 250 or >= 280 and < 380 ? 1 : 0)).ToArray();
    var reference = ReferenceTrack.FromFrames(referenceFrames, 6.4);
    ScoreResult Score(double shift, double delay, double compensation, bool silence = false, bool missingLast = false)
    {
        var session = new ScoreSession(reference);
        foreach (var frame in referenceFrames)
        {
            if (missingLast && frame.Time >= 4) continue;
            session.Add(frame with { Time = frame.Time + delay - compensation, Hertz = frame.Hertz * Math.Pow(2, shift / 12), Confidence = silence ? 0 : frame.Confidence }, 0);
        }
        return session.Result(6.4, true);
    }
    var perfect = Score(0, 0, 0);
    Check("準確旋律獲得滿分", perfect.Pitch == 100, perfect);
    var wrong = Score(4, 0, 0); Check("全程唱錯音高為零分，沒有其他加權得分", wrong.Pitch == 0, wrong);
    var silent = Score(0, 0, 0, silence: true); Check("全程未唱不會得到滿分", silent.Pitch == 0, silent);
    var changing = Enumerable.Range(0,400).Select(i=>new PitchFrame(i*.016,440*Math.Pow(2,(i/40%2)*4d/12),1)).ToArray();
    var changingReference = ReferenceTrack.FromFrames(changing,6.4);
    var late = new ScoreSession(changingReference); var compensated = new ScoreSession(changingReference);
    foreach(var frame in changing){late.Add(frame with{Time=frame.Time+.128},0);compensated.Add(frame,0);}
    Check("延遲補償維持不同音高的音條與歌曲時間對齊",late.Result(6.4,true).Pitch<100&&compensated.Result(6.4,true).Pitch==100);
    var legato = new ScoreSession(reference);
    foreach(var frame in referenceFrames)legato.Add(frame with { Hertz = 440, Confidence = 1 }, 0);
    Check("持續唱準時不因起音數量扣分", legato.Result(6.4, true).Pitch == 100);
    Check("漏唱段落會扣分", Score(0, 0, 0, missingLast: true).Pitch < 80);
    var empty = new ScoreSession(reference); Check("沒有收音資料顯示空值", empty.Result(6.4, true).Pitch == null);
    var transposed = new ScoreSession(reference);
    foreach (var frame in referenceFrames) transposed.Add(frame with { Hertz = frame.Hertz * Math.Pow(2, 3 / 12d) }, 3);
    Check("升調同步變更評分基準", transposed.Result(6.4, true).Pitch == 100);
    var repeated = new ScoreSession(reference);
    foreach (var frame in referenceFrames.Concat(referenceFrames)) repeated.Add(frame, 0);
    Check("重複時間格不重複加分", repeated.Result(6.4, true).Pitch == 100);
    bool rejected = false; try { _ = new ReferenceTrack(reference.Document with { Confidence = [float.NaN] }); } catch (InvalidDataException) { rejected = true; }
    Check("拒絕損壞的評分資料", rejected);
    string settingsPath = Path.Combine(folder, "voice.json"); new VoiceSettings("test-device", 180, true).Save(settingsPath);
    Check("語音設定可還原裝置、延遲及啟用狀態", VoiceSettings.Load(settingsPath) == new VoiceSettings("test-device", 180, true));
    var difficultySettings=new VoiceSettings(Difficulty:ScoringDifficulty.Professional,AutoPlayNext:true);difficultySettings.Save(settingsPath);
    Check("難度與自動播放偏好可保存及重新載入",VoiceSettings.Load(settingsPath)==difficultySettings);
    var subtitleSettings=new VoiceSettings(SubtitlesEnabled:false,SubtitleFontPercent:125);subtitleSettings.Save(settingsPath);
    Check("字幕開關與字體大小可保存及重新載入",VoiceSettings.Load(settingsPath)==subtitleSettings);
    File.WriteAllText(settingsPath,"{\"SubtitleFontPercent\":500}");
    Check("字幕字體大小超出範圍時限制於可用大小",VoiceSettings.Load(settingsPath).SubtitleFontPercent==125);
    File.WriteAllText(settingsPath,"{\"Difficulty\":999}");
    Check("不合法難度回到休閒，自動播放預設關閉",VoiceSettings.Load(settingsPath).Difficulty==ScoringDifficulty.Casual&&!VoiceSettings.Load(settingsPath).AutoPlayNext);
    Check("舊設定缺少字幕欄位時預設開啟並維持原字體大小",VoiceSettings.Load(settingsPath).SubtitlesEnabled&&VoiceSettings.Load(settingsPath).SubtitleFontPercent==100);
    File.WriteAllText(settingsPath, "{broken}"); Check("損壞設定安全回復預設", VoiceSettings.Load(settingsPath) == new VoiceSettings());
    string vocals = Path.Combine(folder, "vocals.wav");
    using (var writer = new WaveFileWriter(vocals, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1))) writer.WriteSamples(Tone(3, 440), 0, 48000);
    var store = new LibraryStore(Path.Combine(folder, "data")); var packages = new SongPackageService(root, store);
    var runtime = new AiRuntime(root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenKaraoke", "ai"));
    var references = new ScoringReferenceService(root, store.DataDirectory, runtime);
    var extracted = await references.AnalyzeVocalsAsync(vocals, folder, null, CancellationToken.None);
    Check("實際 FFmpeg 與模型可建立人聲參考旋律", extracted.Notes.Length > 0 && extracted.VoicedFrames > 100);
    string media = Path.Combine(root, "fixtures", "dual-track-test.mp4");
    string package = Path.Combine(folder, "測試歌曲_測試歌手.opok");
    await packages.ExportAsync(media, 1, 2, "測試歌曲", "測試歌手", package);
    var unscoredSong = new Song("withoutReference", "測試歌曲", "測試歌手", package, 120, 1, 2);
    Check("雙音軌歌曲缺少評分資料時回傳空值", await references.LoadAsync(unscoredSong, CancellationToken.None) == null);
    string attach = Path.Combine(folder, ScoringReferenceService.AttachmentName);
    await using (var player = new MpvPlayer(_ => { }))
    {
        await player.StartAsync(Path.Combine(root, "tools", "media", "mpv.exe"), 0, true); await player.LoadAsync(package, false);
        int count = (int)(player.State.Duration / .016);
        await File.WriteAllTextAsync(attach, JsonSerializer.Serialize(new
        {
            Version = 1, Algorithm = "swift-f0-0.3.0", player.State.Duration,
            PitchHz = Enumerable.Range(0, count).Select(i => i < count / 2 ? 440f : 587.3295f),
            Confidence = Enumerable.Range(0, count).Select(i => i < count / 2 ? 1f : .9f)
        }));
    }
    string attached = Path.Combine(folder, "attached.opok");
    var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "tools", "media", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true };
    foreach (string argument in new[] { "-hide_banner", "-v", "error", "-i", package, "-map", "0", "-c", "copy", "-attach", attach, "-metadata:s:t:0", "mimetype=application/json", "-metadata:s:t:0", "filename=" + ScoringReferenceService.AttachmentName, "-f", "matroska", attached }) start.ArgumentList.Add(argument);
    using (var process = System.Diagnostics.Process.Start(start)!) { await process.WaitForExitAsync(); Check("產生含評分資料的雙音軌封裝", process.ExitCode == 0); }
    var imported = await packages.ImportAsync(attached, "參考旋律", "封裝測試");
    var loaded = await references.LoadAsync(imported, CancellationToken.None);
    Check("匯入後兩條音軌與內嵌評分資料都保留", loaded != null && (await packages.InspectAsync(imported.MediaPath)).Duration > 0);
    Check("舊歌曲可用原有音高資料恢復缺少的音條", loaded?.Document.LoudnessDb == null && loaded?.Notes.Length == 2 && loaded.Notes[1].Midi == 74);
    var cached = await references.LoadAsync(imported, CancellationToken.None); Check("重新載入使用已驗證快取", cached?.Notes.Length == loaded?.Notes.Length);
    await using (var importedMedia = File.OpenRead(imported.MediaPath))
    {
        string hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(importedMedia));
        string cache = Path.Combine(store.DataDirectory, "scoring", hash + ".json");
        await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(new { MediaHash = hash, Reference = (ReferenceDocument?)null }));
        var recovered = await references.LoadAsync(imported, CancellationToken.None);
        var repairedCache = await ScoringReferenceService.LoadCachedAsync(store.DataDirectory, imported.MediaPath, CancellationToken.None);
        Check("快取 Reference 為 null 時讀取內嵌資料並修復快取", recovered?.Notes.SequenceEqual(loaded!.Notes) == true && repairedCache?.Notes.Length == 2);
        using (File.Open(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var fallback = await references.LoadAsync(imported, CancellationToken.None);
            Check("快取遭鎖時仍可載入有效的內嵌評分", fallback?.Notes.SequenceEqual(loaded!.Notes) == true);
        }
    }
    string vocalFile = Path.Combine(folder, "vocal-analysis.wav");
    float[] vocalSamples = [..Tone(1.503, 440), ..new float[PitchDetector.SampleRate / 2], ..Tone(1.107, 660)];
    using (var writer = new WaveFileWriter(vocalFile, WaveFormat.CreateIeeeFloatWaveFormat(PitchDetector.SampleRate, 1)))
        writer.WriteSamples(vocalSamples, 0, vocalSamples.Length);
    var analyzed = await references.AnalyzeVocalsAsync(vocalFile, folder, null, CancellationToken.None);
    string analyzedPath = Path.Combine(folder, "analyzed-score.json");
    analyzed.Write(analyzedPath);
    var roundTrip = ReferenceTrack.Read(analyzedPath);
    Check("實際音訊分析保存音量資料與兩段音高，靜音中間不畫音條",
        roundTrip.Document.LoudnessDb?.Length == analyzed.Document.PitchHz.Length
        && roundTrip.Notes.SequenceEqual(analyzed.Notes)
        && analyzed.Notes.Any(n => n.Start < .2 && Math.Abs(n.Midi - 69) < .5)
        && analyzed.Notes.Any(n => n.Start > 1.8 && Math.Abs(n.Midi - 76) < .5)
        && analyzed.Notes.All(n => n.End <= 1.7 || n.Start >= 1.8));
    var microphones = MicrophoneCapture.Devices(); Check("實際列舉 Windows 輸入裝置", microphones != null, microphones!.Length);
    if (microphones.Length > 0)
    {
        int batches = 0, pitchFrames = 0; Exception? micError = null;
        await using var mic = new MicrophoneCapture(); mic.Level += _ => Interlocked.Increment(ref batches); mic.Frame += (_, _) => Interlocked.Increment(ref pitchFrames); mic.Failed += error => micError = error;
        await mic.StartAsync("default", references.Model); await Task.Delay(1500);
        Check("實際 WASAPI 收音與串流模型可執行", batches > 5 && pitchFrames > 10 && micError == null, new { batches, pitchFrames, error = micError?.Message });
    }
    await File.WriteAllTextAsync(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(folder);
}

catch (Exception error)
{
    await File.WriteAllTextAsync(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine(error); Environment.ExitCode = 1;
}

sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value)=>report(value);
}
