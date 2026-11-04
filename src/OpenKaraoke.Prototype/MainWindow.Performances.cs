using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenKaraoke.Player;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public sealed class PerformanceResult
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public double Duration { get; init; }
    public ScoringDifficulty Difficulty { get; init; }
    public ScoreResult? Score { get; init; }
    public SungNote[] Notes { get; init; } = [];
    public MelodyNote[] ReferenceNotes { get; init; } = [];
    public MusicSlice[] RecordingTimeline { get; set; } = [];
    public bool Complete { get; init; }
    public bool Skipped { get; init; }
    public string? AudioFile { get; set; }
    public string? Error { get; set; }
    public bool Rendering { get; set; }
    internal (MelodyNote[] Original,SungNote[] Sung) RecordingNotes()
    {
        if(RecordingTimeline.Length==0)return(ReferenceNotes,Notes);
        IEnumerable<(double Start,double End)> Project(double start,double end)
        {
            foreach(var slice in RecordingTimeline)
            {
                double from=Math.Max(start,slice.MediaStart),to=Math.Min(end,slice.MediaStart+slice.Duration);
                if(to>from)yield return(slice.Start+from-slice.MediaStart,slice.Start+to-slice.MediaStart);
            }
        }
        return(ReferenceNotes.SelectMany(n=>Project(n.Start,n.End).Select(t=>n with{Start=t.Start,End=t.End})).OrderBy(n=>n.Start).ToArray(),
            Notes.SelectMany(n=>Project(n.Start,n.End).Select(t=>n with{Start=t.Start,End=t.End})).OrderBy(n=>n.Start).ToArray());
    }
}

