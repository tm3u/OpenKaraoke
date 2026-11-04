using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OpenKaraoke.Prototype;

public partial class PerformanceDialog : Window
{
    private readonly PerformanceResult _performance;
    private readonly bool _mutedPlayback;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(50)};
    private readonly CancellationTokenSource _lifetime=new();
    private MediaFoundationReader? _audio;
    private WasapiOut? _output;
    private EventHandler<StoppedEventArgs>? _stoppedHandler;
    private string? _attemptedAudioFile;
    private double _position,_duration,_origin,_dragOrigin;
    private int _version;
    private bool _updating,_dragging,_resumeAfterDrag,_playing,_saving,_closed,_hasFile;
    public PerformanceDialog(Window owner,PerformanceResult performance,bool mutedPlayback=false)
    {
        InitializeComponent();Owner=owner;_performance=performance;_mutedPlayback=mutedPlayback;
        MaxWidth=Math.Max(640,SystemParameters.WorkArea.Width-40);MaxHeight=Math.Max(400,SystemParameters.WorkArea.Height-40);
        PerformanceTitle.Text=performance.Title;PerformanceArtist.Text=performance.Artist;
        PerformanceScore.Text=performance.Score?.Pitch?.ToString("0")??"—";
        ScoreStatus.Visibility=performance.Score?.Pitch==null?Visibility.Visible:Visibility.Collapsed;
        IncompleteStatus.Visibility=performance.Complete?Visibility.Collapsed:Visibility.Visible;
        var notes=performance.RecordingNotes();PerformanceOverview.OriginalNotes=notes.Original;PerformanceOverview.Records=notes.Sung;
        NoPitchStatus.Visibility=notes.Original.Length+notes.Sung.Length==0?Visibility.Visible:Visibility.Collapsed;
        _duration=performance.RecordingTimeline.Length>0?performance.RecordingTimeline.Max(s=>s.Start+s.Duration):performance.Duration;
        RecordingProgress.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>
        {
            _dragOrigin=CurrentPosition();_resumeAfterDrag=_playing;_dragging=true;Seek(_dragOrigin,false);
        }));
        RecordingProgress.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,e)=>
        {
            double target=e.Canceled?_dragOrigin:RecordingProgress.Value;_dragging=false;Seek(target,_resumeAfterDrag);
        }));
        _timer.Tick+=(_,_)=>Refresh();
        Loaded+=(_,_)=>{Refresh();_timer.Start();};
        Closed+=(_,_)=>{_closed=true;_timer.Stop();_lifetime.Cancel();ReleaseAudio();_lifetime.Dispose();};
        RefreshPosition();
    }
    private void Refresh()
    {
        if(_performance.AudioFile is{} file&&file!=_attemptedAudioFile)
        {
            _attemptedAudioFile=file;_hasFile=File.Exists(file);
            try
            {
                _audio=new MediaFoundationReader(file);double duration=_audio.TotalTime.TotalSeconds;
                if(duration<=0)throw new IOException("錄音沒有可播放的內容。");
                _duration=duration;
                var samples=new VolumeSampleProvider(_audio.ToSampleProvider()){Volume=_mutedPlayback?0:1};
                _output=new WasapiOut(AudioClientShareMode.Shared,true,40);_output.Init(samples.ToWaveProvider());
                Seek(_position,true);
            }
            catch(Exception error){PlaybackFailed(error);}
        }
        if(!_dragging)
        {
            try{_position=CurrentPosition();}catch(Exception error){PlaybackFailed(error);}
        }
        RefreshPosition();
        RecordingDownloadButton.IsEnabled=_hasFile&&!_saving;
        RecordingStatus.Text=_performance.Rendering?"錄音製作中…":_audio!=null?"":_hasFile?"無法播放錄音":"本次沒有錄音";
        if(_performance.Error!=null&&RecordingError.Text.Length==0)ShowError(_performance.Error);
    }
    private double CurrentPosition()
    {
        if(!_playing||_output==null)return _position;
        if(_output.PlaybackState==PlaybackState.Stopped)return _duration;
        return Math.Clamp(_origin+(double)_output.GetPosition()/_output.OutputWaveFormat.AverageBytesPerSecond,0,_duration);
    }
    private void StopOutput()
    {
        _playing=false;_version++;
        if(_output==null)return;
        if(_stoppedHandler!=null)_output.PlaybackStopped-=_stoppedHandler;
        _stoppedHandler=null;_output.Stop();
    }
    private void Seek(double seconds,bool resume)
    {
        try
        {
            StopOutput();_position=Math.Clamp(seconds,0,_duration);
            if(_audio!=null&&_output!=null)
            {
                _audio.CurrentTime=TimeSpan.FromSeconds(_position);_origin=_audio.CurrentTime.TotalSeconds;
                int version=_version;
                _stoppedHandler=(_,e)=>Dispatcher.BeginInvoke(new Action(()=>
                {
                    if(_closed||version!=_version)return;
                    if(e.Exception!=null){PlaybackFailed(e.Exception);return;}
                    _playing=false;_position=_duration;RefreshPosition();
                }));
                _output.PlaybackStopped+=_stoppedHandler;
                if(resume&&_position<_duration){_output.Play();_playing=true;}
            }
        }
        catch(Exception error){PlaybackFailed(error);}
        RefreshPosition();
    }
    private void RefreshPosition()
    {
        _updating=true;RecordingProgress.Maximum=Math.Max(.001,_duration);RecordingProgress.Value=_position;_updating=false;
        RecordingProgress.IsEnabled=_duration>0&&(_audio!=null||PerformanceOverview.OriginalNotes.Length+PerformanceOverview.Records.Length>0);
        RecordingTime.Text=$"{TimeLabel(_position)} / {TimeLabel(_duration)}";
        PerformanceOverview.Duration=_duration;PerformanceOverview.Position=_position;PerformanceOverview.InvalidateVisual();
        RecordingPauseButton.IsEnabled=_audio!=null;
        RecordingPauseLabel.Text=_playing?"暫停":_position>=_duration&&_duration>0?"重播":_position>0?"繼續":"播放";
        RecordingStateIcon.Kind=_playing?"pause":"play";
        System.Windows.Automation.AutomationProperties.SetName(RecordingPauseButton,RecordingPauseLabel.Text+"演唱錄音");
    }
    private static string TimeLabel(double seconds)=>seconds>=3600?TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss"):TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
    private void Progress_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(_updating||_performance==null)return;
        if(_dragging){_position=RecordingProgress.Value;RefreshPosition();}
        else Seek(RecordingProgress.Value,_playing);
    }
    private void Toggle_Click(object sender,RoutedEventArgs e)
    {
        if(_audio==null)return;
        try{double position=CurrentPosition();Seek(!_playing&&position>=_duration?0:position,!_playing);}
        catch(Exception error){PlaybackFailed(error);}
    }
    private void PlaybackFailed(Exception error)
    {
        ReleaseAudio();ShowError("無法播放演唱錄音："+error.Message);RefreshPosition();
    }
    private void ReleaseAudio()
    {
        try{StopOutput();}catch(Exception error){ShowError("無法停止錄音播放："+error.Message);}
        try{_output?.Dispose();}catch(Exception error){ShowError("無法釋放錄音裝置："+error.Message);}
        finally{_output=null;_audio?.Dispose();_audio=null;}
    }
    private void ShowError(string text){RecordingError.Text=text;RecordingErrorViewport.Visibility=Visibility.Visible;}
    private async void Download_Click(object sender,RoutedEventArgs e)
    {
        var picker=new SaveFileDialog{Title="下載演唱錄音",Filter="M4A 音訊|*.m4a",DefaultExt=".m4a",AddExtension=true,FileName=Path.ChangeExtension(SongPackageService.Filename(_performance.Title,_performance.Artist),".m4a")};
        if(picker.ShowDialog(this)!=true)return;
        _saving=true;RecordingDownloadButton.IsEnabled=false;RecordingDownloadLabel.Text="儲存中…";
        try{await SaveRecordingAsync(picker.FileName);}
        catch(OperationCanceledException)when(_closed){}
        catch(Exception error){if(!_closed)ShowError("無法儲存錄音："+error.Message);}
        finally{_saving=false;if(!_closed){RecordingDownloadButton.IsEnabled=_hasFile;RecordingDownloadLabel.Text="下載錄音";}}
    }
    internal async Task SaveRecordingAsync(string destination)
    {
        string source=Path.GetFullPath(_performance.AudioFile??throw new InvalidOperationException("錄音尚未完成。")),target=Path.GetFullPath(destination);
        if(source.Equals(target,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("請選擇其他儲存位置，保留原始錄音。");
        string temporary=Path.Combine(Path.GetDirectoryName(target)!,".openkaraoke-recording-"+Guid.NewGuid().ToString("N")+".tmp");
        var cancellation=_lifetime.Token;
        try
        {
            await using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true))
            await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
                await input.CopyToAsync(output,cancellation);
            cancellation.ThrowIfCancellationRequested();File.Move(temporary,target,overwrite:true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private void Window_KeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Escape){Close();e.Handled=true;}}
    private void Header_Drag(object sender,MouseButtonEventArgs e)
    {
        DependencyObject? source=e.OriginalSource as DependencyObject;
        while(source!=null){if(source is Button)return;source=VisualTreeHelper.GetParent(source);}
        if(e.LeftButton==MouseButtonState.Pressed)DragMove();
    }
}
