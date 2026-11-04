using System.Diagnostics;
using System.Text.Json;

namespace OpenKaraoke.Player;

public sealed record AudioTrack(int Id, string Title)
{
    public string Label => $"#{Id}  {Title}";
}

public sealed record PlaybackState
{
    public bool Loaded { get; init; }
    public bool Paused { get; init; } = true;
    public bool Ended { get; init; }
    public bool HasVideo { get; init; }
    public string LyricsMode { get; init; } = "";
    public bool SubtitlesEnabled { get; init; } = true;
    public int SubtitleFontPercent { get; init; } = 100;
    public string File { get; init; } = "";
    public AudioTrack[] Tracks { get; init; } = [];
    public int TrackId { get; init; }
    public int Key { get; init; }
    public double Pitch { get; init; } = 1;
    public double Speed { get; init; } = 1;
    public double Position { get; init; }
    public double Duration { get; init; }
    public double Volume { get; init; } = 100;
    public string Error { get; init; } = "";
}

public sealed class MpvPlayer : IAsyncDisposable
{
    public const string LyricsTrackPrefix = "OpenKaraoke Lyrics:";
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operations = new(1);
    private readonly Action<string> _log;
    private PlaybackState _state = new();
    private MpvConnection? _connection;
    private Process? _process;
    private TaskCompletionSource? _fileLoaded;
    private bool _disposing;
    private bool _subtitlesEnabled = true;
    private int _subtitleFontPercent = 100;

    public string PipeName { get; } = $"openkaraoke-prototype-{Environment.ProcessId}-{Guid.NewGuid():N}";
    public int ProcessId => _process?.Id ?? 0;
    public PlaybackState State { get { lock (_stateLock) return _state; } }
    public event Action<PlaybackState>? Changed;
    public event Action<string>? ShortcutRequested;

    public MpvPlayer(Action<string> log) => _log = log;

    private void Update(Func<PlaybackState, PlaybackState> update)
    {
        PlaybackState state;
        lock (_stateLock) state = _state = update(_state);
        Changed?.Invoke(state);
    }