public partial class MainWindow
{
    private readonly Dictionary<string,PerformanceResult> _performances=[];
    private readonly List<Task> _recordingTasks=[];
    private volatile PerformanceCapture? _recording;
    private string? _recordingError;
    private string? _recordingEntryId;
    private PerformanceResult? _finishedPerformance;
    private double _resultsDeadline;
    private bool _reviewOpen;
    private volatile bool _testingMicrophone;
    private const double ResultsDisplaySeconds=10;
    private void InitializePerformances()
    {
        _localInput.Mixer.MonitorSamples+=(samples,rate,time)=>{if(!_testingMicrophone&&!PhoneScoring)_recording?.AcceptOutput(samples,rate,time);};
        _phone.Mixer.MonitorSamples+=(samples,rate,time)=>{if(!_testingMicrophone&&PhoneScoring)_recording?.AcceptOutput(samples,rate,time);};
    }
    private void StartPerformance()
    {
        if(_queue.CurrentSong is not {} song||_queue.CurrentId is not {} id)return;
        _recordingEntryId=id;_finishedPerformance=null;_recordingError=null;
        try{_recording=new PerformanceCapture(Path.Combine(_store.DataDirectory,"performances",id),song);}
        catch(Exception error){_recording=null;_recordingError="無法開始演唱錄音："+error.Message;Log(error.ToString());}
    }
    private void RecordPlayback(PlaybackState state)=>_recording?.Update(state,_songFlow==SongFlow.Playing&&state.Loaded&&!state.Paused&&!state.Ended&&!_stopped,MicrophoneCapture.Now);
    private async Task EndPerformanceAsync(bool complete)
    {
        var capture=_recording;string? id=_recordingEntryId;_recording=null;_recordingEntryId=null;
        if(id==null)return;
        double until=complete?_player.State.Duration:CurrentPosition;
        var evaluated=_scoreSession?.Result(until,complete:true);
        var score=_voice.ScoringEnabled&&!ScoringFailed?evaluated:null;
        var result=new PerformanceResult
        {
            Title=_queue.CurrentSong?.Title??"",Artist=_queue.CurrentSong?.Artist??"",Duration=_player.State.Duration,
            Difficulty=_scoreSession?.Difficulty??_voice.Difficulty,Score=score,Complete=complete,Rendering=capture!=null,Error=_recordingError,
            Notes=_scoreSession?.Notes()??[],ReferenceNotes=_reference?.Notes??[]
        };
        _performances[id]=result;_finishedPerformance=result;ResultRecordViewport.ScrollToHorizontalOffset(0);
        if(capture==null)return;
        var data=await capture.CompleteAsync(MicrophoneCapture.Now);
        result.RecordingTimeline=data.Music;
        _recordingTasks.Add(FinishRecordingAsync(result,data));
    }
    private async Task DiscardPerformanceAsync()
    {
        var capture=_recording;_recording=null;
        try{if(capture!=null)await capture.DiscardAsync();}
        catch{_recording=capture;throw;}
        string? id=_recordingEntryId??_queue.CurrentId;_recordingEntryId=null;_recordingError=null;
        var result=new PerformanceResult{Title=_queue.CurrentSong?.Title??"",Artist=_queue.CurrentSong?.Artist??"",Duration=_player.State.Duration,Skipped=true};
        if(id!=null)_performances[id]=result;
        _finishedPerformance=result;_scoreSession=null;_finalScore=null;ResultRecordViewport.ScrollToHorizontalOffset(0);
    }
    private async Task FinishRecordingAsync(PerformanceResult result,CapturedPerformance capture)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(capture.Folder,"performance.json"),JsonSerializer.Serialize(result));
            result.AudioFile=await PerformanceComposer.MixAsync(capture,Path.Combine(_root,"tools","media","ffmpeg.exe"));
        }
        catch(Exception error){result.Error=error.Message;Log(error.ToString());}
        finally
        {
            result.Rendering=false;
            try{await File.WriteAllTextAsync(Path.Combine(capture.Folder,"performance.json"),JsonSerializer.Serialize(result));}catch(Exception error){Log(error.ToString());}
            if(!_closing)await Dispatcher.InvokeAsync(RefreshQueue);
        }
    }
    private void History_MouseWheel(object sender,MouseWheelEventArgs e)
    {
        var viewport=(ScrollViewer)sender;viewport.ScrollToHorizontalOffset(viewport.HorizontalOffset-e.Delta);e.Handled=true;
    }
    private async void QueueReview_Click(object sender,RoutedEventArgs e)
    {
        if(_reviewOpen||(sender as Button)?.Tag is not string id||!_performances.TryGetValue(id,out var result)||result.Skipped)return;
        await RunAsync(async()=>
        {
            double openedAt=MicrophoneCapture.Now;_reviewOpen=true;
            bool phone=_phone.Mixer.SuppressMonitoring,local=_localInput.Mixer.SuppressMonitoring;
            try
            {
                if(_busy&&_advanceCompletion!=null)await _advanceCompletion.Task;
                if(_songFlow==SongFlow.Playing&&_player.State.Loaded&&!_player.State.Paused&&!_player.State.Ended)await PauseAsync();
                RecordPlayback(_player.State);
                _phone.Mixer.SuppressMonitoring=_localInput.Mixer.SuppressMonitoring=true;
                new PerformanceDialog(this,result).ShowDialog();
            }
            finally
            {
                _phone.Mixer.SuppressMonitoring=phone;_localInput.Mixer.SuppressMonitoring=local;
                if(_songFlow==SongFlow.Results&&_voice.AutoPlayNext&&_resultsDeadline>0)
                    _resultsDeadline=MicrophoneCapture.Now+Math.Clamp(_resultsDeadline-openedAt,0,ResultsDisplaySeconds);
                _reviewOpen=false;
            }
        });
    }
    private async void AutoPlayNext_Changed(object sender,RoutedEventArgs e)
    {
        if(_updating||_closing||_store==null)return;
        if(ToggleStatus.IsChanging(AutoPlayNextToggle)){_updating=true;AutoPlayNextToggle.IsChecked=ToggleStatus.GetTargetEnabled(AutoPlayNextToggle);_updating=false;return;}
        bool original=_voice.AutoPlayNext,enabled=AutoPlayNextToggle.IsChecked==true;
        ToggleStatus.Begin(AutoPlayNextToggle,enabled);
        try
        {
            await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Background);
            var value=_voice with{AutoPlayNext=enabled};value.Save(VoiceSettingsPath);_voice=value;
            _resultsDeadline=_songFlow==SongFlow.Results&&enabled?MicrophoneCapture.Now+ResultsDisplaySeconds:0;
        }
        catch(Exception error){_updating=true;AutoPlayNextToggle.IsChecked=original;_updating=false;ShowError(error);}
        finally{ToggleStatus.Complete(AutoPlayNextToggle,_voice.AutoPlayNext);}
    }
}
