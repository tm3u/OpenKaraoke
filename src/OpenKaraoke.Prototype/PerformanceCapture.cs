using System.IO;
using System.Threading.Channels;
using NAudio.Wave;
using OpenKaraoke.Library;
using OpenKaraoke.Player;
using PlaybackState = OpenKaraoke.Player.PlaybackState;

namespace OpenKaraoke.Prototype;

public sealed record MusicSlice(double Start, double Duration, double MediaStart, int Track, int Key, double Volume);
public sealed record VoicePart(string File, double Start);
public sealed record CapturedPerformance(string Folder, Song Song, MusicSlice[] Music, VoicePart[] Voices, double Duration, string? Error);

public sealed class PerformanceCapture
{
    private sealed record Block(double Position, int Rate, float[] Samples);
    private readonly object _gate = new();
    private readonly Channel<Block> _blocks = Channel.CreateBounded<Block>(1024);
    private readonly List<MusicSlice> _music = [];
    private readonly List<VoicePart> _voices = [];
    private readonly Task _writing;
    private readonly string _folder;
    private readonly Song _song;
    private bool _active, _finished;
    private double _elapsed, _started, _mediaStart, _volume;
    private int _track, _key;
    private string? _error;
    public PerformanceCapture(string folder, Song song)
    {
        _folder=Path.GetFullPath(folder); _song=song; Directory.CreateDirectory(_folder); _writing=Task.Run(WriteAsync);
    }
    public void Update(PlaybackState state, bool active, double now)
    {
        lock(_gate)
        {
            if(_finished)return;
            if(_active&&active&&_track==state.TrackId&&_key==state.Key&&_volume==state.Volume&&Math.Abs(state.Position-(_mediaStart+now-_started))<.4)return;
            CloseSlice(now);
            if(!active)return;
            _active=true;_started=now;_mediaStart=state.Position;_track=state.TrackId;_key=state.Key;_volume=state.Volume;
        }
    }
    private void CloseSlice(double now)
    {
        if(!_active)return;
        double duration=Math.Max(0,now-_started);
        if(duration>.001)_music.Add(new(_elapsed,duration,_mediaStart,_track,_key,_volume));
        _elapsed+=duration;_active=false;
    }
    public void AcceptOutput(float[] samples, int rate, double now)
    {
        lock(_gate)
        {
            if(!_active||_finished||_error!=null||samples.Length==0)return;
            if(!_blocks.Writer.TryWrite(new(_elapsed+Math.Max(0,now-_started),rate,samples)))_error="錄音寫入速度不足，本次錄音不完整。";
        }
    }
    private async Task WriteAsync()
    {
        WaveFileWriter? writer=null;int rate=0;double start=0;
        try
        {
            await foreach(var block in _blocks.Reader.ReadAllAsync())
            {
                if(writer==null||rate!=block.Rate)
                {
                    writer?.Dispose();rate=block.Rate;start=block.Position;
                    string file=Path.Combine(_folder,$"voice-{_voices.Count}.wav");
                    _voices.Add(new(file,start));writer=new WaveFileWriter(file,WaveFormat.CreateIeeeFloatWaveFormat(rate,1));
                }
                double gap=block.Position-start-writer.Length/(double)writer.WaveFormat.AverageBytesPerSecond;
                if(gap>.1)
                {
                    var silence=new float[Math.Min(rate,Math.Max(1,(int)(gap*rate)))];int remaining=(int)(gap*rate);
                    while(remaining>0){int length=Math.Min(silence.Length,remaining);writer.WriteSamples(silence,0,length);remaining-=length;}
                }
                int skip=gap<-.1?Math.Min(block.Samples.Length,(int)(-gap*rate)):0;
                writer.WriteSamples(block.Samples,skip,block.Samples.Length-skip);
            }
        }
        catch(Exception error){lock(_gate)_error="無法保存演唱錄音："+error.Message;}
        finally{writer?.Dispose();}
    }
    public async Task<CapturedPerformance> CompleteAsync(double now)
    {
        lock(_gate){if(!_finished){CloseSlice(now);_finished=true;_blocks.Writer.TryComplete();}}
        await _writing;
        return new(_folder,_song,_music.ToArray(),_voices.ToArray(),_elapsed,_error);
    }
    public async Task DiscardAsync()
    {
        lock(_gate){_active=false;_finished=true;_blocks.Writer.TryComplete();}
        await _writing;
        if(!Directory.Exists(_folder))return;
        foreach(var voice in _voices)
        {
            string file=Path.GetFullPath(voice.File);
            if(!string.Equals(Path.GetDirectoryName(file),_folder,StringComparison.OrdinalIgnoreCase))throw new IOException("錄音暫存檔不在本次錄音目錄內。");
            File.Delete(file);
        }
        if(Directory.Exists(_folder)&&!Directory.EnumerateFileSystemEntries(_folder).Any())Directory.Delete(_folder);
    }
}
