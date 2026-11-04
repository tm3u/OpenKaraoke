using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using OpenKaraoke.Library;
using OpenKaraoke.Prototype;

string root = Path.GetFullPath(args[0]);
if(args.Contains("--resolve-clip"))
{
    string resolved=await SongCreationService.ResolveFullVideoUrlAsync(args[Array.IndexOf(args,"--resolve-clip")+1]);
    if(new Uri(resolved).AbsolutePath!="/watch")throw new InvalidOperationException("沒有取得完整影片網址");
    Console.WriteLine("FULL_VIDEO "+resolved);return;
}
if (args.Contains("--youtube"))
{
    string url = args[Array.IndexOf(args, "--youtube") + 1];
    string work = Path.Combine(root, "artifacts", "youtube-creation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(work);
    string aiDirectory=Path.Combine(root,".tools","ai-runtime");
    if(!Directory.Exists(aiDirectory))aiDirectory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenKaraoke","ai");
    if(args.Contains("--ai-dir"))aiDirectory=Path.GetFullPath(args[Array.IndexOf(args,"--ai-dir")+1]);
    var runtime = new AiRuntime(root, aiDirectory);
    var library = new LibraryStore(Path.Combine(work, "library"));
    var packaging = new SongPackageService(root, library);
    var service = new SongCreationService(root, runtime, packaging);
    var progress = new Progress<CreationProgress>(p => Console.WriteLine(p.Stage + " " + p.Detail + (p.Percent.HasValue ? $" {p.Percent:0}%" : "")));
    var video = await service.ReadVideoAsync(url, progress, CancellationToken.None);
    Console.WriteLine("SOURCE " + video.Title);
    string title=args.Contains("--title")?args[Array.IndexOf(args,"--title")+1]:"測試歌曲";
    string artist=args.Contains("--artist")?args[Array.IndexOf(args,"--artist")+1]:"測試歌手";
    var result = await service.CreateAsync(video, title, artist, work, progress, CancellationToken.None);
    await File.WriteAllTextAsync(Path.Combine(work, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("RESULT " + result.Package);
    return;
}
if (args.Contains("--setup-ai"))
{
    var runtime = new AiRuntime(root, Path.Combine(root, ".tools", "ai-runtime"));
    await runtime.EnsureAsync(new Progress<CreationProgress>(p => Console.WriteLine(p.Detail + (p.Percent.HasValue ? $" {p.Percent:0}%" : ""))), CancellationToken.None);
    Console.WriteLine("AI READY " + runtime.DirectoryPath);
    return;
}
string folder = Path.Combine(root, "artifacts", "creation-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(folder);
var checks = new List<object>();
void Check(string name, bool passed)
{
    Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
    checks.Add(new { Name = name, Passed = passed });
    if (!passed) throw new Exception(name);
}
async Task<bool> Reject(Func<Task> action)
{
    try { await action(); return false; } catch (Exception error) when (error is ArgumentException or InvalidOperationException or OperationCanceledException or IOException) { return true; }
}
var store = new LibraryStore(Path.Combine(folder, "library"));
var packages = new SongPackageService(root, store);
string source = Path.Combine(root, "fixtures", "dual-track-test.mp4");
string output = Path.Combine(folder, "測試歌曲_測試歌手.opok");
await packages.ExportAsync(source, 1, 2, "測試歌曲", "測試歌手", output);
var info = await packages.InspectAsync(output);
Check("匯出為真正包含雙音軌的 opok 歌曲檔", info.Title == "測試歌曲" && info.Artist == "測試歌手" && Math.Abs(info.Duration - 120) < 0.1);
Check("標準檔名自動填入歌名與歌手", SongPackageService.ParseFilename(output) == ("測試歌曲", "測試歌手"));
Check("不符格式時完整檔名填入歌名", SongPackageService.ParseFilename("歌_手_更多.opok") == ("歌_手_更多", ""));
Check("沒有分隔符時歌手保持空白", SongPackageService.ParseFilename("歌曲.opok") == ("歌曲", ""));
Check("匯入檔名的歌名、歌手自動清除前後空白",SongPackageService.ParseFilename("　 測試歌曲 _ 測試歌手　.opok")==("測試歌曲","測試歌手")&&SongPackageService.ParseFilename("　歌曲　.opok")==("歌曲",""));
string canonical="https://www.youtube.com/watch?v=jNQXAC9IVRw";
Check("一般、短網址與內嵌網址忽略時間、片段範圍及播放清單",new[]{canonical+"&t=30&end=40&list=PL123", "https://youtu.be/jNQXAC9IVRw?t=30#t=40", "https://www.youtube.com/embed/jNQXAC9IVRw?start=30&end=50"}.All(url=>SongCreationService.NormalizeYouTubeUrl(url)==canonical));
string clip="https://www.youtube.com/clip/UgytZKpehg-hEMBSn3F4AaABCQ";
Check("YouTube Clip 分享網址接受並移除額外參數",SongCreationService.NormalizeYouTubeUrl(clip+"?si=example&t=30")==clip);
string clipPage="<script>var ytInitialData={\"currentVideoEndpoint\":{\"watchEndpoint\":{\"videoId\":\"jNQXAC9IVRw\"}},\"clip\":{\"startTimeMs\":30000,\"endTimeMs\":40000}};</script>";
Check("Clip 解析取得完整原片網址，不使用片段起訖",SongCreationService.FullVideoFromClipPage(clipPage)==canonical);
Check("缺少原始影片資料的 Clip 不會誤取其他影片",await Reject(()=>Task.FromResult(SongCreationService.FullVideoFromClipPage("<script>{\"videoId\":\"jNQXAC9IVRw\"}</script>"))));
byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(output));
var song = await packages.ImportAsync(output, "重新命名", "另一位歌手");
Check("匯入建立獨立副本並使用歌名歌手命名", song.MediaPath != output && Path.GetFileName(song.MediaPath) == "重新命名_另一位歌手.opok" && File.Exists(song.MediaPath));
byte[] sourceHashAfter = SHA256.HashData(await File.ReadAllBytesAsync(output));
Check("來源檔內容保持原樣", hash.SequenceEqual(sourceHashAfter));
info = await packages.InspectAsync(song.MediaPath);
Check("副本內歌曲資料與資料庫一致", info.Title == song.Title && info.Artist == song.Artist && store.Search("重新命名").Total == 1);
Check("拒絕直接匯入 mp4", await Reject(() => packages.ImportAsync(source, "歌曲", "歌手")));
string renamed = Path.Combine(folder, "改副檔名.opok"); File.Copy(source, renamed);
Check("拒絕只改副檔名的影片", await Reject(() => packages.InspectAsync(renamed)));
Check("歌名與歌手必填且空白無效", await Reject(() => packages.ImportAsync(output, " ", "歌手")) && await Reject(() => packages.ImportAsync(output, "歌曲", " ")));
Check("取消匯入不留下歌曲與暫存檔", await Reject(() => packages.ImportAsync(output, "取消", "測試", new CancellationToken(true))) && store.Search("取消").Total == 0 && Directory.GetDirectories(store.MediaDirectory).Length == 1);
Check("重複匯入失敗時清理新副本", await Reject(() => packages.ImportAsync(output, song.Title, song.Artist)) && Directory.GetDirectories(store.MediaDirectory).Length == 1);
string legacy = Path.ChangeExtension(output, ".openkaraoke"); File.Copy(output, legacy);
var legacySong = await packages.ImportAsync(legacy, "舊格式匯入", "測試歌手");
Check("舊 openkaraoke 可匯入為 opok，原始檔維持不變", legacySong.MediaPath.EndsWith("舊格式匯入_測試歌手.opok")
    && SHA256.HashData(await File.ReadAllBytesAsync(legacy)).SequenceEqual(hash));
Check("新格式不只是改副檔名，仍驗證實際歌曲內容", SongPackageService.IsPackageFile("歌曲.OPOK")
    && SongPackageService.IsPackageFile(legacy) && !SongPackageService.IsPackageFile(source));
Check("未匯入歌詞的歌曲不建立獨立歌詞檔", Directory.GetFiles(Path.GetDirectoryName(legacySong.MediaPath)!).Length == 1);
await LyricsChecks.RunAsync(root, Path.Combine(folder, "lyrics"), Check);
await File.WriteAllTextAsync(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("REPORT " + folder);
