using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenKaraoke.Prototype;

public sealed record CreationProgress(string Stage, string Detail, double? Percent = null);

public sealed class AiRuntime
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _root;
    private readonly JsonElement _manifest;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string DirectoryPath { get; }
    public string MediaDirectory => Path.Combine(_root, "tools", "media");
    public string Python => Path.Combine(DirectoryPath, "python", "python.exe");
    public string Downloader => Path.Combine(DirectoryPath, "yt-dlp.exe");
    public string Deno => Path.Combine(DirectoryPath, "deno.exe");
    public string Model => Path.Combine(DirectoryPath, "955717e8-8726e21a.th");
    public string Adapter => Path.Combine(_root, "tools", "creation", "separate.py");
    private string Version => _manifest.GetProperty("version").GetString()!;
    public bool IsReady => File.Exists(Path.Combine(DirectoryPath, "ready-" + Version))
        && !File.Exists(Path.Combine(DirectoryPath,"installing"))
        && new[]{Python,Model,Downloader,Deno,Path.Combine(MediaDirectory,"ffprobe.exe"),Path.Combine(MediaDirectory,"ffmpeg.exe")}.All(File.Exists);

    public AiRuntime(string root, string directory)
    {
        _root = root; DirectoryPath = Path.GetFullPath(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tools", "creation", "runtime-manifest.json")));
        _manifest = document.RootElement.Clone();
        Directory.CreateDirectory(DirectoryPath);
    }

    private async Task Download(string key, string path, IProgress<CreationProgress>? progress, CancellationToken token)
    {
        string label=key switch{"python"=>"Python 執行環境","model"=>"人聲分離模型","downloader"=>"影片下載工具","deno"=>"影片解析元件",_=>"必要檔案"};
        string hash = _manifest.GetProperty(key + "Sha256").GetString()!;
        if (File.Exists(path) && await Hash(path, token) == hash) return;
        string temporary = path + ".download";
        try
        {
            progress?.Report(new("setup","連線下載 · "+label));
            using var response = await Http.GetAsync(_manifest.GetProperty(key + "Url").GetString(), HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                byte[] buffer = new byte[1024 * 1024]; long received = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token); received += count;
                    long? total = response.Content.Headers.ContentLength;
                    progress?.Report(new("setup", "下載 · " + label, total > 0 ? 100.0 * received / total : null));
                }
            }
            progress?.Report(new("setup","驗證 · "+label));
            if (await Hash(temporary, token) != hash) throw new IOException("下載檔案驗證失敗，請重試。");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<string> Hash(string file, CancellationToken token)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }

    public async Task EnsureDownloadToolsAsync(IProgress<CreationProgress>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await Download("downloader", Downloader, progress, token);
            if (!File.Exists(Deno))
            {
                string archive = Path.Combine(DirectoryPath, "deno.zip"); await Download("deno", archive, progress, token);
                using var zip = ZipFile.OpenRead(archive);
                zip.GetEntry("deno.exe")!.ExtractToFile(Deno, true);
            }
            if (!new[]{"ffmpeg.exe","ffprobe.exe"}.All(name => File.Exists(Path.Combine(MediaDirectory,name))))
                throw new FileNotFoundException("找不到隨附的媒體工具，請重新解壓完整程式套件。");
        }
        finally { _gate.Release(); }
    }

    public async Task EnsureAsync(IProgress<CreationProgress>? progress, CancellationToken token)
    {
        await EnsureDownloadToolsAsync(progress, token);
        await _gate.WaitAsync(token);
        try
        {
            if (IsReady) return;
            if (!File.Exists(Path.Combine(DirectoryPath,"python.ready")))
            {
                string archive = Path.Combine(DirectoryPath, "python.tar.gz"); await Download("python", archive, progress, token);
                progress?.Report(new("setup", "準備 AI 執行環境"));
                await using var source = File.OpenRead(archive);
                await using var gzip = new GZipStream(source, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, DirectoryPath, overwriteFiles: true, cancellationToken: token);
                await File.WriteAllTextAsync(Path.Combine(DirectoryPath,"python.ready"),Version,token);
            }
            bool gpu = false;
            string nvidia = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (File.Exists(nvidia))
            {
                string driver = await ToolProcess.RunAsync(nvidia, ["--query-gpu=driver_version", "--format=csv,noheader"], token);
                gpu = int.TryParse(driver.Trim().Split('.')[0], out int major) && major >= 570;
            }
            progress?.Report(new("setup", "安裝本機 AI 元件"));
            string installing=Path.Combine(DirectoryPath,"installing");bool repair=File.Exists(installing);
            await File.WriteAllTextAsync(installing,Version,token);
            string help=await ToolProcess.RunAsync(Python,["-m","pip","install","--help"],token);
            string[] progressArgs=help.Contains("raw",StringComparison.Ordinal)?["--progress-bar","raw"]:[];
            string[] pip = ["-m", "pip", "install", "--disable-pip-version-check", "--no-warn-script-location", "--cache-dir", Path.Combine(DirectoryPath, "pip-cache"), .. progressArgs];
            string[] recovery=repair?["--force-reinstall"]:[];
            var installProgress=new AiInstallProgress(progress);
            await ToolProcess.RunAsync(Python, [.. pip, .. recovery, "torch==2.8.0", "torchaudio==2.8.0", "--index-url", gpu ? "https://download.pytorch.org/whl/cu128" : "https://download.pytorch.org/whl/cpu"], token,
                installProgress.Report);
            progress?.Report(new("setup","準備人聲分離套件"));
            await ToolProcess.RunAsync(Python, [.. pip, .. recovery, "-r", Path.Combine(_root, "tools", "creation", "requirements.txt")], token,
                installProgress.Report);
            await Download("model", Model, progress, token);
            progress?.Report(new("setup","驗證 AI 元件"));
            string ready = await ToolProcess.RunAsync(Python, [Adapter, "--check"], token);
            await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "ready-" + Version), ready, token);
            File.Delete(installing);
        }
        finally { _gate.Release(); }
    }
}
