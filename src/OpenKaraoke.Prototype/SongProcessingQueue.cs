using System.IO;
using System.Text.Json;
using OpenKaraoke.Library;

namespace OpenKaraoke.Prototype;

public sealed record SongWorkItem(string Id, string Kind, string Title, string Artist, string Source, YouTubeVideo? Video = null,
    string Status = "queued", string Stage = "", double? Percent = null, string Error = "", SongLyrics? Lyrics = null);
public delegate Task<Song> SongProcessor(SongWorkItem item, string folder, IProgress<CreationProgress> progress, CancellationToken cancellation);

public sealed class SongProcessingQueue : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<SongWorkItem> _items = [];
    private readonly SongProcessor _process;
    private readonly LibraryStore? _store;
    private readonly Func<bool>? _aiReady;
    private readonly Func<IProgress<CreationProgress>,CancellationToken,Task>? _prepareAi;
    private readonly string _folder, _statePath;
    private Task? _worker;
    private CancellationTokenSource? _activeCancellation;
    private string? _activeId;
    private bool _disposing;
    public event Action? Changed;
    public event Action<Song>? SongAdded;
    public string RecoveryMessage { get; private set; } = "";
    public SongWorkItem[] Items
    {
        get
        {
            lock(_gate)
            {
                bool preparing=_items.Any(i=>i.Kind=="setup");
                return _items.Select(i=>preparing&&i.Kind=="create"&&i.Status=="queued"?i with{Stage="等待必要 AI 元件"}:i).ToArray();
            }
        }
    }

    public SongProcessingQueue(string folder, SongProcessor process, LibraryStore? store = null,
        Func<bool>? aiReady = null,Func<IProgress<CreationProgress>,CancellationToken,Task>? prepareAi = null)
    {
        if((aiReady==null)!=(prepareAi==null))throw new ArgumentException("AI 就緒檢查與準備程序必須一起提供。");
        _aiReady=aiReady;_prepareAi=prepareAi;
        _folder = Path.GetFullPath(folder); _statePath = Path.Combine(_folder, "jobs.json"); _process = process; _store=store;
        Directory.CreateDirectory(_folder);
        if (!File.Exists(_statePath)) return;
        bool damaged = false; var ids = new HashSet<string>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_statePath));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("處理清單資料格式不正確。");
            foreach (var element in document.RootElement.EnumerateArray())
            {
                try
                {
                    var item = element.Deserialize<SongWorkItem>();
                    if (item == null || !Guid.TryParseExact(item.Id, "N", out _) || item.Kind is not ("create" or "import" or "setup")
                        || (item.Kind!="setup"&&string.IsNullOrWhiteSpace(item.Source))
                        || item.Status is not ("queued" or "active" or "error" or "cancelled")
                        || item.Percent is < 0 or > 100)
                        throw new InvalidDataException("處理項目格式不正確。");
                    if(item.Kind=="setup")
                    {
                        if(_items.Any(i=>i.Kind=="setup")||item.Source!=""||item.Status=="cancelled")throw new InvalidDataException("AI 準備項目格式不正確。");
                        item=item with{Title="必要 AI 元件",Artist="",Video=null};
                    }
                    else { SongPackageService.ValidateNames(item.Title, item.Artist); item.Lyrics?.Validate(); }
                    if (item.Kind == "create")
                    {
                        if (item.Video == null || string.IsNullOrWhiteSpace(item.Video.Url) || !double.IsFinite(item.Video.Duration) || item.Video.Duration <= 0)
                            throw new InvalidDataException("缺少有效影片資料。");
                        SongCreationService.NormalizeYouTubeUrl(item.Video.Url);
                    }
                    else if(item.Kind=="import")
                    {
                        if(!Path.IsPathFullyQualified(item.Source))throw new InvalidDataException("匯入來源路徑無效。");
                        _=Path.GetFullPath(item.Source);
                    }
                    if (!ids.Add(item.Id)) throw new InvalidDataException("處理項目編號重複。");
                    _items.Add(item.Status == "active" ? item with { Status = "queued", Stage = "", Percent = null, Error = "" }
                        : item with { Stage = item.Stage ?? "", Error = item.Error ?? "" });
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { damaged = true; }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { damaged = true; }
        bool dependencyChanged=EnsureAiDependency();
        if (!damaged) {if(dependencyChanged)Save();return;}
        string backup = Path.Combine(_folder, "jobs-recovery-" + Guid.NewGuid().ToString("N") + ".json");
        File.Copy(_statePath, backup);
        Save();
        RecoveryMessage = "處理清單有異常，已保留有效項目；原始資料備份於 " + backup;
    }
    public void Start() { lock (_gate) StartWorker(); }
    public string AddCreate(YouTubeVideo video, string title, string artist, SongLyrics? lyrics = null)
    {
        video=video with{Url=SongCreationService.NormalizeYouTubeUrl(video.Url)};
        return Add(new(Guid.NewGuid().ToString("N"), "create", title.Trim(), artist.Trim(), video.Url, video, Lyrics: lyrics));
    }
    public string AddImport(string file, string title, string artist, SongLyrics? lyrics = null) => Add(new(Guid.NewGuid().ToString("N"), "import", title.Trim(), artist.Trim(), Path.GetFullPath(file), Lyrics: lyrics));
    private string Add(SongWorkItem item)
    {
        SongPackageService.ValidateNames(item.Title, item.Artist);
        item.Lyrics?.Validate(item.Video?.Duration);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            EnsureAvailable(item);
            var original=_items.ToArray();
            _items.Add(item);
            try { EnsureAiDependency();Save(); } catch { _items.Clear();_items.AddRange(original);throw; }
            StartWorker();
        }
        Changed?.Invoke(); return item.Id;
    }
    public void Cancel(string id)
    {
        lock (_gate)
        {
            int at = _items.FindIndex(i => i.Id == id); if (at < 0 || _items[at].Kind=="setup" || _items[at].Status is not ("active" or "queued")) return;
            if (_activeId == id) _activeCancellation?.Cancel();
            else
            {
                var previous = _items[at];
                _items[at] = previous with { Status = "cancelled", Stage = "已取消", Percent = null };
                try { Save(); }
                catch { _items[at] = previous; throw; }
            }
        }
        Changed?.Invoke();
    }
    public void Retry(string id)
    {
        lock (_gate)
        {
            int at = _items.FindIndex(i => i.Id == id && i.Status is "error" or "cancelled"); if (at < 0) return;
            var item = _items[at] with { Status = "queued", Stage = "", Percent = null, Error = "" };
            if(item.Kind!="setup")EnsureAvailable(item);
            var original=_items.ToArray();
            try
            {
                if(item.Kind=="setup")_items[at]=item;
                else{_items.RemoveAt(at);_items.Add(item);}
                EnsureAiDependency();Save();
            }
            catch{_items.Clear();_items.AddRange(original);throw;}
            StartWorker();
        }
        Changed?.Invoke();
    }
    private bool EnsureAiDependency()
    {
        if(_aiReady==null)return false;
        if(_aiReady())return _items.RemoveAll(i=>i.Kind=="setup"&&i.Status!="active")>0;
        int at=_items.FindIndex(i=>i.Kind=="create"&&i.Status=="queued");if(at<0)return false;
        int setup=_items.FindIndex(i=>i.Kind=="setup");
        if(setup>=0)
        {
            if(setup<=at)return false;
            var dependency=_items[setup];_items.RemoveAt(setup);_items.Insert(at,dependency);return true;
        }
        _items.Insert(at,new(Guid.NewGuid().ToString("N"),"setup","必要 AI 元件","",""));return true;
    }
    private void EnsureAvailable(SongWorkItem item)
    {
        if(_items.Any(i=>i.Id!=item.Id&&i.Status is "queued" or "active"&&i.Title==item.Title&&i.Artist==item.Artist)
            ||(_store?.ContainsSong(item.Title,item.Artist)==true&&_store.FindById(item.Id)==null))
            throw new InvalidOperationException("歌曲已加入或正在處理。");
    }
    private void StartWorker()
    {
        if (_disposing || _worker is { IsCompleted: false } || !_items.Any(i => i.Status == "queued")) return;
        _worker = Task.Run(RunAsync);
    }
    private void Save()
    {
        string temporary = _statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_items)); File.Move(temporary, _statePath, true);
    }
    private void Report(string id, CreationProgress progress)
    {
        lock (_gate)
        {
            int at = _items.FindIndex(i => i.Id == id && i.Status == "active"); if (at < 0) return;
            _items[at] = _items[at] with { Stage = progress.Detail, Percent = progress.Percent.HasValue ? Math.Clamp(progress.Percent.Value, 0, 100) : null };
        }
        Changed?.Invoke();
    }
    private sealed class ProgressSink(Action<CreationProgress> report) : IProgress<CreationProgress>
    { public void Report(CreationProgress value) => report(value); }

    private static SongWorkItem SaveFailed(SongWorkItem item, Exception error) => item with
    {
        Status = "error", Stage = "無法儲存處理清單，請重試", Error = error.Message, Percent = null
    };

    private async Task RunAsync()
    {
        while (true)
        {
            SongWorkItem item;
            CancellationTokenSource? cancellation = null;
            lock (_gate)
            {
                if(_disposing){_worker=null;return;}
                EnsureAiDependency();
                var next = _items.FirstOrDefault(i => i.Status == "queued");
                if (next == null) { _worker = null; return; }
                if(next.Kind=="create"&&_items.Any(i=>i.Kind=="setup"&&i.Status=="error")){_worker=null;return;}
                item = next with { Status = "active", Stage = next.Kind=="setup"?"準備必要 AI 元件":next.Kind == "create" ? "準備影片" : "驗證歌曲檔", Error = "" };
                int at = _items.FindIndex(i => i.Id == item.Id);
                _items[at] = item;
                try
                {
                    Save();
                    _activeId = item.Id;
                    _activeCancellation = cancellation = new();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _items[at] = SaveFailed(item, error);
                    _worker = null;
                }
            }
            Changed?.Invoke();
            if (cancellation == null) return;

            Song? added = null;
            Exception? processingError = null;
            string work = Path.Combine(_folder, "work", item.Id);
            try
            {
                var progress=new ProgressSink(p=>Report(item.Id,p));
                if(item.Kind=="setup")
                {
                    await (_prepareAi??throw new InvalidOperationException("未設定 AI 元件準備程序。"))(progress,cancellation.Token);
                    if(_aiReady?.Invoke()!=true)throw new IOException("必要 AI 元件尚未就緒，請重試。");
                }
                else
                {
                    Directory.CreateDirectory(work);
                    added = await _process(item, work, progress, cancellation.Token);
                }
            }
            catch (Exception error) { processingError = error; }
            finally
            {
                string workRoot = Path.GetFullPath(Path.Combine(_folder, "work")) + Path.DirectorySeparatorChar;
                if (Path.GetFullPath(work).StartsWith(workRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(work))
                    try { Directory.Delete(work, true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }

            bool saved;
            lock (_gate)
            {
                _activeId = null;
                _activeCancellation = null;
                cancellation.Dispose();
                int at = _items.FindIndex(i => i.Id == item.Id);
                var result = processingError switch
                {
                    null => null,
                    OperationCanceledException when _disposing => item with { Status = "queued", Stage = "", Percent = null },
                    OperationCanceledException when item.Kind != "setup" => item with { Status = "cancelled", Stage = "已取消", Percent = null },
                    _ => item with
                    {
                        Status = "error", Percent = null, Error = processingError.Message,
                        Stage = item.Kind == "setup" ? "AI 元件準備失敗，請重試" : item.Kind == "create" ? "製作失敗" : "匯入失敗"
                    }
                };
                if (result == null) _items.RemoveAt(at);
                else _items[at] = result;
                try { Save(); saved = true; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (result == null) _items.Insert(at, SaveFailed(item, error));
                    else _items[at] = SaveFailed(item, error);
                    _worker = null;
                    saved = false;
                }
            }
            Changed?.Invoke();
            if (!saved) return;
            if (added != null) SongAdded?.Invoke(added);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate) { _disposing = true; _activeCancellation?.Cancel(); worker = _worker; }
        if (worker != null) await worker;
    }
}

public sealed class SongProcessingService(LibraryStore store, SongCreationService creator, SongPackageService packages, ScoringReferenceService references)
{
    public async Task<Song> ProcessAsync(SongWorkItem item, string folder, IProgress<CreationProgress> progress, CancellationToken cancellation)
    {
        if (store.FindById(item.Id) is {} existing) return existing;
        string source = item.Source;
        if (item.Kind == "create")
        {
            var result = await creator.CreateAsync(item.Video ?? throw new InvalidDataException("缺少影片資料。"), item.Title, item.Artist, folder, progress, cancellation);
            source = result.Package;
        }
        progress.Report(new("reference", "讀取評分基準"));
        var info = await packages.InspectAsync(source, cancellation);
        var song = new Song(item.Id, item.Title, item.Artist, source, info.Duration, 1, 2);
        if (await references.LoadAsync(song, cancellation) == null)
            await references.BuildAsync(song, progress, cancellation);
        cancellation.ThrowIfCancellationRequested();
        progress.Report(new("import", "加入歌曲庫"));
        return await packages.ImportAsync(source, item.Title, item.Artist, cancellation, item.Id, item.Lyrics);
    }
}
