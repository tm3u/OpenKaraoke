using System.Globalization;
using System.IO;

namespace OpenKaraoke.Prototype;

public static class PerformanceComposer
{
    private static readonly SemaphoreSlim Gate=new(1,1);
    private static string N(double value)=>value.ToString("0.########",CultureInfo.InvariantCulture);
    public static async Task<string> MixAsync(CapturedPerformance capture,string ffmpeg,CancellationToken cancellation=default)
    {
        if(capture.Error!=null)throw new IOException(capture.Error);
        if(capture.Music.Length==0||capture.Duration<.05)throw new IOException("沒有足夠的演唱錄音。");
        await Gate.WaitAsync(cancellation);
        try
        {
            var parts=new List<string>();
            foreach(var slice in capture.Music)
            {
                string part=Path.Combine(capture.Folder,$"music-{parts.Count}.wav");parts.Add(part);
                string? pitched=null;
                try
                {
                    string[] input=["-ss",N(slice.MediaStart),"-t",N(slice.Duration),"-i",capture.Song.MediaPath,"-map",$"0:a:{Math.Max(0,slice.Track-1)}"];
                    if(slice.Key!=0)
                    {
                        pitched=Path.Combine(capture.Folder,$"pitch-{parts.Count}.wav");
                        string mpv=Path.Combine(Path.GetDirectoryName(ffmpeg)!,"mpv.exe");
                        if(File.Exists(pitched))File.Delete(pitched);
                        await ToolProcess.RunAsync(mpv,["--no-config","--terminal=yes","--msg-level=all=warn","--vid=no","--sid=no","--audio-file-auto=no","--sub-auto=no","--aid="+Math.Max(1,slice.Track),"--start="+N(slice.MediaStart),"--length="+N(slice.Duration),"--pitch="+N(Math.Pow(2,slice.Key/12d)),"--audio-samplerate=48000","--audio-channels=stereo","--volume=100","--of=wav","--oac=pcm_f32le","--o="+pitched,"--",capture.Song.MediaPath],cancellation);
                        input=["-i",pitched];
                    }
                    string filter=$"volume={N(slice.Volume/100)},apad,atrim=duration={N(slice.Duration)},aresample=48000,afade=t=in:d=0.002,afade=t=out:st={N(Math.Max(0,slice.Duration-.002))}:d=0.002";
                    await ToolProcess.RunAsync(ffmpeg,["-hide_banner","-nostdin","-v","error","-y","-threads","1","-filter_threads","1",..input,"-vn","-af",filter,"-ac","2","-c:a","pcm_f32le",part],cancellation);
                }
                finally{if(pitched!=null&&File.Exists(pitched))File.Delete(pitched);}
            }
            string manifest=Path.Combine(capture.Folder,"music.txt");
            await File.WriteAllLinesAsync(manifest,parts.Select(file=>"file '"+file.Replace("\\","/").Replace("'","'\\''")+"'"),cancellation);
            var arguments=new List<string>{"-hide_banner","-nostdin","-v","error","-y","-threads","1","-filter_complex_threads","1","-f","concat","-safe","0","-i",manifest};
            foreach(var voice in capture.Voices)arguments.AddRange(["-i",voice.File]);
            var filters=new List<string>();var inputs=new List<string>{"[0:a]"};
            for(int i=0;i<capture.Voices.Length;i++)
            {
                filters.Add($"[{i+1}:a]aresample=48000,adelay={N(capture.Voices[i].Start*1000)}:all=1[v{i}]");inputs.Add($"[v{i}]");
            }
            filters.Add(string.Join("",inputs)+$"amix=inputs={inputs.Count}:duration=first:normalize=0,alimiter=limit=0.95:latency=1,atrim=duration={N(capture.Duration)}[recording]");
            string temporary=Path.Combine(capture.Folder,"recording.tmp.m4a"),output=Path.Combine(capture.Folder,"recording.m4a");
            arguments.AddRange(["-filter_complex",string.Join(';',filters),"-map","[recording]","-c:a","aac","-b:a","256k","-movflags","+faststart",temporary]);
            await ToolProcess.RunAsync(ffmpeg,arguments,cancellation);File.Move(temporary,output,true);
            foreach(string file in parts.Concat(capture.Voices.Select(v=>v.File)).Append(manifest))File.Delete(file);
            return output;
        }
        finally{Gate.Release();}
    }
}
