using OpenKaraoke.Scoring;

namespace OpenKaraoke.Phone;

public sealed class PhoneLevelCapture(PhoneMixer mixer,string sourceName="手機麥克風",bool waitForConnection=false) : IMicrophoneCapture
{
    private readonly HashSet<PhoneAudio> _inputs=[];
    private bool _disposed;
    public event Action<PitchFrame,double>? Frame{add{}remove{}}
    public event Action<double>? Level;
    public event Action<Exception>? Failed;
    public Task StartAsync(string deviceId,string model,bool detectPitch=true)
    {
        if(mixer.ConnectedCount==0&&!waitForConnection)throw new IOException($"請先連接{sourceName}。");mixer.Changed+=Refresh;Refresh();return Task.CompletedTask;
    }
    private void Refresh()
    {
        if(_disposed)return;
        lock(_inputs)foreach(var channel in mixer.Channels)if(_inputs.Add(channel.Audio))channel.Audio.Received+=Received;
        if(mixer.ConnectedCount==0){if(waitForConnection)Level?.Invoke(0);else Failed?.Invoke(new IOException($"{sourceName}已中斷連線。"));}
    }
    private void Received(float[] data,int rate,double time)=>Level?.Invoke(mixer.Channels.Select(c=>c.Audio.Level).DefaultIfEmpty(0).Max());
    public ValueTask DisposeAsync(){_disposed=true;mixer.Changed-=Refresh;lock(_inputs){foreach(var input in _inputs)input.Received-=Received;_inputs.Clear();}return ValueTask.CompletedTask;}
}
