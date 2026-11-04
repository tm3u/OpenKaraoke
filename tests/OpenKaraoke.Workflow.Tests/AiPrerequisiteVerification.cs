using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OpenKaraoke.Library;
using OpenKaraoke.Prototype;

internal static class AiPrerequisiteVerification
{
    public static async Task RunAsync(string folder,string package,Action<string,bool,object?> check)
    {
        var video=new YouTubeVideo("https://www.youtube.com/watch?v=LbiWaeLdqeA","影片",120);
        var completed=new ConcurrentQueue<string>();
        Task<Song> Process(SongWorkItem item,string work,IProgress<CreationProgress> progress,CancellationToken token)
        {completed.Enqueue(item.Title);return Task.FromResult(new Song(item.Id,item.Title,item.Artist,package,120,1,2));}
        async Task Wait(Func<bool> condition)
        {
            var watch=Stopwatch.StartNew();while(!condition())
            {if(watch.Elapsed.TotalSeconds>10)throw new TimeoutException("AI 相依工作驗收逾時。");await Task.Delay(10);}
        }
        bool ready=false;int preparations=0,added=0;
        var started=new TaskCompletionSource<IProgress<CreationProgress>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken setupToken=default;
        await using(var queue=new SongProcessingQueue(Path.Combine(folder,"ai-first"),Process,aiReady:()=>ready,prepareAi:async(progress,token)=>
        {
            Interlocked.Increment(ref preparations);setupToken=token;started.TrySetResult(progress);
            await release.Task.WaitAsync(token);ready=true;
        }))
        {
            queue.SongAdded+=_=>Interlocked.Increment(ref added);
            string first=queue.AddCreate(video,"歌曲甲","驗收歌手"),second=queue.AddCreate(video,"歌曲乙","驗收歌手");
            var progress=await started.Task.WaitAsync(TimeSpan.FromSeconds(10));var items=queue.Items;string setup=items[0].Id;
            check("首次加入獨立 AI 項目在歌曲前且歌曲保持排隊",items.Length==3&&items[0].Kind=="setup"&&items[0].Status=="active"&&items.Skip(1).All(i=>i.Status=="queued"&&i.Stage=="等待必要 AI 元件"),null);
            check("多首歌共用一次準備且尚未執行歌曲",preparations==1&&completed.IsEmpty,null);
            progress.Report(new("setup","下載人聲分離模型",37));
            check("下載百分比只出現在 AI 項目",queue.Items[0].Percent==37&&queue.Items.Skip(1).All(i=>i.Percent==null),null);
            queue.Cancel(setup);
            check("AI 項目不能由取消 API 中止",!setupToken.IsCancellationRequested&&queue.Items[0].Status=="active",null);
            queue.Cancel(second);
            check("等待元件時仍可單獨取消歌曲",queue.Items.Single(i=>i.Id==second).Status=="cancelled"&&!setupToken.IsCancellationRequested,null);
            bool duplicate=false;try{queue.AddCreate(video,"歌曲甲","驗收歌手");}catch(InvalidOperationException){duplicate=true;}
            check("阻擋重複歌曲不新增元件項目",duplicate&&queue.Items.Count(i=>i.Kind=="setup")==1,null);
            release.TrySetResult();await Wait(()=>queue.Items.All(i=>i.Status=="cancelled")&&added==1);
            check("元件完成才處理歌曲且不把元件加入歌庫",completed.ToArray().SequenceEqual(new[]{"歌曲甲"})&&queue.Items.All(i=>i.Kind!="setup"),null);
            queue.AddCreate(video,"歌曲丙","驗收歌手");await Wait(()=>added==2);
            check("元件備妥後加入歌曲不再顯示或執行準備",preparations==1&&queue.Items.All(i=>i.Kind!="setup"),null);
        }
        ready=false;int attempt=0;completed.Clear();
        await using(var queue=new SongProcessingQueue(Path.Combine(folder,"ai-retry"),Process,aiReady:()=>ready,prepareAi:(_,_)=>
        {
            if(Interlocked.Increment(ref attempt)==1)throw new IOException("模擬下載中斷");ready=true;return Task.CompletedTask;
        }))
        {
            queue.AddCreate(video,"重試甲","驗收歌手");queue.AddCreate(video,"重試乙","驗收歌手");
            await Wait(()=>queue.Items.Any(i=>i.Kind=="setup"&&i.Status=="error"));await Task.Delay(100);
            var setup=queue.Items.Single(i=>i.Kind=="setup");
            check("元件下載失敗保留原因且歌曲等待，不自行反覆重試",setup.Error=="模擬下載中斷"&&queue.Items.Count(i=>i.Status=="queued")==2&&completed.IsEmpty&&attempt==1,null);
            queue.Cancel(setup.Id);
            check("失敗的必要元件也不能取消",queue.Items.Any(i=>i.Id==setup.Id&&i.Status=="error"),null);
            queue.Retry(setup.Id);await Wait(()=>queue.Items.Length==0);
            check("重試必要元件成功後依序開始所有等待歌曲",attempt==2&&completed.ToArray().SequenceEqual(new[]{"重試甲","重試乙"}),null);
        }
        completed.Clear();
        await using(var queue=new SongProcessingQueue(Path.Combine(folder,"ai-not-ready"),Process,aiReady:()=>false,prepareAi:(_,_)=>Task.CompletedTask))
        {
            queue.AddCreate(video,"未完成驗證","驗收歌手");await Wait(()=>queue.Items.Any(i=>i.Status=="error"));
            check("準備程序返回但未通過就緒檢查時不放行歌曲",queue.Items[0].Kind=="setup"&&queue.Items[1].Status=="queued"&&completed.IsEmpty,null);
        }
        await using(var queue=new SongProcessingQueue(Path.Combine(folder,"ai-interrupted"),Process,aiReady:()=>false,prepareAi:(_,_)=>throw new OperationCanceledException("連線被中斷")))
        {
            queue.AddCreate(video,"中斷後等待","驗收歌手");await Wait(()=>queue.Items.Any(i=>i.Status=="error"));
            check("非關閉程式造成的元件中斷保留重試並阻擋歌曲",queue.Items[0].Kind=="setup"&&queue.Items[0].Status=="error"&&queue.Items[1].Status=="queued",null);
        }
        ready=false;var allStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using(var queue=new SongProcessingQueue(Path.Combine(folder,"ai-all-cancelled"),Process,aiReady:()=>ready,prepareAi:async(_,token)=>
        {setupToken=token;allStarted.TrySetResult();await allRelease.Task.WaitAsync(token);ready=true;}))
        {
            string song=queue.AddCreate(video,"取消所有歌曲","驗收歌手");await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));queue.Cancel(song);
            check("取消所有歌曲仍繼續準備共用元件",queue.Items[0].Status=="active"&&!setupToken.IsCancellationRequested,null);
            allRelease.TrySetResult();await Wait(()=>queue.Items.All(i=>i.Status=="cancelled"));
        }
        ready=false;string restart=Path.Combine(folder,"ai-resume");
        var resumeStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original=new SongProcessingQueue(restart,Process,aiReady:()=>ready,prepareAi:async(_,token)=>
        {resumeStarted.TrySetResult();await Task.Delay(Timeout.Infinite,token);});
        original.AddCreate(video,"還原工作","驗收歌手");await resumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));string setupId=original.Items[0].Id;
        await original.DisposeAsync();
        check("應用程式關閉後保留可恢復的元件及歌曲工作",original.Items.Length==2&&original.Items.All(i=>i.Status=="queued"),null);
        await using(var restored=new SongProcessingQueue(restart,Process,aiReady:()=>ready,prepareAi:(_,_)=>{ready=true;return Task.CompletedTask;}))
        {
            check("重新開啟共用同一個元件項目，不重複建立",restored.Items[0].Id==setupId&&restored.Items.Count(i=>i.Kind=="setup")==1,null);
            restored.Start();await Wait(()=>restored.Items.Length==0);
            check("重新開啟可接續準備並完成原歌曲",completed.Contains("還原工作"),null);
        }
        ready=true;string recheck=Path.Combine(folder,"ai-dependency-recheck");
        var savedQueue=new SongProcessingQueue(recheck,async(_,_,_,token)=>{await Task.Delay(Timeout.Infinite,token);throw new InvalidOperationException();},aiReady:()=>ready,prepareAi:(_,_)=>Task.CompletedTask);
        savedQueue.AddCreate(video,"等待工作","驗收歌手");await savedQueue.DisposeAsync();
        var pending=savedQueue.Items.Single();ready=false;
        await using(var queue=new SongProcessingQueue(recheck,Process,aiReady:()=>ready,prepareAi:(_,_)=>Task.CompletedTask))
            check("元件失效後重新開啟，會在歌曲前安排必要元件",queue.Items.Length==2&&queue.Items[0].Kind=="setup"&&queue.Items[1].Id==pending.Id,null);
        var lateSetup=new SongWorkItem(Guid.NewGuid().ToString("N"),"setup","必要 AI 元件","","",Status:"error");
        File.WriteAllText(Path.Combine(recheck,"jobs.json"),JsonSerializer.Serialize(new[]{pending,lateSetup}));
        await using(var queue=new SongProcessingQueue(recheck,Process,aiReady:()=>false,prepareAi:(_,_)=>Task.CompletedTask))
            check("還原時將必要元件移至等待歌曲前",queue.Items[0].Id==lateSetup.Id&&queue.Items[1].Id==pending.Id,null);
        await using(var queue=new SongProcessingQueue(recheck,Process,aiReady:()=>true,prepareAi:(_,_)=>Task.CompletedTask))
            check("已備妥環境移除過期失敗項目",queue.Items.Length==1&&queue.Items[0].Kind=="create",null);
        string rollback=Path.Combine(folder,"ai-rollback");Directory.CreateDirectory(Path.Combine(rollback,"jobs.json.tmp"));
        await using(var queue=new SongProcessingQueue(rollback,Process,aiReady:()=>false,prepareAi:(_,_)=>Task.CompletedTask))
        {
            bool rejected=false;try{queue.AddCreate(video,"無法保存","驗收歌手");}catch(Exception error)when(error is IOException or UnauthorizedAccessException){rejected=true;}
            check("新增工作保存失敗時同時回復歌曲及元件項目",rejected&&queue.Items.Length==0,null);
        }
        int importSetup=0;
        await using(var queue=new SongProcessingQueue(Path.Combine(folder,"ai-import"),Process,aiReady:()=>false,prepareAi:(_,_)=>{importSetup++;return Task.CompletedTask;}))
        {
            queue.AddImport(package,"既有歌曲匯入","驗收歌手");await Wait(()=>queue.Items.Length==0);
            check("匯入既有歌曲不強迫下載完整 AI 環境",importSetup==0,null);
        }
    }
}
