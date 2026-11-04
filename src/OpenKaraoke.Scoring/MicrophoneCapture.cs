using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OpenKaraoke.Scoring;

public sealed record MicrophoneDevice(string Id, string Name)
{
    public override string ToString() => Name;
}
public sealed class MicrophoneCapture : IMicrophoneCapture
{
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private Task? _worker;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<(float[] Audio,int Rate,double End)> _packets = Channel.CreateBounded<(float[],int,double)>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
    private bool _disposing;
    private bool _disposed;
    private bool _detectPitch;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    public event Action<PitchFrame, double>? Frame;
    public event Action<double>? Level;
    public event Action<Exception>? Failed;
    public event Action<float[],int,double>? Samples;
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public static MicrophoneDevice[] Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<MicrophoneDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            using (device) result.Add(new(device.ID, device.FriendlyName));
        return result.ToArray();
    }

    public async Task StartAsync(string deviceId, string model, bool detectPitch = true)
    {
        await _lifecycle.WaitAsync();
        try
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capture != null) throw new InvalidOperationException("麥克風已啟動。");
        await Task.Run(() =>
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = deviceId == "default" ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : enumerator.GetDevice(deviceId);
            _detectPitch=detectPitch;_capture=CreateCapture(10);
            PitchDetector? detector;
            try{detector=detectPitch?new PitchDetector(model):null;}
            catch{_capture.Dispose();_capture=null;_packets.Writer.TryComplete();_stopped.TrySetResult();throw;}
            if(detectPitch)_worker=Task.Run(()=>ProcessAsync(detector!));
            try { _capture.StartRecording(); }
            catch
            {
                var failed=_capture;_capture=null;failed.Dispose();
                try{_capture=CreateCapture(20);_capture.StartRecording();}
                catch{_capture?.Dispose();_capture=null;_packets.Writer.TryComplete();_stopped.TrySetResult();throw;}
            }
        });
        }
        finally { _lifecycle.Release(); }
    }

    private WasapiCapture CreateCapture(int milliseconds)
    {
        var capture=new WasapiCapture(_device!,true,milliseconds);
        int rate=Math.Clamp(capture.WaveFormat.SampleRate,8000,96000);capture.WaveFormat=WaveFormat.CreateIeeeFloatWaveFormat(rate,1);
        capture.DataAvailable+=(_,args)=>
        {
            if(args.BytesRecorded==0||!ReferenceEquals(_capture,capture))return;
            var audio=new float[args.BytesRecorded/4];Buffer.BlockCopy(args.Buffer,0,audio,0,args.BytesRecorded);
            double end=Now;Samples?.Invoke(audio,rate,end);
            if(!_detectPitch){if(Level!=null)Level(Math.Sqrt(audio.Sum(v=>(double)v*v)/audio.Length));return;}
            if(!_packets.Writer.TryWrite((audio,rate,end))){var error=new IOException("麥克風音訊未能即時處理，請重新選擇輸入來源。");_packets.Writer.TryComplete(error);Failed?.Invoke(error);}
        };
        capture.RecordingStopped+=(_,args)=>
        {
            if(!ReferenceEquals(_capture,capture))return;
            _packets.Writer.TryComplete();_stopped.TrySetResult();if(args.Exception!=null&&!_disposing)Failed?.Invoke(args.Exception);
        };
        return capture;
    }

    private async Task ProcessAsync(PitchDetector detector)
    {
        using(detector)
        try
        {
            var stream = new PitchStream(detector);
            double origin = double.NaN;
            var pending = new List<float>();var resampler=new AudioResampler();
            await foreach (var packet in _packets.Reader.ReadAllAsync())
            {
                if (double.IsNaN(origin)) origin = packet.End - packet.Audio.Length / (double)packet.Rate;
                Level?.Invoke(Math.Sqrt(packet.Audio.Sum(v => (double)v * v) / Math.Max(1, packet.Audio.Length)));
                pending.AddRange(resampler.Convert(packet.Audio,packet.Rate));
                if (pending.Count < PitchDetector.Hop) continue;
                foreach (var frame in stream.Push(pending.ToArray())) Frame?.Invoke(frame, origin + frame.Time);
                pending.Clear();
            }
            if (!double.IsNaN(origin))
                foreach (var frame in stream.Push(pending.ToArray(), final: true)) Frame?.Invoke(frame, origin + frame.Time);
        }
        catch (Exception error) { if (!_disposing) Failed?.Invoke(error); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
        if (_disposed) return;
        _disposing = true;
        if (_capture != null)
        {
            _capture.StopRecording();
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
            _capture.Dispose(); _capture = null;
        }
        _packets.Writer.TryComplete();
        if (_worker != null) await _worker;
        _device?.Dispose(); _device = null;
        _disposed = true;
        }
        finally { _lifecycle.Release(); }
    }
}
