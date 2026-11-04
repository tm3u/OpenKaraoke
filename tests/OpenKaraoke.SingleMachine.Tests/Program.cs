using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenKaraoke.Library;
using OpenKaraoke.Prototype;
using OpenKaraoke.Player;

if(args.Length>1&&args[1]=="--inspect")
{
    using var state=JsonDocument.Parse(File.ReadAllText(Path.Combine(args[0],"artifacts","single-machine-state.json")));
    await using var connection=new MpvConnection(state.RootElement.GetProperty("PipeName").GetString()!);
    await connection.ConnectAsync();
    foreach(string name in new[]{"aid","track-list","audio-params","current-ao","options/ao","pause"})
    {
        try{Console.WriteLine(name+": "+await connection.GetAsync(name));}catch(Exception error){Console.WriteLine(name+": "+error.Message);}
    }
    return;
}

string projectRoot=Path.GetFullPath(args.Length>0?args[0]:".");
int toolsIndex=Array.IndexOf(args,"--tools-root");
string toolsRoot=toolsIndex>=0&&toolsIndex+1<args.Length?Path.GetFullPath(args[toolsIndex+1]):projectRoot;
string folder=Path.Combine(projectRoot,"artifacts","single-machine-tests",DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(folder);
var checks=new List<object>();
void Check(string name,bool passed)
{
    checks.Add(new {Name=name,Passed=passed});
    Console.WriteLine($"{(passed?"PASS":"FAIL")} {name}");
    if(!passed)throw new InvalidOperationException(name);
}
if(args.Contains("--published-layout"))
{
    string[] expected=["LICENSE","OpenKaraoke.exe","README.md","THIRD-PARTY-NOTICES.md"];
    Check("發布根目錄只保留啟動程式、說明與授權",Directory.GetFiles(toolsRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)));
    Check("執行環境與 GPU 元件集中在 app",new[]{"hostfxr.dll","coreclr.dll","OpenKaraoke.dll","OpenKaraoke.Phone.dll","onnxruntime.dll","onnxruntime_providers_cuda.dll"}.All(file=>File.Exists(Path.Combine(toolsRoot,"app",file))));
    Check("使用者套件不含偵錯符號與連結用程式庫",!Directory.EnumerateFiles(toolsRoot,"*",SearchOption.AllDirectories).Any(file=>Path.GetExtension(file) is ".pdb" or ".lib"));
    string report=Path.Combine(folder,"launcher-report.json"),trace=Path.Combine(folder,"host-trace.txt");
    var launch=new System.Diagnostics.ProcessStartInfo(Path.Combine(toolsRoot,"OpenKaraoke.exe"))
    {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=folder};
    foreach(string argument in new[]{"--data-dir",Path.Combine(folder,"launcher-data"),"--ai-dir",Path.Combine(folder,"launcher-ai"),"--verify-empty-ui",report})launch.ArgumentList.Add(argument);
    launch.Environment["DOTNET_ROOT"]=Path.Combine(folder,"unavailable-dotnet");
    launch.Environment["DOTNET_ROOT_X64"]=Path.Combine(folder,"unavailable-dotnet");
    launch.Environment["DOTNET_HOST_TRACE"]="1";launch.Environment["DOTNET_HOST_TRACEFILE"]=trace;
    launch.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);
    using var process=System.Diagnostics.Process.Start(launch)!;
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try{await process.WaitForExitAsync(timeout.Token);}
    catch(OperationCanceledException){process.Kill(true);throw new TimeoutException("發布啟動驗證逾時");}
    Check("沒有外部 .NET 且從其他目錄仍可直接啟動",process.ExitCode==0&&File.Exists(report));
    using var result=JsonDocument.Parse(File.ReadAllText(report));
    Check("發布啟動器保留主視窗、圖示與控制項",result.RootElement.GetProperty("Passed").GetBoolean());
    string loaded=File.ReadAllText(trace);
    Check("確實載入 app 內的隨附環境而非系統 .NET",loaded.Contains("Using app-local location")&&loaded.Contains("app\\hostfxr.dll")&&loaded.Contains("Executing as a self-contained app"));
}
foreach(string tool in new[]{"mpv","ffmpeg","ffprobe"})
{
    var isolated=new System.Diagnostics.ProcessStartInfo(Path.Combine(toolsRoot,"tools","media",tool+".exe"))
    {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=folder,RedirectStandardOutput=true,RedirectStandardError=true};
    isolated.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);
    isolated.ArgumentList.Add(tool=="mpv"?"--version":"-version");
    using var process=System.Diagnostics.Process.Start(isolated)!;
    var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try{await process.WaitForExitAsync(timeout.Token);}
    catch(OperationCanceledException){process.Kill(true);throw new TimeoutException(tool+" 啟動逾時");}
    string version=await output,details=await error;
    Check(tool+" 在不同工作目錄、沒有外部媒體工具 PATH 時仍可載入共用依賴",process.ExitCode==0&&version.Contains(tool,StringComparison.OrdinalIgnoreCase));
}
var store=new LibraryStore(Path.Combine(folder,"data"));
string media=Path.Combine(projectRoot,"fixtures","dual-track-test.mp4");
Song SongAt(int i)=>new(i.ToString(),i==0?"晴天 %_ O'Hara":"歌曲 "+i,i%2==0?"周杰倫":"測試歌手",media,120,1,2);
for(int i=0;i<135;i++)store.Add(SongAt(i));
Check("空白搜尋只回傳熱門前 10 首",store.Search("   ").Songs.Length==10&&store.Search("").Total==10);
bool duplicateRejected=false;try{store.Add(SongAt(0) with {Id="duplicate"});}catch(InvalidOperationException){duplicateRejected=true;}
Check("拒絕重複匯入相同歌名與歌手",duplicateRejected);
var trimmed=new Song("trimmed","　 歌 名  ","\t 歌 手　",media,120,1,2);
Check("歌名與歌手清除前後空白並保留中間空格",trimmed.Title=="歌 名"&&trimmed.Artist=="歌 手");
Check("修改與反序列化歌曲同樣清除前後空白",(trimmed with{Title="  新歌  "}).Title=="新歌"&&JsonSerializer.Deserialize<Song>(JsonSerializer.Serialize(trimmed))?.Title=="歌 名");
Check("異常空值保留既有驗證流程，不因清除空白而崩潰",new Song("null",null!,null!,media,120,1,2) is{Title:"",Artist:""});
Check("SQL 搜尋歌名與歌手",store.Search("周杰倫").Total==68&&store.Search("晴天").Songs.Length==1);
Check("搜尋特殊字元視為文字",store.Search("%_").Total==1&&store.Search("O'HARA").Total==1&&store.Search("' OR 1=1 --").Total==0);
Check("滾動分批結果完整且不重複",store.Search("歌曲",0).Songs.Concat(store.Search("歌曲",60).Songs).Concat(store.Search("歌曲",120).Songs).Select(s=>s.Id).Distinct().Count()==134);
var queue=new PlaybackQueue(store); var song=SongAt(4);
queue.Add(song);queue.Add(song);queue.Add(SongAt(5));
Check("重複點同一首歌各有獨立紀錄",queue.Entries.Select(e=>e.Id).Distinct().Count()==3);
Check("點播次數與加入待播交易一致",store.Search("").Songs[0].Id==song.Id&&store.Search("歌曲 4").Songs.Single(s=>s.Id==song.Id).Requests==2);
queue.Start(queue.Next()!);
string currentId=queue.CurrentId!;int currentCount=queue.Entries.Count;
queue.Remove(currentId);
Check("播放中的歌曲不能移除，且目前歌曲與保存紀錄不變",queue.CurrentId==currentId&&queue.CurrentSong==song&&queue.Entries.Count==currentCount&&queue.Entries.Single(e=>e.Id==currentId).Status=="playing"&&store.LoadSession().Entries.Any(e=>e.Id==currentId&&e.Status=="playing"));
queue.Add(song);string duplicatePendingId=queue.Entries[^1].Id;queue.Remove(duplicatePendingId);
Check("相同歌曲的待播項目可獨立移除，不影響播放中項目",queue.CurrentId==currentId&&queue.Entries.Count==currentCount&&queue.Entries.All(e=>e.Id!=duplicatePendingId));
queue.SetMode("original");queue.Complete();
Check("已唱紀錄保留當時的原唱伴奏設定",queue.Entries[0].Mode=="original");
Check("唱完保留灰色歷史狀態",queue.Entries.Count==3&&queue.Entries[0].Status=="played"&&queue.PendingCount==2);
queue.ShowPlayed=false; queue.Move(queue.Entries[2].Id,-1);queue.Save();
Check("隱藏已播放不影響待播排序",queue.Entries[1].Song.Id=="5"&&queue.Entries[0].Status=="played");
var restored=new PlaybackQueue(new LibraryStore(store.DataDirectory));
Check("重開清空所有待播與歷史，只保留顯示偏好及音軌模式",restored.Entries.Count==0&&!restored.ShowPlayed&&restored.Mode=="original"&&restored.CurrentId==null&&store.LoadSession().Entries.Length==0);
restored.Replay();
queue.Replay();Check("重播新增播放紀錄而保留歷史",queue.Entries.Count==4&&queue.Entries.Count(e=>e.Status=="played")==1);
bool rejected=false;try{store.Remove("4","錯誤歌名");}catch(InvalidOperationException){rejected=true;}
Check("刪除必須精確輸入完整歌名",rejected&&store.Search("歌曲 4").Songs.Any(s=>s.Id=="4"));
store.Remove("4",song.Title);
Check("從歌曲庫移除不破壞待播與檔案",!store.Search("歌曲 4").Songs.Any(s=>s.Id=="4")&&File.Exists(media)&&queue.Entries.Any(e=>e.Song.Id=="4"));
int before=queue.Entries.Count;
try{queue.Add(song);}catch(InvalidOperationException){ }
Check("已移除歌曲不能再次加入且交易不留殘項",queue.Entries.Count==before);
string playedId=queue.Entries.First(e=>e.Status=="played").Id;
queue.Remove(playedId);bool historyRetained=queue.Entries.Count==before;
string pendingId=queue.Next()!.Id;queue.Remove(pendingId);
var savedQueue=store.LoadSession();
Check("同次使用的已播放紀錄不可移除，待播可個別移除並保存",historyRetained&&queue.Entries.Count==before-1&&savedQueue.Entries.Any(e=>e.Id==playedId)&&savedQueue.Entries.All(e=>e.Id!=pendingId)&&queue.CurrentSong!=null);
using(var db=new SqliteConnection($"Data Source={Path.Combine(store.DataDirectory,"library.db")}"))
{
    db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA integrity_check";Check("SQLite 完整性",(string)cmd.ExecuteScalar()! == "ok");
    cmd.CommandText="SELECT sqlite_version()"; Console.WriteLine("SQLite "+cmd.ExecuteScalar());
}
var importer=new MediaImportService(toolsRoot,store);
var dual=await importer.ImportAsync("雙音軌測試","合成音訊",media,null,1,2,null,CancellationToken.None);
Check("雙音軌影片匯入到自有歌曲庫",File.Exists(dual.MediaPath)&&dual.MediaPath!=media&&dual.InstrumentalTrack==2);
var probe=await importer.ProbeAsync(dual.MediaPath);
Check("匯入後驗證影片與兩條音軌",probe.HasVideo&&probe.Tracks.Length==2&&Math.Abs(probe.Duration-120)<0.1);
string companion=Path.Combine(folder,"伴奏 660.wav");
var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(toolsRoot,"tools","media","ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
foreach(string a in new[]{"-v","error","-n","-i",Path.Combine(projectRoot,"fixtures","single-track-test.mp4"),"-map","0:a:0","-c:a","pcm_s16le",companion})start.ArgumentList.Add(a);
using(var process=System.Diagnostics.Process.Start(start)!){ var errors=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();if(process.ExitCode!=0)throw new Exception(await errors); }
var pair=await importer.ImportAsync("合併雙檔測試","合成音訊",Path.Combine(projectRoot,"fixtures","single-track-test.mp4"),companion,1,0,null,CancellationToken.None);
probe=await importer.ProbeAsync(pair.MediaPath);
Check("原唱影片與伴奏檔整合可切換雙音軌",probe.HasVideo&&probe.Tracks.Length==2&&pair.OriginalTrack==1&&pair.InstrumentalTrack==2);
rejected=false;try{await importer.ImportAsync("錯誤","測試",media,companion,1,0,null,CancellationToken.None);}catch(InvalidOperationException){rejected=true;}
Check("拒絕不同步長度的原唱與伴奏",rejected);
rejected=false;try{await importer.ImportAsync("錯誤","測試",media,null,1,1,null,CancellationToken.None);}catch(InvalidOperationException){rejected=true;}
Check("拒絕原唱伴奏對應相同音軌",rejected);
rejected=false;try{await importer.ProbeAsync(Path.Combine(projectRoot,"fixtures","invalid-test.mp4"));}catch(Exception){rejected=true;}
Check("損壞媒體檔可正常回報錯誤",rejected);
var cancelled=new CancellationToken(true);rejected=false;
try{await importer.ImportAsync("取消","測試",media,null,1,2,null,cancelled);}catch(OperationCanceledException){rejected=true;}
Check("取消匯入不留下半成品",rejected&&!Directory.EnumerateFiles(store.MediaDirectory,"*.importing.*").Any());

string originalData=Path.Combine(folder,"relocation","original"),movedData=Path.Combine(folder,"relocation","moved"),finalData=Path.Combine(folder,"relocation","moved-again");
var portable=new LibraryStore(originalData);var portableSongs=new List<Song>();
for(int i=0;i<3;i++)
{
    string id=Guid.NewGuid().ToString("N"),path=Path.Combine(portable.MediaDirectory,id,"歌曲.mp4");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.Copy(media,path);
    var item=new Song(id,"移轉歌曲 "+i,"移轉歌手",path,120,1,2);portable.Add(item);portableSongs.Add(item);
}
var portableQueue=new PlaybackQueue(portable);portableQueue.Add(portableSongs[0]);portableQueue.Start(portableQueue.Next()!);portableQueue.Complete();
portableQueue.Add(portableSongs[1]);portableQueue.Start(portableQueue.Next()!);portableQueue.SetMode("original");portableQueue.Add(portableSongs[2]);portableQueue.ShowPlayed=false;portableQueue.Save();
portable.Remove(portableSongs[0].Id,portableSongs[0].Title);
using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(originalData,"library.db")}.ToString()))
{
    db.Open();using var command=db.CreateCommand();
    command.CommandText="SELECT count(*) FROM songs WHERE media_path LIKE 'media%'";Check("新歌曲在資料庫保存相對媒體路徑",Convert.ToInt32(command.ExecuteScalar())==3);
}
void MoveTestData(string source,string destination)
{
    string allowed=Path.GetFullPath(folder)+Path.DirectorySeparatorChar;
    if(!Path.GetFullPath(source).StartsWith(allowed,StringComparison.OrdinalIgnoreCase)||!Path.GetFullPath(destination).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("移轉驗收路徑超出隔離資料夾。");
    SqliteConnection.ClearAllPools();Directory.Move(source,destination);
}
MoveTestData(originalData,movedData);var movedStore=new LibraryStore(movedData);var movedSession=movedStore.LoadSession();
Check("搬移資料目錄後，相對媒體路徑指向新位置",!Directory.Exists(originalData)&&portableSongs.All(s=>movedStore.FindById(s.Id) is{} moved&&moved.MediaPath.StartsWith(movedData+Path.DirectorySeparatorChar)&&File.Exists(moved.MediaPath)));
Check("移轉保留已刪除歌曲歷史、目前與待播順序及音軌設定",movedStore.Search("移轉歌曲").Total==2&&movedSession.Entries.Select(e=>e.Status).SequenceEqual(new[]{"played","playing","pending"})&&movedSession.Entries.All(e=>File.Exists(e.Song.MediaPath))&&!movedSession.ShowPlayed&&movedSession.Mode=="original"&&movedSession.Entries[1].Mode=="original");
Check("搬移後的歌曲可以實際載入播放引擎",(await new MediaImportService(toolsRoot,movedStore).ProbeAsync(movedSession.Entries[1].Song.MediaPath)).Tracks.Length==2);
MoveTestData(movedData,finalData);var finalStore=new LibraryStore(finalData);var finalQueue=new PlaybackQueue(finalStore);
Check("再次搬移後歌庫可用，啟動清空所有待播及歷史",finalQueue.CurrentId==null&&finalQueue.PendingCount==0&&finalQueue.Entries.Count==0&&finalStore.Search("移轉歌曲").Songs.All(s=>s.MediaPath.StartsWith(finalData+Path.DirectorySeparatorChar)&&File.Exists(s.MediaPath))&&finalQueue.Mode=="original"&&!finalQueue.ShowPlayed);

string recoveryData=Path.Combine(folder,"session-recovery");var recoveryStore=new LibraryStore(recoveryData);recoveryStore.Add(SongAt(0));
QueueEntry validPending=new(Guid.NewGuid().ToString("N"),SongAt(0)),validHistory=new(Guid.NewGuid().ToString("N"),SongAt(0),"played","original");
void WriteSession(string data,string json)
{
    using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"library.db")}.ToString());db.Open();using var command=db.CreateCommand();
    command.CommandText="INSERT INTO session(id,json) VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=$json";command.Parameters.AddWithValue("$json",json);command.ExecuteNonQuery();
}
string partial="{\"Entries\":["+JsonSerializer.Serialize(validPending)+",null,{\"Id\":3},"+JsonSerializer.Serialize(validHistory)+","+JsonSerializer.Serialize(validPending)+"],\"ShowPlayed\":false,\"Mode\":\"original\"}";
WriteSession(recoveryData,partial);var recoveredQueue=new PlaybackQueue(recoveryStore);
Check("啟動隔離異常紀錄後清空待播，只保留偏好設定",recoveredQueue.Entries.Count==0&&!recoveredQueue.ShowPlayed&&recoveredQueue.Mode=="original"&&recoveryStore.RecoveryMessage.Length>0);
Check("修復待播資料前完整保留原始內容",Directory.EnumerateFiles(recoveryData,"session-recovery-*.json").Single() is{} backup&&File.ReadAllText(backup)==partial);
var recoveredAgain=new PlaybackQueue(new LibraryStore(recoveryData));Check("修復後重開仍為空清單，不重複產生異常備份",recoveredAgain.Entries.Count==0&&Directory.EnumerateFiles(recoveryData,"session-recovery-*.json").Count()==1);
WriteSession(recoveryData,"{broken");Check("整份待播 JSON 損毀時仍可載入歌曲庫",new PlaybackQueue(recoveryStore).Entries.Count==0&&recoveryStore.Search("晴天").Total==1&&Directory.EnumerateFiles(recoveryData,"session-recovery-*.json").Any(p=>File.ReadAllText(p)=="{broken"));
string startupData=Path.Combine(folder,"startup-recovery");_ = new LibraryStore(startupData);WriteSession(startupData,"{broken");Directory.CreateDirectory(Path.Combine(startupData,"creation"));File.WriteAllText(Path.Combine(startupData,"creation","jobs.json"),"{broken");
await using(var shortcutPlayer=new MpvPlayer(_=>{}))
{
    await shortcutPlayer.StartAsync(Path.Combine(toolsRoot,"tools","media","mpv.exe"),0,headless:true);
    await using var connection=new MpvConnection(shortcutPlayer.PipeName);
    await connection.ConnectAsync();
    var receivedActions=new List<string>();
    foreach(string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory,"mpv-input.conf")))
    {
        var binding=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Receive(string action)=>completion.TrySetResult(action);
        shortcutPlayer.ShortcutRequested+=Receive;
        try
        {
            await connection.CommandAsync("keypress",binding[0]);
            receivedActions.Add(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally{shortcutPlayer.ShortcutRequested-=Receive;}
    }
    Check("播放器快捷鍵完整傳遞全螢幕、播放、音軌與升降調操作",receivedActions.SequenceEqual(new[]{"fullscreen","exit-fullscreen","pause","original","instrumental","key-up","key-down","key-reset"}));
}
await File.WriteAllTextAsync(Path.Combine(projectRoot,"artifacts","single-machine-tests.json"),JsonSerializer.Serialize(new {Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("全部通過。");
