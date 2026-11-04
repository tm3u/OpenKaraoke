using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using OpenKaraoke.Library;
using OpenKaraoke.Prototype;
using OpenKaraoke.Scoring;

string root=Path.GetFullPath(args.Length>0?args[0]:".");
string folder=Path.Combine(root,"artifacts","workflow-tests-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(folder);
var checks=new List<object>();
void Check(string name,bool passed,object? evidence=null)
{
    checks.Add(new{Name=name,Passed=passed,Evidence=evidence});Console.WriteLine($"{(passed?"PASS":"FAIL")} {name}");if(!passed)throw new InvalidOperationException(name);
}
async Task Wait(Func<bool> condition)
{
    var clock=Stopwatch.StartNew();while(!condition()){if(clock.Elapsed.TotalSeconds>35)throw new TimeoutException("工作列驗收逾時");await Task.Delay(20);}
}
async Task AttachFixtureReferenceAsync(string package)
{
    string referenceFile=Path.ChangeExtension(package,"score.json"),temporary=Path.ChangeExtension(package,"with-score.opok");
    ReferenceTrack.FromFrames(Enumerable.Range(0,7500).Select(i=>new PitchFrame(i*.016,440,1)).ToArray(),120).Write(referenceFile);
    var start=new ProcessStartInfo(Path.Combine(root,"tools","media","ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true};
    foreach(string argument in new[]{"-hide_banner","-nostdin","-v","error","-n","-i",package,"-map","0","-c","copy","-attach",referenceFile,"-metadata:s:t:0","mimetype=application/json","-metadata:s:t:0","filename="+ScoringReferenceService.AttachmentName,"-f","matroska",temporary})start.ArgumentList.Add(argument);
    using var process=Process.Start(start)!;await process.WaitForExitAsync();
    if(process.ExitCode!=0)throw new IOException("無法封裝合成驗收基準。");
    File.Move(temporary,package,true);
}
try
{
    var store=new LibraryStore(Path.Combine(folder,"data"));var packages=new SongPackageService(root,store);
    if(args.Contains("--youtube"))
    {
        var runtimeReal=new AiRuntime(root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenKaraoke","ai"));
        var creatorReal=new SongCreationService(root,runtimeReal,packages);var processorReal=new SongProcessingService(store,creatorReal,packages,new ScoringReferenceService(root,store.DataDirectory,runtimeReal));
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var video=await creatorReal.ReadVideoAsync(args[Array.IndexOf(args,"--youtube")+1],null,timeout.Token);
        string followup=Path.Combine(folder,"接續匯入_驗收歌手.opok");await packages.ExportAsync(Path.Combine(root,"fixtures","dual-track-test.mp4"),1,2,"接續匯入","驗收歌手",followup,timeout.Token);
        await AttachFixtureReferenceAsync(followup);
        await using var realQueue=new SongProcessingQueue(Path.Combine(folder,"queue"),processorReal.ProcessAsync);
        var completed=new List<Song>();string lastStage="";
        realQueue.SongAdded+=song=>{lock(completed)completed.Add(song);};
        realQueue.Changed+=()=>
        {
            var current=realQueue.Items.FirstOrDefault(i=>i.Status=="active");if(current==null)return;
            string stage=current.Stage+(current.Percent.HasValue?" "+(int)(current.Percent.Value/20)*20+"%":"");
            if(stage!=lastStage){lastStage=stage;Console.WriteLine(stage);}
        };
        realQueue.AddCreate(video,"實際製作流程","驗收歌手");realQueue.AddImport(followup,"接續匯入","驗收歌手");
        while(realQueue.Items.Length>0)
        {
            timeout.Token.ThrowIfCancellationRequested();var failed=realQueue.Items.FirstOrDefault(i=>i.Status=="error");
            if(failed!=null)throw new IOException(failed.Error);await Task.Delay(100,timeout.Token);
        }
        await Task.Delay(100,timeout.Token);
        Check("實際 YouTube 製作與匯入共用依序處理工作列",completed.Select(s=>s.Title).SequenceEqual(new[]{"實際製作流程","接續匯入"}));
        Check("製作完成無須預聽或手動確認即可入庫",store.Search("實際製作流程").Total==1&&completed[0].MediaPath.EndsWith("實際製作流程_驗收歌手.opok"));
        var referencesReal=new ScoringReferenceService(root,store.DataDirectory,runtimeReal);
        Check("新製作歌曲保留可用的旋律評分資料",await referencesReal.LoadAsync(completed[0],timeout.Token)!=null);
        Check("後續匯入亦自動入庫且所有完成工作均移除",store.Search("接續匯入").Total==1&&realQueue.Items.Length==0);
        await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(folder);return;
    }
    string package=Path.Combine(folder,"來源歌曲_來源歌手.opok");await packages.ExportAsync(Path.Combine(root,"fixtures","dual-track-test.mp4"),1,2,"來源歌曲","來源歌手",package);
    await AttachFixtureReferenceAsync(package);
    string sourceHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package)));
    int active=0,peak=0;var order=new List<string>();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int failOnce=1;
    async Task<Song> Process(SongWorkItem item,string work,IProgress<CreationProgress> progress,CancellationToken token)
    {
        int current=Interlocked.Increment(ref active);peak=Math.Max(peak,current);lock(order)order.Add(item.Title);
        try
        {
            progress.Report(new("test","加入歌曲庫",35));await release.Task.WaitAsync(token);
            if(item.Title=="失敗重試"&&Interlocked.Exchange(ref failOnce,0)==1)throw new IOException("明確的驗收失敗");
            return await packages.ImportAsync(package,item.Title,item.Artist,token,item.Id);
        }
        finally{Interlocked.Decrement(ref active);}
    }
    await using(var queue=new SongProcessingQueue(Path.Combine(folder,"queue"),Process,store))
    {
        int added=0;queue.SongAdded+=_=>Interlocked.Increment(ref added);
        queue.AddCreate(new("https://www.youtube.com/watch?v=LbiWaeLdqeA","不應用作歌名",120),"製作甲","驗收歌手");
        queue.AddImport(package,"匯入乙","驗收歌手");queue.AddImport(package,"匯入丙","驗收歌手");
        await Wait(()=>queue.Items.Any(i=>i.Status=="active"&&i.Percent==35));
        Check("製作與匯入共用可累加清單",queue.Items.Length==3&&queue.Items.Select(i=>i.Kind).SequenceEqual(new[]{"create","import","import"}));
        Check("任一時間只有一個工作執行",peak==1&&queue.Items.Count(i=>i.Status=="active")==1);
        Check("進度更新可被介面讀取",queue.Items.Single(i=>i.Status=="active").Percent==35);
        bool duplicatePending=false;try{queue.AddImport(package," 匯入乙 "," 驗收歌手 ");}catch(InvalidOperationException){duplicatePending=true;}
        Check("製作與匯入不可重複排入相同歌名歌手",duplicatePending&&queue.Items.Length==3);
        release.TrySetResult();await Wait(()=>queue.Items.Length==0&&added==3);
        Check("完成自動入庫且清單移除",store.Search("驗收歌手").Total==3&&queue.Items.Length==0);
        Check("依加入順序執行且無平行分離",order.Take(3).SequenceEqual(new[]{"製作甲","匯入乙","匯入丙"})&&peak==1);
        bool duplicateLibrary=false;try{queue.AddCreate(new("https://www.youtube.com/watch?v=LbiWaeLdqeA","測試",120),"製作甲","驗收歌手");}catch(InvalidOperationException){duplicateLibrary=true;}
        Check("已入庫歌曲在執行 AI 或複製前拒絕重複",duplicateLibrary&&queue.Items.Length==0);
        string failed=queue.AddImport(package,"失敗重試","驗收歌手");queue.AddImport(package,"後續照常","驗收歌手");
        await Wait(()=>queue.Items.Length==1&&queue.Items[0].Status=="error"&&store.Search("後續照常").Total==1);
        Check("單筆失敗不阻塞後續工作",queue.Items[0].Id==failed&&queue.Items[0].Error.Contains("驗收失敗"));
        queue.Retry(failed);await Wait(()=>queue.Items.Length==0&&store.Search("失敗重試").Total==1);
        Check("失敗可重試且成功後自動移除",true);
    }
    var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task<Song> Blocked(SongWorkItem item,string work,IProgress<CreationProgress> progress,CancellationToken token)
    {await gate.Task.WaitAsync(token);return await packages.ImportAsync(package,item.Title,item.Artist,token,item.Id);}
    await using(var queue=new SongProcessingQueue(Path.Combine(folder,"cancel"),Blocked))
    {
        string first=queue.AddImport(package,"取消當前","驗收歌手"),second=queue.AddImport(package,"取消待處理","驗收歌手");
        await Wait(()=>queue.Items.Any(i=>i.Id==first&&i.Status=="active"));queue.Cancel(second);
        Check("尚未執行的工作可取消",queue.Items.Single(i=>i.Id==second).Status=="cancelled");
        queue.Cancel(first);await Wait(()=>queue.Items.All(i=>i.Status=="cancelled"));
        Check("目前工作可取消且不留下入庫半成品",store.Search("取消當前").Total==0);
        gate.TrySetResult();queue.Retry(first);queue.Retry(second);await Wait(()=>queue.Items.Length==0);
        Check("取消的工作可重試",store.Search("取消當前").Total==1&&store.Search("取消待處理").Total==1);
    }
    string restartFolder=Path.Combine(folder,"restart");gate=new(TaskCreationOptions.RunContinuationsAsynchronously);
    var before=new SongProcessingQueue(restartFolder,Blocked);
    before.AddImport(package,"重開甲","驗收歌手");before.AddImport(package,"重開乙","驗收歌手");
    await Wait(()=>before.Items.Any(i=>i.Status=="active"));await before.DisposeAsync();
    Check("關閉主程式保留尚未完成的工作",before.Items.All(i=>i.Status=="queued")&&before.Items.Length==2);
    var runtime=new AiRuntime(root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenKaraoke","ai"));
    var creator=new SongCreationService(root,runtime,packages);var service=new SongProcessingService(store,creator,packages,new ScoringReferenceService(root,store.DataDirectory,runtime));
    string startLockFolder = Path.Combine(folder, "start-lock");
    Directory.CreateDirectory(startLockFolder);
    var startLocked = new SongWorkItem(Guid.NewGuid().ToString("N"), "import", "啟動時鎖定", "驗收歌手", package);
    string startState = Path.Combine(startLockFolder, "jobs.json");
    await File.WriteAllTextAsync(startState, JsonSerializer.Serialize(new[] { startLocked }));
    await using (var queue = new SongProcessingQueue(startLockFolder, service.ProcessAsync, store))
    {
        using (File.Open(startState, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            queue.Start();
            await Wait(() => queue.Items.Single().Status == "error");
            Check("工作啟動時清單鎖定會回報可重試錯誤", queue.Items.Single().Stage.Contains("儲存處理清單") && store.FindById(startLocked.Id) == null);
        }
        queue.Retry(startLocked.Id);
        await Wait(() => queue.Items.Length == 0);
        Check("清單解鎖後可直接重試完成", store.FindById(startLocked.Id) != null);
    }

    string finishLockFolder = Path.Combine(folder, "finish-lock");
    var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using (var queue = new SongProcessingQueue(finishLockFolder, async (item, work, progress, token) =>
    {
        await finish.Task.WaitAsync(token);
        return await service.ProcessAsync(item, work, progress, token);
    }, store))
    {
        int notifications = 0;
        queue.SongAdded += _ => Interlocked.Increment(ref notifications);
        string id = queue.AddImport(package, "完成時鎖定", "驗收歌手");
        await Wait(() => queue.Items.Single().Status == "active");
        using (File.Open(Path.Combine(finishLockFolder, "jobs.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            finish.TrySetResult();
            await Wait(() => queue.Items.Single().Status == "error");
            Check("入庫後清單寫入失敗仍保留可重試項目", store.FindById(id) != null && queue.Items.Single().Stage.Contains("儲存處理清單"));
        }
        queue.Retry(id);
        await Wait(() => queue.Items.Length == 0 && Volatile.Read(ref notifications) == 1);
        Check("重試完成寫入時只保留一份歌曲", store.Search("完成時鎖定").Total == 1);
    }
    await using(var restored=new SongProcessingQueue(restartFolder,service.ProcessAsync))
    {Check("重新開啟還原工作順序",restored.Items.Select(i=>i.Title).SequenceEqual(new[]{"重開甲","重開乙"}));restored.Start();await Wait(()=>restored.Items.Length==0);}
    Check("還原後仍可使用實際匯入服務入庫",store.Search("重開").Total==2);
    var importedLyrics=new SongLyrics("加入時歌詞.lrc","[00:01.00]<00:01.00>一<00:02.00>起<00:03.00>唱<00:04.00>","karaoke");
    await using(var lyricsQueue=new SongProcessingQueue(Path.Combine(folder,"lyrics"),service.ProcessAsync,store))
    {
        lyricsQueue.AddImport(package,"包含歌詞的歌曲","驗收歌手",importedLyrics);
        await Wait(()=>lyricsQueue.Items.Length==0||lyricsQueue.Items.Any(i=>i.Status=="error"));
        Check("實際工作服務將當次歌詞隨歌曲入庫",lyricsQueue.Items.Length==0&&store.ContainsSong("包含歌詞的歌曲","驗收歌手"),lyricsQueue.Items);
        var lyricsSong=store.Search("包含歌詞的歌曲").Songs.Single();
        Check("歌詞與原有雙音軌及評分附件並存",(await packages.InspectAsync(lyricsSong.MediaPath)).LyricsMode=="karaoke"
            &&await new ScoringReferenceService(root,store.DataDirectory,runtime).LoadAsync(lyricsSong,CancellationToken.None)!=null);
        Check("背景工作同時保存同名逐字 ASS 副本",Path.GetExtension(lyricsSong.MediaPath)==".opok"
            &&File.ReadAllText(Path.ChangeExtension(lyricsSong.MediaPath,".ass"))==importedLyrics.ToSidecar(120));
        var copy=await packages.ImportAsync(lyricsSong.MediaPath,"保留既有歌詞","驗收歌手");
        Check("重新匯入已有歌詞的歌曲會保留字幕與評分資料",(await packages.InspectAsync(copy.MediaPath)).LyricsMode=="karaoke"
            &&await new ScoringReferenceService(root,store.DataDirectory,runtime).LoadAsync(copy,CancellationToken.None)!=null
            &&File.Exists(Path.ChangeExtension(copy.MediaPath,".ass")));
    }
    string operation=Guid.NewGuid().ToString("N");var once=await packages.ImportAsync(package,"重複提交","驗收歌手",operationId:operation);
    var twice=await packages.ImportAsync(package,"重複提交","驗收歌手",operationId:operation);
    Check("工作提交可復原，不重複建立歌曲",once.Id==twice.Id&&store.Search("重複提交").Total==1);
    var unchanged=await packages.ImportAsync(package,"重複提交","驗收歌手",operationId:operation,lyrics:importedLyrics);
    Check("已完成的工作不會透過重試補加或替換歌詞",unchanged.Id==once.Id&&(await packages.InspectAsync(unchanged.MediaPath)).LyricsMode=="");
    Check("匯入保留原始歌曲檔內容",sourceHash==Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package))));
    bool namesRejected=false;try{await using var queue=new SongProcessingQueue(Path.Combine(folder,"invalid"),Process);queue.AddImport(package," ","歌手");}catch(ArgumentException){namesRejected=true;}
    Check("空白歌名不能加入工作列",namesRejected);
    string recoveryFolder=Path.Combine(folder,"recovery");Directory.CreateDirectory(recoveryFolder);
    var recoverFirst=new SongWorkItem(Guid.NewGuid().ToString("N"),"import","復原甲","驗收歌手",package,Status:"active");
    var recoverSecond=new SongWorkItem(Guid.NewGuid().ToString("N"),"import","復原乙","驗收歌手",package);
    string brokenJobs="["+JsonSerializer.Serialize(recoverFirst)+",null,{\"Id\":3},"+JsonSerializer.Serialize(recoverSecond)+","+JsonSerializer.Serialize(recoverFirst)+"]";
    File.WriteAllText(Path.Combine(recoveryFolder,"jobs.json"),brokenJobs);
    await using(var recovered=new SongProcessingQueue(recoveryFolder,service.ProcessAsync,store))
    {
        Check("單筆處理資料異常不阻止其他工作還原",recovered.Items.Select(i=>i.Id).SequenceEqual(new[]{recoverFirst.Id,recoverSecond.Id})&&recovered.Items.All(i=>i.Status=="queued")&&recovered.RecoveryMessage.Length>0);
        Check("處理清單修復前完整備份原檔",File.ReadAllText(Directory.EnumerateFiles(recoveryFolder,"jobs-recovery-*.json").Single())==brokenJobs);
        recovered.Start();await Wait(()=>recovered.Items.Length==0);
        Check("隔離異常後的有效工作仍可完成實際匯入",store.Search("復原").Total==2);
    }
    File.WriteAllText(Path.Combine(recoveryFolder,"jobs.json"),"{broken");
    await using(var recovered=new SongProcessingQueue(recoveryFolder,service.ProcessAsync,store))
    {
        Check("整份處理 JSON 損毀時可啟動並保留原檔",recovered.Items.Length==0&&recovered.RecoveryMessage.Length>0&&Directory.EnumerateFiles(recoveryFolder,"jobs-recovery-*.json").Any(p=>File.ReadAllText(p)=="{broken"));
        recovered.AddImport(package,"損毀後新工作","驗收歌手");await Wait(()=>recovered.Items.Length==0&&store.ContainsSong("損毀後新工作","驗收歌手"));
        Check("整份處理清單損毀後仍可加入及完成新工作",true);
    }
    await AiPrerequisiteVerification.RunAsync(folder,package,Check);
    await AiDownloadVerification.RunAsync(folder,Check);
    await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(folder);
}
catch(Exception error)
{
    await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));Console.Error.WriteLine(error);Environment.ExitCode=1;
}
