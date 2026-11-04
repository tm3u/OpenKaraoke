using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Dsp;

namespace OpenKaraoke.Phone;

public sealed class PhoneAudio : IWaveProvider, IDisposable
{
    private const int TransitionMilliseconds = 2;
    private readonly object _gate=new();
    private float[] _buffer=new float[320];
    private int _read,_count,_rate=16000,_volume=100;
    private bool _connected,_muted=true,_primed;
    private float _left,_right;
    private bool _leftAvailable, _rightAvailable, _hasOutput, _outputAvailable, _splicePending;
    private float _lastOutput, _transitionFrom;
    private int _transitionRemaining;
    private double _phase,_level;
    private long _discardedSamples,_stalePackets,_underrunSamples;
    private BiQuadFilter[]? _antiAlias;
    private int _filterOutputRate;
    public WaveFormat WaveFormat=>WaveFormat.CreateIeeeFloatWaveFormat(InputRate,1);
    public event Action<float[],int,double>? Received;
    public event Action<bool>? ConnectionChanged;
    public int InputRate {get{lock(_gate)return _rate;}}
    public bool Connected {get{lock(_gate)return _connected;}}
    public bool Muted {get{lock(_gate)return _muted;}}
    public int Volume {get{lock(_gate)return _volume;}}
    public double Level {get{lock(_gate)return _level;}}
    public int BufferedSamples {get{lock(_gate)return _count;}}
    public double BufferedMilliseconds {get{lock(_gate)return _count*1000d/_rate;}}
    public long DiscardedSamples {get{lock(_gate)return _discardedSamples;}}
    public long StalePackets {get{lock(_gate)return _stalePackets;}}
    public long UnderrunSamples {get{lock(_gate)return _underrunSamples;}}
    public bool HasCaptureTimestamps {get;internal set;}
    internal bool SmoothDiscontinuities { get; set; } = true;
    internal void DiscardStalePacket(){lock(_gate)_stalePackets++;}
    public void Configure(bool muted,int volume)
    {lock(_gate){_muted=muted;_volume=Math.Clamp(volume,0,100);if(muted)ClearLocked();}}
    public void SetFormat(int sampleRate)
    {
        if(sampleRate is <8000 or >96000)throw new ArgumentOutOfRangeException(nameof(sampleRate));
        lock(_gate){_rate=sampleRate;_buffer=new float[Math.Max(1,sampleRate*20/1000)];ClearLocked();}
    }
    public bool Connect()
    {lock(_gate){if(_connected)return false;_connected=true;_muted=true;HasCaptureTimestamps=false;_discardedSamples=_stalePackets=_underrunSamples=0;ClearLocked();}ConnectionChanged?.Invoke(true);return true;}
    public void Disconnect()
    {lock(_gate){if(!_connected)return;_connected=false;_muted=true;ClearLocked();}ConnectionChanged?.Invoke(false);}
    private void ClearLocked()
    {
        _read = _count = 0;
        _level = _phase = 0;
        _left = _right = _lastOutput = _transitionFrom = 0;
        _primed = _leftAvailable = _rightAvailable = _hasOutput = _outputAvailable = _splicePending = false;
        _transitionRemaining = 0;
        _antiAlias = null;
        _filterOutputRate = 0;
        Array.Clear(_buffer);
    }
    public bool AcceptPcm(ReadOnlySpan<byte> bytes,double timestamp)
    {
        if(bytes.Length is 0 or >8192||bytes.Length%2!=0)return false;
        var samples=new float[bytes.Length/2];
        for(int i=0;i<samples.Length;i++)samples[i]=BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i*2,2))/32768f;
        return AcceptSamples(samples,timestamp);
    }
    public bool AcceptSamples(float[] samples,double timestamp)
    {
        if(samples.Length is 0 or >96000||!double.IsFinite(timestamp))return false;
        double energy=0;foreach(float sample in samples){if(!float.IsFinite(sample))return false;energy+=(double)sample*sample;}
        int rate;
        lock(_gate)
        {
            if(!_connected)return false;rate=_rate;_level=Math.Sqrt(energy/samples.Length);
            if(!_muted)
            {
                foreach(float value in samples){if(_count==_buffer.Length){_read=(_read+1)%_buffer.Length;_count--;_discardedSamples++;}_buffer[(_read+_count++)%_buffer.Length]=value;}
                if (_count > _rate * 15 / 1000)
                {
                    int skipped = _count - _rate / 100;
                    _read = (_read + skipped) % _buffer.Length;
                    _count -= skipped;
                    _discardedSamples += skipped;
                    _primed = false;
                    _phase = 0;
                    _splicePending = true;
                }
            }
        }
        Received?.Invoke(samples,rate,timestamp);return true;
    }
    private float NextSample(out bool available)
    {
        available = _count > 0;
        float sample = 0;
        if (available)
        {
            sample = _buffer[_read];
            _read = (_read + 1) % _buffer.Length;
            _count--;
        }
        else _underrunSamples++;
        if(_antiAlias!=null)foreach(var filter in _antiAlias)sample=filter.Transform(sample);
        return Math.Clamp(sample*_volume/100f*.8f,-.85f,.85f);
    }

    private float SmoothOutput(float sample, int transitionFrames)
    {
        if (_hasOutput && (_splicePending || _leftAvailable != _outputAvailable))
        {
            _transitionFrom = _lastOutput;
            _transitionRemaining = transitionFrames;
        }
        _hasOutput = true;
        _outputAvailable = _leftAvailable;
        _splicePending = false;

        if (_transitionRemaining > 0)
        {
            _transitionRemaining--;
            float progress = 1 - _transitionRemaining / (float)transitionFrames;
            float weight = progress * progress * (3 - 2 * progress);
            sample = _transitionFrom + (sample - _transitionFrom) * weight;
        }
        _lastOutput = sample;
        return sample;
    }

    public void AddOutput(Span<float> destination,int outputRate)
    {
        lock(_gate)
        {
            if(_muted||!_connected)return;
            if(_filterOutputRate!=outputRate)
            {
                _filterOutputRate=outputRate;_antiAlias=_rate>outputRate?new[]{.5097956f,.6013449f,.8999762f,2.5629154f}.Select(q=>BiQuadFilter.LowPassFilter(_rate,outputRate*.45f,q)).ToArray():null;
            }
            if (!_primed)
            {
                _left = NextSample(out _leftAvailable);
                _right = NextSample(out _rightAvailable);
                _primed = true;
            }
            double step=_rate/(double)outputRate;
            int transitionFrames = Math.Max(1, outputRate * TransitionMilliseconds / 1000);
            _transitionRemaining = Math.Min(_transitionRemaining, transitionFrames);
            for (int i = 0; i < destination.Length; i++)
            {
                float sample = _left + (_right - _left) * (float)_phase;
                if (SmoothDiscontinuities)
                {
                    if (!_leftAvailable) sample = 0;
                    else if (!_rightAvailable) sample = _left;
                    sample = SmoothOutput(sample, transitionFrames);
                }
                destination[i] += sample;
                _phase += step;
                while (_phase >= 1)
                {
                    _phase--;
                    _left = _right;
                    _leftAvailable = _rightAvailable;
                    _right = NextSample(out _rightAvailable);
                }
            }
        }
    }
    public int Read(byte[] buffer,int offset,int count)
    {var output=MemoryMarshal.Cast<byte,float>(buffer.AsSpan(offset,count));output.Clear();AddOutput(output,InputRate);return count;}
    public void Dispose()=>Disconnect();
}
