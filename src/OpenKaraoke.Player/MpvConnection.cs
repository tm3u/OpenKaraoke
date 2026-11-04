using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace OpenKaraoke.Player;

public sealed class MpvConnection : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _name;
    private NamedPipeClientStream? _connectedPipe;
    private Task? _reader;
    private long _requestId;

    public event Action<JsonElement>? EventReceived;
    public event Action<Exception>? Disconnected;

    public MpvConnection(string name) => _name = name;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connectedPipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _connectedPipe.ConnectAsync(8000, cancellationToken);
        _reader = ReadAsync();
    }

    public async Task<JsonElement> CommandAsync(params object[] command)
    {
        var pipe = _connectedPipe ?? throw new InvalidOperationException("播放器尚未連線");
        long id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            byte[] packet = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { command, request_id = id }) + "\n");
            await _writeLock.WaitAsync(_lifetime.Token);
            try { await pipe.WriteAsync(packet, _lifetime.Token); }
            finally { _writeLock.Release(); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task<JsonElement> GetAsync(string property) => CommandAsync("get_property", property);
    public Task<JsonElement> SetAsync(string property, object value) => CommandAsync("set_property", property, value);

    private async Task ReadAsync()
    {
        try
        {
            using var reader = new StreamReader(_connectedPipe!, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync(_lifetime.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (!message.TryGetProperty("request_id", out var requestId))
                {
                    EventReceived?.Invoke(message.Clone());
                    continue;
                }
                if (!_pending.TryGetValue(requestId.GetInt64(), out var completion)) continue;
                string? error = message.GetProperty("error").GetString();
                if (error != "success") completion.TrySetException(new InvalidOperationException($"mpv：{error}"));
                else completion.TrySetResult(message.TryGetProperty("data", out var data) ? data.Clone() : default);
            }
            if (!_lifetime.IsCancellationRequested) throw new IOException("mpv 連線已中斷");
        }
        catch (Exception error)
        {
            foreach (var item in _pending.Values) item.TrySetException(error);
            if (!_lifetime.IsCancellationRequested) Disconnected?.Invoke(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _connectedPipe?.Dispose();
        if (_reader != null) await _reader;
        _lifetime.Dispose();
    }
}
