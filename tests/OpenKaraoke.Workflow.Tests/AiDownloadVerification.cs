using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenKaraoke.Prototype;

internal static class AiDownloadVerification
{
    public static async Task RunAsync(string folder,Action<string,bool,object?> check)
    {
        string root=Path.Combine(folder,"download-fixture"),runtimeFolder=Path.Combine(root,"ai");
        Directory.CreateDirectory(Path.Combine(root,"tools","creation"));Directory.CreateDirectory(Path.Combine(root,"tools","media"));
        await File.WriteAllBytesAsync(Path.Combine(root,"tools","media","ffmpeg.exe"),[1,2,3]);
        await File.WriteAllBytesAsync(Path.Combine(root,"tools","media","ffprobe.exe"),[1,2,3]);
        byte[] downloader=new byte[256*1024];Random.Shared.NextBytes(downloader);
        byte[] Archive(string name)
        {
            using var memory=new MemoryStream();
            using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
            {using var file=zip.CreateEntry(name).Open();file.Write([1,2,3]);}
            return memory.ToArray();
        }
        var bodies=new Dictionary<string,byte[]>{{"downloader",downloader},{"deno",Archive("deno.exe")}};
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port,requests=0;
        using var stop=new CancellationTokenSource();
        var server=Task.Run(async()=>
        {
            try
            {
                while(!stop.IsCancellationRequested)
                {
                    using var client=await listener.AcceptTcpClientAsync(stop.Token);await using var stream=client.GetStream();
                    using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
                    string first=(await reader.ReadLineAsync(stop.Token))!;while(!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))){}
                    byte[] body=bodies[first.Split(' ')[1].TrimStart('/')];Interlocked.Increment(ref requests);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"),stop.Token);
                    for(int offset=0;offset<body.Length;offset+=16384)
                    {await stream.WriteAsync(body.AsMemory(offset,Math.Min(16384,body.Length-offset)),stop.Token);await Task.Delay(2,stop.Token);}
                }
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){}
        });
        try
        {
            var manifest=new Dictionary<string,string>{{"version","fixture"}};
            foreach(var pair in bodies)
            {
                manifest[pair.Key+"Url"]=$"http://127.0.0.1:{port}/{pair.Key}";
                manifest[pair.Key+"Sha256"]=Convert.ToHexStringLower(SHA256.HashData(pair.Value));
            }
            await File.WriteAllTextAsync(Path.Combine(root,"tools","creation","runtime-manifest.json"),JsonSerializer.Serialize(manifest));
            var runtime=new AiRuntime(root,runtimeFolder);var reports=new ConcurrentQueue<CreationProgress>();
            await runtime.EnsureDownloadToolsAsync(new ProgressSink(reports.Enqueue),CancellationToken.None);
            check("實際 HTTP 下載將位元組比例回報為元件進度",reports.Any(p=>p.Detail=="下載 · 影片下載工具"&&p.Percent is >0 and <100)&&reports.Any(p=>p.Percent==100),null);
            check("HTTP 下載完成後驗證雜湊並解壓必要工具",requests==2&&new[]{runtime.Downloader,runtime.Deno}.All(File.Exists),null);
            check("媒體工具直接使用程式隨附的完整相依目錄",runtime.MediaDirectory==Path.Combine(root,"tools","media")&&!File.Exists(Path.Combine(runtimeFolder,"ffmpeg.exe")),null);
            await runtime.EnsureDownloadToolsAsync(new ProgressSink(reports.Enqueue),CancellationToken.None);
            check("已下載且驗證正確的工具不重複下載",requests==2,null);
            await File.WriteAllTextAsync(Path.Combine(runtimeFolder,"ready-fixture"),"ready");
            check("就緒標記存在但缺少 Python 與模型仍不可放行歌曲",!runtime.IsReady,null);
            Directory.CreateDirectory(Path.GetDirectoryName(runtime.Python)!);await File.WriteAllBytesAsync(runtime.Python,[1]);await File.WriteAllBytesAsync(runtime.Model,[1]);
            check("所有必要檔案與成功標記備妥後才視為就緒",runtime.IsReady,null);
            await File.WriteAllTextAsync(Path.Combine(runtimeFolder,"installing"),"incomplete");
            check("未完成安裝標記會阻止誤判就緒",!runtime.IsReady,null);
        }
        finally{stop.Cancel();listener.Stop();await server;}
    }
    private sealed class ProgressSink(Action<CreationProgress> report):IProgress<CreationProgress>
    {public void Report(CreationProgress value)=>report(value);}
}
