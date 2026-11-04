namespace OpenKaraoke.Phone;

public sealed class VoiceEcho
{
    private float[] _first=[],_second=[];
    private int _atFirst,_atSecond,_rate,_level;
    private float _toneFirst,_toneSecond,_wet=.5f,_limiter=1;
    private bool _bypassed;
    public int Level {get=>Volatile.Read(ref _level);set=>Volatile.Write(ref _level,Math.Clamp(value,-3,3));}
    public void Reset()
    {Array.Clear(_first);Array.Clear(_second);_atFirst=_atSecond=0;_toneFirst=_toneSecond=0;_limiter=1;_wet=(Level+3)/6f;_bypassed=Level==-3;}
    public void Process(Span<float> audio,int rate)
    {
        if(_rate!=rate){_rate=rate;_first=new float[Math.Max(1,(int)(rate*.145))];_second=new float[Math.Max(1,(int)(rate*.215))];Reset();}
        float target=(Level+3)/6f,smooth=1-MathF.Exp(-1/(rate*.02f)),tone=1-MathF.Exp(-2*MathF.PI*4500/rate),release=1-MathF.Exp(-1/(rate*.06f));
        if(target==0&&_wet<.00001f){if(!_bypassed)Reset();return;}_bypassed=false;
        for(int i=0;i<audio.Length;i++)
        {
            float input=audio[i];_wet+=(target-_wet)*smooth;
            _toneFirst+=(_first[_atFirst]-_toneFirst)*tone;_toneSecond+=(_second[_atSecond]-_toneSecond)*tone;
            if(Math.Abs(_toneFirst)<1e-20f)_toneFirst=0;if(Math.Abs(_toneSecond)<1e-20f)_toneSecond=0;
            _first[_atFirst]=input+_toneFirst*.5f;_second[_atSecond]=input+_toneSecond*.45f;
            if(++_atFirst==_first.Length)_atFirst=0;if(++_atSecond==_second.Length)_atSecond=0;
            float value=input+_wet*(_toneFirst*.55f+_toneSecond*.45f);
            float limit=Math.Abs(value)>.95f?.95f/Math.Abs(value):1;
            _limiter=Math.Min(limit,_limiter+(1-_limiter)*release);audio[i]=value*_limiter;
        }
    }
}
