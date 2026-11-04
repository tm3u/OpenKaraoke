using OpenKaraoke.Scoring;

namespace OpenKaraoke.Phone;

public sealed class LocalMicrophone : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly bool _playOutput;
    private readonly PhoneChannel _channel;
    private MicrophoneCapture? _capture;
    private Action<float[],int,double>? _onSamples;
    private Action<Exception>? _onFailed;
    private bool _ready,_disposed;
    private int _version,_failed;
    public LocalMicrophone(bool playOutput=true)
    {
        _playOutput = playOutput;
        _channel = Mixer.TryConnect("pc")!;
        _channel.Audio.SmoothDiscontinuities = false;
        Mixer.Disconnect(_channel);
    }
    public PhoneMixer Mixer {get;}=new();
    public PhoneAudio Audio=>_channel.Audio;
    public string DeviceId {get;private set;}="";
    public Exception? Error {get;private set;}
    public bool Running=>_ready&&Volatile.Read(ref _failed)==0&&Audio.Connected;
    public bool IsChanging {get;private set;}
    public bool TargetEnabled {get;private set;}
    public event Action? Changed;
    public event Action<Exception>? Failed;
    public async Task StartAsync(string deviceId,int volume)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(Running&&DeviceId==deviceId){Audio.Configure(false,volume);return;}
            IsChanging=true;TargetEnabled=true;Changed?.Invoke();await StopCoreAsync();Error=null;Interlocked.Exchange(ref _failed,0);
            int version=++_version;var firstPacket=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Mixer.TryConnect("pc");Audio.Configure(false,volume);var capture=new MicrophoneCapture();_capture=capture;
            _onSamples=(samples,rate,end)=>
            {
                if(version!=Volatile.Read(ref _version))return;
                if(Audio.InputRate!=rate)Audio.SetFormat(rate);
                if(!Audio.AcceptSamples(samples,end)){var error=new IOException("電腦麥克風音訊格式無效。");firstPacket.TrySetException(error);Fail(error,version);return;}
                firstPacket.TrySetResult();
            };
            _onFailed=error=>{if(version!=Volatile.Read(ref _version))return;firstPacket.TrySetException(error);Fail(error,version);};
            capture.Samples+=_onSamples;capture.Failed+=_onFailed;Mixer.OutputFailed+=_onFailed;
            if(_playOutput)Mixer.StartOutput();
            await capture.StartAsync(deviceId,"",detectPitch:false);
            await firstPacket.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if(Volatile.Read(ref _failed)!=0||_playOutput&&!Mixer.OutputActive)throw Error??new IOException("無法播放電腦麥克風收音。");
            DeviceId=deviceId;_ready=true;
        }
        catch(Exception error){Error=error;await StopCoreAsync();throw;}
        finally{IsChanging=false;_gate.Release();Changed?.Invoke();}
    }
    private void Fail(Exception error,int version)
    {
        if(version!=Volatile.Read(ref _version)||Interlocked.Exchange(ref _failed,1)!=0)return;
        Error=error;_ready=false;Failed?.Invoke(error);_ = StopAfterFailureAsync(version);
    }
    private async Task StopAfterFailureAsync(int version)
    {
        await _gate.WaitAsync();
        try{if(version==Volatile.Read(ref _version))await StopCoreAsync();}
        catch(Exception error){Error=error;}
        finally{_gate.Release();Changed?.Invoke();}
    }
    private async Task StopCoreAsync()
    {
        Interlocked.Increment(ref _version);_ready=false;DeviceId="";
        var capture=_capture;_capture=null;
        if(_onFailed!=null)Mixer.OutputFailed-=_onFailed;
        if(capture!=null){capture.Samples-=_onSamples;capture.Failed-=_onFailed;}
        Mixer.DisconnectAll();if(capture!=null)await capture.DisposeAsync();
        _onSamples=null;_onFailed=null;
    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try{IsChanging=true;TargetEnabled=false;Changed?.Invoke();await StopCoreAsync();}
        finally{IsChanging=false;_gate.Release();Changed?.Invoke();}
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try{if(_disposed)return;_disposed=true;await StopCoreAsync();Mixer.Dispose();}
        finally{_gate.Release();}
    }
}
