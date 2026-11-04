using System.Threading.Channels;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Phone;

public sealed class PhoneCapture(PhoneAudio audio,string sourceName="手機麥克風") : IMicrophoneCapture
{
    private readonly Channel<(float[] Audio,int Rate,double End)> _packets=Channel.CreateBounded<(float[],int,double)>(new BoundedChannelOptions(16){SingleReader=true,SingleWriter=true});
    private Task? _worker;
    private bool _disposed;
    private int _failed;
    private readonly SemaphoreSlim _lifecycle=new(1,1);
    public event Action<PitchFrame,double>? Frame;
    public event Action<double>? Level;
    public event Action<Exception>? Failed;
    public Exception? Error { get; private set; }
    public async Task StartAsync(string deviceId,string model,bool detectPitch=true)
    {
        await _lifecycle.WaitAsync();
        try
        {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_worker!=null)throw new InvalidOperationException("麥克風已啟動。");
        if(!audio.Connected)throw new InvalidOperationException($"請先連接{sourceName}。");
        var detector=detectPitch?await Task.Run(()=>new PitchDetector(model)):null;
        if(!audio.Connected){detector?.Dispose();throw new IOException($"{sourceName}已中斷連線。");}
        audio.Received+=OnReceived;audio.ConnectionChanged+=OnConnection;
        _worker=Task.Run(()=>ProcessAsync(detector));
        }
        finally{_lifecycle.Release();}
    }
    private void OnReceived(float[] samples,int rate,double end)
    {
        if(_disposed||Volatile.Read(ref _failed)!=0)return;
        if(!_packets.Writer.TryWrite((samples,rate,end)))Fail(new IOException($"{sourceName}收音未能即時處理，請重新連線。"));
    }
    private void Fail(Exception error)
    {
        if(Interlocked.Exchange(ref _failed,1)!=0)return;
        Error=error;_packets.Writer.TryComplete(error);
        if(!_disposed)Failed?.Invoke(error);
    }
    private void OnConnection(bool connected){if(!connected)_packets.Writer.TryComplete();}
    private async Task ProcessAsync(PitchDetector? detector)
    {
        using(detector)
        try
        {
            var stream=detector==null?null:new PitchStream(detector);double origin=double.NaN,lastPacket=double.NaN;var pending=new List<float>();var resampler=new AudioResampler();
            await foreach(var packet in _packets.Reader.ReadAllAsync())
            {
                double duration=packet.Audio.Length/(double)packet.Rate;
                if(double.IsNaN(origin)||packet.End<=lastPacket||packet.End-lastPacket-duration>(audio.HasCaptureTimestamps?.008:.2))
                {
                    if(stream!=null&&!double.IsNaN(origin))foreach(var frame in stream.Push(pending.ToArray(),final:true))Frame?.Invoke(frame,origin+frame.Time);
                    origin=packet.End-duration;pending.Clear();resampler=new();if(detector!=null)stream=new PitchStream(detector);
                }
                lastPacket=packet.End;
                Level?.Invoke(Math.Sqrt(packet.Audio.Sum(v=>(double)v*v)/Math.Max(1,packet.Audio.Length)));
                if(stream==null)continue;
                pending.AddRange(resampler.Convert(packet.Audio,packet.Rate));if(pending.Count<PitchDetector.Hop)continue;
                foreach(var frame in stream.Push(pending.ToArray()))Frame?.Invoke(frame,origin+frame.Time);pending.Clear();
            }
            if(stream!=null&&!double.IsNaN(origin))foreach(var frame in stream.Push(pending.ToArray(),final:true))Frame?.Invoke(frame,origin+frame.Time);
        }
        catch(Exception error){Fail(error);}
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try{if(_disposed)return;_disposed=true;audio.Received-=OnReceived;audio.ConnectionChanged-=OnConnection;_packets.Writer.TryComplete();if(_worker!=null)await _worker;}
        finally{_lifecycle.Release();}
    }
}
