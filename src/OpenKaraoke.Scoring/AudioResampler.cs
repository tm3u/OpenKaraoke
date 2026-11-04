namespace OpenKaraoke.Scoring;

public sealed class AudioResampler
{
    private long _index;
    private double _next;
    private float _previous;
    public float[] Convert(float[] input,int inputRate,int outputRate=16000)
    {
        if(inputRate==outputRate)return input;
        var result=new List<float>((int)Math.Ceiling(input.Length*(double)outputRate/inputRate)+1);double step=inputRate/(double)outputRate;
        foreach(float current in input)
        {
            while(_next<=_index){double fraction=_index==0?1:_next-(_index-1);result.Add(_previous+(current-_previous)*(float)fraction);_next+=step;}
            _previous=current;_index++;
        }
        return result.ToArray();
    }
}