    public async Task StartAsync(string executable, nint window, bool headless = false, bool audioOnly = false)
    {
        await _operations.WaitAsync();
        try
        {
            if (_connection != null) return;
            if (!System.IO.File.Exists(executable)) throw new FileNotFoundException("找不到隨附的 mpv 播放器", executable);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string argument in new[] {
                "--no-config", "--idle=yes", "--keep-open=yes", headless || audioOnly ? "--force-window=no" : "--force-window=yes", "--terminal=no",
                "--input-default-bindings=no", "--input-vo-keyboard=yes", "--input-cursor=no", "--osc=no",
                "--input-conf=" + System.IO.Path.Combine(AppContext.BaseDirectory, "mpv-input.conf"),
                "--osd-level=0", "--volume=100", headless || audioOnly ? "--vo=null" : "--vo=gpu-next", "--gpu-api=d3d11", "--hwdec=auto-safe",
                "--audio-file-auto=no", "--sub-auto=no",
                $"--wid={unchecked((uint)window.ToInt64())}", $"--input-ipc-server=\\\\.\\pipe\\{PipeName}" })
                start.ArgumentList.Add(argument);
            if (headless) start.ArgumentList.Add("--ao=null");
            _process = Process.Start(start) ?? throw new InvalidOperationException("無法啟動 mpv");
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _log(e.Data); };
            _process.BeginErrorReadLine();
            _connection = new MpvConnection(PipeName);
            _connection.EventReceived += OnEvent;
            _connection.Disconnected += error =>
            {
                if (_disposing) return;
                _fileLoaded?.TrySetException(error);
                Update(s => s with { Loaded = false, Error = error.Message });
            };
            try
            {
                await _connection.ConnectAsync();
                int index = 0;
                foreach (string property in new[] { "time-pos", "duration", "pause", "aid", "pitch", "speed", "volume", "eof-reached" })
                    await _connection.CommandAsync("observe_property", ++index, property);
                await ApplySubtitlesAsync(_connection, _subtitlesEnabled, _subtitleFontPercent);
                _log("mpv 已連線：" + (await _connection.GetAsync("mpv-version")).GetString());
            }
            catch
            {
                await _connection.DisposeAsync(); _connection = null;
                if (!_process.HasExited) _process.Kill(true);
                _process.Dispose(); _process = null;
                throw;
            }
        }
        finally { _operations.Release(); }
    }

    public async Task<Dictionary<string, string>> ReadMetadataAsync()
    {
        var value = await RequireConnection().GetAsync("metadata");
        return value.ValueKind == System.Text.Json.JsonValueKind.Object
            ? value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    public async Task LoadAsync(string file, bool play = true)
    {
        if (!System.IO.File.Exists(file)) throw new FileNotFoundException("歌曲檔案不存在", file);
        await _operations.WaitAsync();
        try
        {
            var connection = RequireConnection();
            Update(s => new PlaybackState { File = file, Volume = s.Volume, SubtitlesEnabled = _subtitlesEnabled, SubtitleFontPercent = _subtitleFontPercent });
            _fileLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await connection.SetAsync("pause", true);
            await connection.SetAsync("pitch", 1.0);
            await connection.CommandAsync("loadfile", System.IO.Path.GetFullPath(file), "replace");
            await _fileLoaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var trackList = (await connection.GetAsync("track-list")).EnumerateArray().ToArray();
            var tracks = trackList
                .Where(t => t.GetProperty("type").GetString() == "audio")
                .Select(t => new AudioTrack(t.GetProperty("id").GetInt32(),
                    t.TryGetProperty("title", out var title) ? title.GetString() ?? "未命名音軌"
                    : t.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("handler_name", out var handler)
                        ? handler.GetString() ?? "未命名音軌" : "未命名音軌"))
                .ToArray();
            int selected = tracks.FirstOrDefault(t => IsInstrumental(t.Title))?.Id
                ?? tracks.FirstOrDefault()?.Id ?? 0;
            if (selected != 0) await connection.SetAsync("aid", selected);
            var lyrics = trackList.FirstOrDefault(t => t.GetProperty("type").GetString() == "sub"
                && t.TryGetProperty("title", out var title) && title.GetString() is {} name
                && (name == LyricsTrackPrefix + "karaoke" || name == LyricsTrackPrefix + "scroll"));
            string lyricsMode = "";
            if (lyrics.ValueKind == JsonValueKind.Object)
            {
                await connection.SetAsync("sid", lyrics.GetProperty("id").GetInt32());
                lyricsMode = lyrics.GetProperty("title").GetString()![LyricsTrackPrefix.Length..];
            }
            else await connection.SetAsync("sid", "no");
            await ApplySubtitlesAsync(connection, _subtitlesEnabled, _subtitleFontPercent);
            double duration = (await connection.GetAsync("duration")).GetDouble();
            await connection.SetAsync("volume", State.Volume);
            await connection.SetAsync("pause", !play);
            bool video = (await connection.GetAsync("track-list")).EnumerateArray().Any(t => t.GetProperty("type").GetString() == "video");
            Update(s => s with { Loaded = true, HasVideo = video, LyricsMode = lyricsMode, Tracks = tracks, TrackId = selected, Duration = duration, Paused = !play, Ended = false, Error = "" });
            _log($"已載入 {file}；{tracks.Length} 條音軌");
        }
        catch (Exception error)
        {
            Update(s => s with { Loaded = false, Error = error.Message });
            throw;
        }
        finally { _fileLoaded = null; _operations.Release(); }
    }

    public static bool IsInstrumental(string name) => new[] { "instrumental", "karaoke", "no_vocals", "伴奏" }
        .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    public Task SelectTrackAsync(int id) => OperateAsync(async c =>
    {
        if (!State.Tracks.Any(t => t.Id == id)) throw new InvalidOperationException("找不到指定音軌");
        int key = State.Key;
        await c.SetAsync("aid", id);
        var deadline = Stopwatch.StartNew();
        int actual = 0;
        while (deadline.Elapsed < TimeSpan.FromSeconds(4))
        {
            var value = await c.GetAsync("aid");
            if (value.ValueKind == JsonValueKind.Number) actual = value.GetInt32();
            if (actual == id) break;
            await Task.Delay(30);
        }
        if (actual != id) throw new InvalidOperationException("播放器沒有切換到指定音軌");
        await ApplyKeyAsync(c, key);
        Update(s => s with { TrackId = actual, Error = "" });
    });

    public Task ChangeKeyAsync(int change) => OperateAsync(c => ApplyKeyAsync(c, Math.Clamp(State.Key + change, -6, 6)));
    public Task ResetKeyAsync() => OperateAsync(c => ApplyKeyAsync(c, 0));

    private async Task ApplyKeyAsync(MpvConnection connection, int key)
    {
        double expected = Math.Pow(2, key / 12.0);
        await connection.SetAsync("pitch", expected);
        double actual = (await connection.GetAsync("pitch")).GetDouble();
        double speed = (await connection.GetAsync("speed")).GetDouble();
        if (Math.Abs(actual - expected) > 0.001 || Math.Abs(speed - 1) > 0.001)
            throw new InvalidOperationException("升降調或正常播放速度未確認，請檢查播放器版本");
        Update(s => s with { Key = key, Pitch = actual, Speed = speed, Error = "" });
    }

    public Task TogglePauseAsync() => OperateAsync(async c =>
    {
        if(State.Ended)return;
        await c.SetAsync("pause",!State.Paused);
        bool paused=(await c.GetAsync("pause")).GetBoolean();
        Update(s=>s with{Paused=paused});
    });

    public Task SeekAsync(double seconds) => OperateAsync(async c =>
    {
        await c.CommandAsync("seek", Math.Clamp(seconds, 0, State.Duration), "absolute+exact");
        Update(s => s with { Ended = false });
    });

    public Task SetVolumeAsync(double value) => OperateAsync(async c =>
    {
        await c.SetAsync("volume", Math.Clamp(value, 0, 100));
        double actual = (await c.GetAsync("volume")).GetDouble();
        Update(s => s with { Volume = actual });
    });

    public async Task SetSubtitlesAsync(bool enabled, int fontPercent)
    {
        fontPercent = Math.Clamp(fontPercent, 75, 125);
        await _operations.WaitAsync();
        try
        {
            if (_connection != null) await ApplySubtitlesAsync(_connection, enabled, fontPercent);
            _subtitlesEnabled = enabled; _subtitleFontPercent = fontPercent;
            Update(s => s with { SubtitlesEnabled = enabled, SubtitleFontPercent = fontPercent });
        }
        finally { _operations.Release(); }
    }

    private static async Task ApplySubtitlesAsync(MpvConnection connection, bool enabled, int fontPercent)
    {
        await connection.SetAsync("sub-ass-override", "scale");
        await connection.SetAsync("sub-scale-signs", true);
        await connection.SetAsync("sub-scale", fontPercent / 100d);
        await connection.SetAsync("sub-visibility", enabled);
        if ((await connection.GetAsync("sub-visibility")).GetBoolean() != enabled
            || Math.Abs((await connection.GetAsync("sub-scale")).GetDouble() - fontPercent / 100d) > .001)
            throw new InvalidOperationException("歌詞顯示設定未正確套用。");
    }

    public Task SetPausedAsync(bool paused) => OperateAsync(async c =>
    {
        await c.SetAsync("pause", paused);
        Update(s => s with { Paused = paused });
    });

    public Task CaptureFrameAsync(string path) => OperateAsync(async c =>
        { await c.CommandAsync("screenshot-to-file", path, "window"); });

    public Task StopAsync() => OperateAsync(async c =>
    {
        await c.SetAsync("pause", true);
        await c.CommandAsync("seek", 0, "absolute+exact");
        Update(s => s with { Paused = true, Ended = false, Position = 0 });
    });

    public async Task UnloadAsync()
    {
        await _operations.WaitAsync();
        try { if (_connection != null) await _connection.CommandAsync("stop"); }
        finally
        {
            Update(s => new PlaybackState { Volume = s.Volume, SubtitlesEnabled = _subtitlesEnabled, SubtitleFontPercent = _subtitleFontPercent });
            _operations.Release();
        }
    }

    public Task SetCaptionAsync(string title, string artist, double width, double height, double scale = 1) => OperateAsync(async c =>
    {
        static string Ass(string text) => text.Replace("\\", "＼").Replace("{", "｛").Replace("}", "｝")
            .Replace("\r", " ").Replace("\n", " ");
        string N(double value)=>value.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture);
        await c.CommandAsync("osd-overlay", 1, "ass-events",
            "{\\an7\\pos("+N(18*scale)+","+N(16*scale)+")\\fnMicrosoft JhengHei\\b1\\fs"+N(20*scale)+"\\bord1\\shad1\\1c&HFFFFFF&}" + Ass(title)
            + "\\N{\\fs"+N(13*scale)+"}" + Ass(artist), Math.Max(1,(int)width), Math.Max(1,(int)height));
    });

    private async Task OperateAsync(Func<MpvConnection, Task> operation)
    {
        await _operations.WaitAsync();
        try
        {
            if (!State.Loaded) return;
            await operation(RequireConnection());
        }
        finally { _operations.Release(); }
    }

    private MpvConnection RequireConnection() => _connection ?? throw new InvalidOperationException("播放器尚未啟動");

    private void OnEvent(JsonElement message)
    {
        if (!message.TryGetProperty("event", out var eventName)) return;
        switch (eventName.GetString())
        {
            case "client-message":
                if (message.TryGetProperty("args", out var arguments) && arguments.GetArrayLength() > 0)
                {
                    string? action = arguments[0].GetString();
                    const string shortcutPrefix = "openkaraoke:";
                    if (action?.StartsWith(shortcutPrefix, StringComparison.Ordinal) == true)
                        ShortcutRequested?.Invoke(action[shortcutPrefix.Length..]);
                }
                break;
            case "file-loaded": _fileLoaded?.TrySetResult(); break;
            case "end-file":
                if (message.TryGetProperty("reason", out var reason) && reason.GetString() == "error")
                {
                    string description = message.TryGetProperty("file_error", out var detail) ? detail.GetString() ?? "未知錯誤" : "未知錯誤";
                    var error = new IOException("無法解碼影片：" + description);
                    _fileLoaded?.TrySetException(error);
                    Update(s => s with { Loaded = false, Error = error.Message });
                }
                break;
            case "property-change":
                if (!message.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null) break;
                string? name = message.GetProperty("name").GetString();
                Update(s => name switch
                {
                    "time-pos" => s with { Position = data.GetDouble() },
                    "eof-reached" => s with { Ended = data.GetBoolean() },
                    "duration" => s with { Duration = data.GetDouble() },
                    "pause" => s with { Paused = data.GetBoolean() },
                    "aid" when data.ValueKind == JsonValueKind.Number => s with { TrackId = data.GetInt32() },
                    "pitch" => s with { Pitch = data.GetDouble(), Key = (int)Math.Round(12 * Math.Log2(data.GetDouble())) },
                    "speed" => s with { Speed = data.GetDouble() },
                    "volume" => s with { Volume = data.GetDouble() },
                    _ => s
                });
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        if (_connection != null)
        {
            try { await _connection.CommandAsync("quit"); } catch (Exception) { }
            await _connection.DisposeAsync();
        }
        if (_process == null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        _process.Dispose();
    }
}
