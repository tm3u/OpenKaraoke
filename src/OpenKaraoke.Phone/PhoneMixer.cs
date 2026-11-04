using System.Buffers;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OpenKaraoke.Phone;

public sealed class PhoneChannel(string id,int slot) : IDisposable
{
    public string Id {get;}=id;
    public int Slot {get;}=slot;
    public string Name=>"手機 "+Slot;
    public PhoneAudio Audio {get;}=new();
    private int _version;
    public int Version=>Volatile.Read(ref _version);
    internal void Connect(){Audio.SetFormat(16000);Audio.Connect();Interlocked.Increment(ref _version);}
    internal void Disconnect(){Audio.Disconnect();Interlocked.Increment(ref _version);}
    public void Dispose()=>Audio.Dispose();
}

public sealed class PhoneMixer : IWaveProvider,IDisposable
{
    public const int MaximumMicrophones=2;
    private readonly object _gate=new(),_outputGate=new();
    private readonly PhoneChannel?[] _channels=new PhoneChannel[MaximumMicrophones];
    private WasapiOut? _output;
    private readonly VoiceEcho _echo=new();
    private bool _hadAudibleSource;
    private volatile bool _suppressMonitoring;
    public bool SuppressMonitoring {get=>_suppressMonitoring;set=>_suppressMonitoring=value;}
    public int EchoLevel {get=>_echo.Level;set=>_echo.Level=value;}
    public WaveFormat WaveFormat {get;private set;}=WaveFormat.CreateIeeeFloatWaveFormat(48000,2);
    public event Action? Changed;
    public event Action<Exception>? OutputFailed;
    public event Action<float[],int,double>? MonitorSamples;
    public PhoneChannel[] Channels {get{lock(_gate)return _channels.OfType<PhoneChannel>().ToArray();}}
    public int ConnectedCount=>Channels.Count(c=>c.Audio.Connected);
    public bool OutputActive=>_output?.PlaybackState==PlaybackState.Playing;
    public long OutputPosition=>_output?.GetPosition()??0;
    public int RequestedOutputMilliseconds {get;private set;}
    public PhoneChannel? TryConnect(string id)
    {
        PhoneChannel? channel;
        lock(_gate)
        {
            channel=_channels.FirstOrDefault(c=>c?.Id==id);
            if(channel?.Audio.Connected==true)return null;
            if(channel==null)
            {
                int slot=Array.FindIndex(_channels,c=>c==null||!c.Audio.Connected);if(slot<0)return null;
                _channels[slot]?.Dispose();channel=new(id,slot+1);_channels[slot]=channel;
            }
            channel.Connect();
        }
        Changed?.Invoke();return channel;
    }
    public void Disconnect(PhoneChannel channel,int? version=null)
    {lock(_gate){if(_channels[channel.Slot-1]!=channel||!channel.Audio.Connected||version.HasValue&&channel.Version!=version.Value)return;channel.Disconnect();}Changed?.Invoke();}
    public int Read(byte[] buffer,int offset,int count)
    {
        var output=MemoryMarshal.Cast<byte,float>(buffer.AsSpan(offset,count));int frames=output.Length/WaveFormat.Channels;
        float[] temporary=ArrayPool<float>.Shared.Rent(frames);
        try
        {
            var mono=temporary.AsSpan(0,frames);mono.Clear();var channels=Channels;foreach(var channel in channels)channel.Audio.AddOutput(mono,WaveFormat.SampleRate);
            bool audible=!SuppressMonitoring&&channels.Any(c=>c.Audio.Connected&&!c.Audio.Muted&&c.Audio.Volume>0);
            if(SuppressMonitoring)mono.Clear();
            if(audible)_echo.Process(mono,WaveFormat.SampleRate);else if(_hadAudibleSource)_echo.Reset();_hadAudibleSource=audible;
            for(int frame=0;frame<frames;frame++){float value=Math.Clamp(mono[frame],-.95f,.95f);output.Slice(frame*WaveFormat.Channels,WaveFormat.Channels).Fill(value);}
            if(MonitorSamples!=null)
            {
                for(int frame=0;frame<frames;frame++)mono[frame]=Math.Clamp(mono[frame],-.95f,.95f);
                MonitorSamples.Invoke(mono.ToArray(),WaveFormat.SampleRate,OpenKaraoke.Scoring.MicrophoneCapture.Now);
            }
        }
        finally{ArrayPool<float>.Shared.Return(temporary);}
        return count;
    }
    public void StartOutput()
    {
        lock(_outputGate)
        {
            if(_output!=null)return;
            foreach(int latency in new[]{10,20})
            {
                var output=new WasapiOut(AudioClientShareMode.Shared,true,latency);
                try
                {
                    var format=output.OutputWaveFormat;
                    if(format.BitsPerSample==32&&(format.Encoding==WaveFormatEncoding.IeeeFloat||format is WaveFormatExtensible ext&&ext.SubFormat==new Guid("00000003-0000-0010-8000-00aa00389b71")))WaveFormat=format;
                    output.Init(this);output.PlaybackStopped+=(_,e)=>{if(e.Exception!=null)OutputFailed?.Invoke(e.Exception);};output.Play();_output=output;RequestedOutputMilliseconds=latency;return;
                }
                catch{output.Dispose();if(latency==20)throw;}
            }
        }
    }
    public void StopOutput(){lock(_outputGate){var output=_output;_output=null;RequestedOutputMilliseconds=0;output?.Stop();output?.Dispose();_echo.Reset();_hadAudibleSource=false;}}
    public void DisconnectAll(){foreach(var channel in Channels)Disconnect(channel);StopOutput();}
    public void Dispose(){DisconnectAll();foreach(var channel in Channels)channel.Dispose();}
}
