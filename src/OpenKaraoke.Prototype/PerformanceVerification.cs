using System.IO;
using NAudio.Wave;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyPerformanceAsync(Action<string,bool,object?> check,string folder)
    {
        var song=_queue.CurrentSong!;string captureFolder=Path.Combine(folder,"performance-audio");
        var capture=new PerformanceCapture(captureFolder,song);
        var state=new OpenKaraoke.Player.PlaybackState{Loaded=true,Paused=false,Position=0,TrackId=2,Key=0,Volume=100};
        static float[] Tone(int rate,double hz)=>Enumerable.Range(0,rate).Select(i=>(float)(.2*Math.Sin(2*Math.PI*hz*i/rate))).ToArray();
        capture.Update(state,true,100);capture.AcceptOutput(Tone(16000,220),16000,100);
        capture.Update(state with{Position=1,Paused=true},false,101);
        capture.AcceptOutput(Tone(16000,999),16000,102);
        capture.Update(state with{Position=1,Key=2,Volume=50},true,104);capture.AcceptOutput(Tone(16000,220),16000,104);
        capture.Update(state with{Position=2,TrackId=1,Volume=25},true,105);capture.AcceptOutput(Tone(48000,330),48000,105);
        var data=await capture.CompleteAsync(106);
        check("錄音排除暫停時間，並保存各段音軌、升降調與音量",Math.Abs(data.Duration-3)<.001&&data.Music.Length==3&&data.Music[1].Key==2&&data.Music[1].Volume==50&&data.Music[2].Track==1&&data.Voices.Length==2,data.Music);
        var mapped=new PerformanceResult{Duration=30,ReferenceNotes=[new(9,11,60),new(11,21,64),new(24,25,67)],Notes=[new(9,11,60,true),new(11,21,66,false)],RecordingTimeline=[new(0,2,10,1,0,100),new(2,2,20,1,0,100),new(4,1,10,1,0,100)]};
        var projected=mapped.RecordingNotes();
        check("音條依錄音片段映射並裁掉未錄區間，保留原始評分",projected.Original.SequenceEqual(new MelodyNote[]{new(0,1,60),new(1,2,64),new(2,3,64),new(4,5,60)})&&projected.Sung.SequenceEqual(new SungNote[]{new(0,1,60,true),new(1,2,66,false),new(2,3,66,false),new(4,5,60,true)})&&mapped.ReferenceNotes[0].Start==9,projected);
        var withoutRecording=new PerformanceResult{ReferenceNotes=[new(1,2,60)],Notes=[new(1,2,62,false)]};
        check("沒有錄音時仍可查看完整原始音條",withoutRecording.RecordingNotes().Original.SequenceEqual(withoutRecording.ReferenceNotes)&&withoutRecording.RecordingNotes().Sung.SequenceEqual(withoutRecording.Notes),null);
        var restored=System.Text.Json.JsonSerializer.Deserialize<PerformanceResult>(System.Text.Json.JsonSerializer.Serialize(mapped))!;
        check("錄音時間映射可完整保存並還原",restored.RecordingTimeline.SequenceEqual(mapped.RecordingTimeline)&&restored.RecordingNotes().Original.SequenceEqual(projected.Original),null);
        string audio=await PerformanceComposer.MixAsync(data,Path.Combine(_root,"tools","media","ffmpeg.exe"));
        using(var reader=new MediaFoundationReader(audio))check("合成的演唱錄音可由 Windows 實際解碼播放",Math.Abs(reader.TotalTime.TotalSeconds-3)<.1&&reader.Length>0,reader.TotalTime.TotalSeconds);
        string pcm=Path.Combine(captureFolder,"decoded.f32");
        await ToolProcess.RunAsync(Path.Combine(_root,"tools","media","ffmpeg.exe"),["-hide_banner","-nostdin","-v","error","-y","-i",audio,"-f","f32le","-ac","1","-ar","16000",pcm],CancellationToken.None);
        byte[] bytes=await File.ReadAllBytesAsync(pcm);var samples=new float[bytes.Length/4];Buffer.BlockCopy(bytes,0,samples,0,bytes.Length);
        double Magnitude(double from,double hz)
        {
            int start=(int)(from*16000),length=4000;double real=0,imaginary=0;
            for(int i=0;i<length;i++){double phase=2*Math.PI*hz*i/16000;real+=samples[start+i]*Math.Cos(phase);imaginary+=samples[start+i]*Math.Sin(phase);}
            return 2*Math.Sqrt(real*real+imaginary*imaginary)/length;
        }
        double firstVoice=Magnitude(.2,220),firstMusic=Magnitude(.2,660),shiftedMusic=Magnitude(1.2,660*Math.Pow(2,2d/12)),lastVoice=Magnitude(2.2,330),lastMusic=Magnitude(2.2,440);
        check("錄音同時包含歌曲音樂與麥克風人聲",firstVoice>.1&&firstMusic>.04,new{Voice=firstVoice,Music=firstMusic});
        check("錄音保留當次升調後的歌曲音高",shiftedMusic>.02,shiftedMusic);
        check("切換導唱後保留該音軌與不同取樣率的人聲",lastVoice>.1&&lastMusic>.01,new{Voice=lastVoice,Music=lastMusic});
        check("暫停期間的收音沒有進入演唱錄音",Magnitude(1.2,999)<.005,null);
        check("錄音成功後清除自己的暫存 PCM，保留完成音檔",!Directory.EnumerateFiles(captureFolder,"*.wav").Any()&&File.Exists(audio),null);
        string loweredFolder=Path.Combine(folder,"performance-lowered");Directory.CreateDirectory(loweredFolder);
        var lowered=data with{Folder=loweredFolder,Music=[new(0,1,0,0,-3,100)],Voices=[],Duration=1};
        string loweredAudio=await PerformanceComposer.MixAsync(lowered,Path.Combine(_root,"tools","media","ffmpeg.exe"));
        using(var reader=new MediaFoundationReader(loweredAudio))check("降調錄音維持原長度，未指定音軌時使用第一軌",Math.Abs(reader.TotalTime.TotalSeconds-1)<.1,reader.TotalTime.TotalSeconds);
        string loweredPcm=Path.Combine(loweredFolder,"decoded.f32");
        await ToolProcess.RunAsync(Path.Combine(_root,"tools","media","ffmpeg.exe"),["-hide_banner","-nostdin","-v","error","-y","-i",loweredAudio,"-f","f32le","-ac","1","-ar","16000",loweredPcm],CancellationToken.None);
        bytes=await File.ReadAllBytesAsync(loweredPcm);samples=new float[bytes.Length/4];Buffer.BlockCopy(bytes,0,samples,0,bytes.Length);
        double loweredHz=440*Math.Pow(2,-3/12d);
        check("錄音保留實際降調後的歌曲音高",Magnitude(.25,loweredHz)>.02,loweredHz);
        var duplicate=new PerformanceCapture(Path.Combine(folder,"performance-empty"),song);var empty=await duplicate.CompleteAsync(0);bool rejected=false;
        try{await PerformanceComposer.MixAsync(empty,Path.Combine(_root,"tools","media","ffmpeg.exe"));}catch(IOException){rejected=true;}
        check("空白錄音會明確回報，不捏造可播放檔案",rejected,null);
        string discardedFolder=Path.Combine(folder,"performance-discarded");var discarded=new PerformanceCapture(discardedFolder,song);
        discarded.Update(state,true,100);discarded.AcceptOutput(Tone(16000,220),16000,100);await discarded.DiscardAsync();
        check("捨棄本次錄音會停止寫入並清除自己的暫存資料",!Directory.Exists(discardedFolder),null);
        await discarded.DiscardAsync();check("重複捨棄不會恢復或重新建立錄音",!Directory.Exists(discardedFolder),null);
        string sharedFolder=Path.Combine(folder,"performance-preserve-unknown");var shared=new PerformanceCapture(sharedFolder,song);string unrelated=Path.Combine(sharedFolder,"keep.txt");await File.WriteAllTextAsync(unrelated,"保留其他檔案");
        shared.Update(state,true,100);shared.AcceptOutput(Tone(16000,220),16000,100);await shared.DiscardAsync();
        check("捨棄錄音僅清除自己建立的音訊，保留其他檔案",await File.ReadAllTextAsync(unrelated)=="保留其他檔案"&&!Directory.EnumerateFiles(sharedFolder,"*.wav").Any(),null);
    }
}
