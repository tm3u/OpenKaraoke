using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using OpenKaraoke.Library;
using OpenKaraoke.Player;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow : Window
{
    private readonly string _root=FindRoot();
    private readonly LibraryStore _store;
    private readonly PlaybackQueue _queue;
    private readonly MpvPlayer _player;
    private readonly MediaImportService _importer;
    private readonly SongPackageService _packages;
    private readonly AiRuntime _aiRuntime;
    private readonly SongCreationService _creator;
    private readonly SongProcessingQueue _processingQueue;
    private readonly ObservableCollection<SongRow> _songs=[];
    private readonly ObservableCollection<QueueRow> _queueRows=[];
    private readonly Dictionary<string,(long At,bool Shown)> _queueFeedback=[];
    private int _queueFeedbackVersion;
    private readonly DispatcherTimer _searchTimer=new() { Interval=TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _volumeTimer=new() { Interval=TimeSpan.FromMilliseconds(65) };
    private readonly DispatcherTimer _toastTimer=new() { Interval=TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _diagnosticsTimer=new() { Interval=TimeSpan.FromSeconds(1) };
    private readonly string _diagnosticsPath;
    private bool _updating, _busy, _fullscreen, _closing, _closed, _fetching, _stopped;
    private int _queryVersion, _searchTotal, _generation, _endedGeneration=-1;
    private double _volume=100, _oldVolume=100;
    private WindowState _previousWindowState;
    private Rect _previousBounds;
    private WindowChrome? _previousChrome;
    private string _queueSignature="";
    private readonly object _logLock=new();

    public MainWindow()
    {
        InitializeComponent();
        string version=typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        Title=$"OpenKaraoke v{version}";AppVersionLabel.Text=$"v{version}";
        InitializeSizing();
        string[] args=Environment.GetCommandLineArgs();
        if(args.Contains("--verify-idle-performance")&&Argument(args,"--data-dir")==null)
            throw new ArgumentException("待機效能量測必須用 --data-dir 指定隔離資料目錄。");
        bool verification=args.Any(a=>a is "--verify-ui" or "--verify-empty-ui" or "--verify-scoring" or "--verify-creation" or "--verify-phone" or "--verify-idle-performance");
        if(verification)
        {
            ShowActivated=false;
            void IgnoreExternalInput(object sender,PreProcessInputEventArgs e)
            {if(e.StagingItem.Input is MouseButtonEventArgs or MouseWheelEventArgs or KeyEventArgs or TextCompositionEventArgs or TouchEventArgs or StylusEventArgs)e.Cancel();}
            InputManager.Current.PreProcessInput+=IgnoreExternalInput;
            Closed+=(_,_)=>InputManager.Current.PreProcessInput-=IgnoreExternalInput;
        }
        string data=Argument(args,"--data-dir")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenKaraoke");
        _store=new LibraryStore(data); _queue=new PlaybackQueue(_store); _importer=new MediaImportService(_root,_store);
        _packages=new SongPackageService(_root,_store);
        _aiRuntime=new AiRuntime(_root,Argument(args,"--ai-dir")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenKaraoke","ai"));
        _references=new ScoringReferenceService(_root,_store.DataDirectory,_aiRuntime);
        _creator=new SongCreationService(_root,_aiRuntime,_packages);
        _processingQueue=new SongProcessingQueue(Path.Combine(_store.DataDirectory,"creation"),new SongProcessingService(_store,_creator,_packages,_references).ProcessAsync,_store,
            ()=>_aiRuntime.IsReady,(progress,token)=>_aiRuntime.EnsureAsync(progress,token));
        _processingQueue.SongAdded+=song=>Dispatcher.BeginInvoke(async ()=>{if(_closing)return;await SearchAsync();Notify("已加入歌曲庫 · "+song.Title);});
        InitializeScoring();
        InitializePhone();InitializePerformances();
        _diagnosticsPath=Argument(args,"--diagnostics")??Path.Combine(_store.DataDirectory,"artifacts","player-state.json");
        _player=new MpvPlayer(Log);
        _player.Changed+=state=>{UpdatePlaybackClock(state);Dispatcher.BeginInvoke(()=>Render(_player.State));};
        _player.ShortcutRequested+=action=>{if(!verification)Dispatcher.BeginInvoke(async ()=>await HandleShortcutAsync(action));};
        SongList.ItemsSource=_songs; QueueList.ItemsSource=_queueRows; ShowPlayedToggle.IsChecked=_queue.ShowPlayed;ToggleStatus.Complete(ShowPlayedToggle,_queue.ShowPlayed);
        _searchTimer.Tick+=async (_,_)=>{ _searchTimer.Stop(); await SearchAsync(); };
        _volumeTimer.Tick+=async (_,_)=>{ _volumeTimer.Stop(); await RunAsync(()=>_player.SetVolumeAsync(_volume)); };
        _toastTimer.Tick+=(_,_)=>{ _toastTimer.Stop(); Toast.Visibility=Visibility.Collapsed; };
        _diagnosticsTimer.Tick+=(_,_)=>WriteDiagnostics();
        StateChanged+=(_,_)=>{ MaximizeIcon.Kind=WindowState==WindowState.Maximized?"copy":"square"; MaximizeButton.ToolTip=WindowState==WindowState.Maximized?"視窗化":"最大化"; };
        Loaded+=async (_,_)=>
        {
            _diagnosticsTimer.Start(); await SearchAsync(); Render(_player.State);_processingQueue.Start();
            var recovery = new[] { _store.RecoveryMessage, _processingQueue.RecoveryMessage }.Where(message => message.Length > 0).ToArray();
            foreach (string message in recovery) Log(message);
            if (recovery.Length > 0) Notify("保存資料有異常，已還原可用項目；備份位置請見錯誤紀錄。");
            try{if(_voice.DeviceId=="phone")await _phone.ApplyAsync(_voice);else await _localInput.StartAsync(_voice.DeviceId,_voice.LocalVolume);}catch(Exception error){if(!PhoneScoring){_microphoneFailed=true;_scoringError="未連接麥克風或無法存取";}ShowError(error);}
            if(args.Contains("--fixture"))await RunAsync(async ()=>
            {
                var fixture=_store.Search("合成測試歌曲").Songs.FirstOrDefault();
                if(!File.Exists(FixtureMediaPath))throw new IOException("找不到驗收影片，請以 --fixture-media 指定原始碼套件中的合成測試影片。");
                fixture??=await _importer.ImportAsync("合成測試歌曲","合成音訊",FixtureMediaPath,null,1,2,null,CancellationToken.None);
                _queue.Add(fixture); RefreshQueue(); await SearchAsync(); await AdvanceAsync();
            });
            if(Argument(args,"--snapshot") is {} snapshot)
            {
                var capture=new DispatcherTimer { Interval=TimeSpan.FromSeconds(2) };
                capture.Tick+=async (_,_)=>{capture.Stop();await RunAsync(()=>CaptureAsync(snapshot));};capture.Start();
            }
            if(Argument(args,"--verify-ui") is {} uiReport)await VerifyInterfaceAsync(uiReport);
            if(Argument(args,"--verify-empty-ui") is {} emptyReport)await VerifyEmptyInterfaceAsync(emptyReport);
            if(Argument(args,"--verify-creation") is {} creationReport)await VerifyCreationAsync(creationReport);
            if(Argument(args,"--verify-scoring") is {} scoringReport)await VerifyScoringAsync(scoringReport);
            if(Argument(args,"--verify-phone") is {} phoneReport)await VerifyPhoneAsync(phoneReport);
            if(Argument(args,"--verify-idle-performance") is {} idlePerformanceReport)await VerifyIdlePerformanceAsync(idlePerformanceReport);
        };
        Closing+=Window_Closing;
    }
    private static string? Argument(string[] args,string key) { int i=Array.IndexOf(args,key); return i>=0&&i+1<args.Length?Path.GetFullPath(args[i+1]):null; }
    private string FixtureMediaPath=>Argument(Environment.GetCommandLineArgs(),"--fixture-media")??Path.Combine(_root,"fixtures","dual-track-test.mp4");
    private static string FindRoot()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory!=null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"tools","media","mpv.exe")))return directory.FullName;
        return AppContext.BaseDirectory;
    }
    private async Task SearchAsync(bool append=false)
    {
        string query=SearchBox.Text.Trim(); int version=append?_queryVersion:++_queryVersion, offset=append?_songs.Count:0;
        if(append&&(_fetching||query.Length==0||offset>=_searchTotal))return;
        _fetching=true;
        try
        {
            var page=await Task.Run(()=>_store.Search(query,offset));
            if(version!=_queryVersion||_closing)return;
            if(!append)_songs.Clear();
            var queued=_queue.Entries.Where(entry=>entry.Status!="played").Select(entry=>entry.Song.Id).ToHashSet();
            foreach(var song in page.Songs)_songs.Add(new SongRow(song,_songs.Count+1,queued.Contains(song.Id)));
            _searchTotal=page.Total; LibraryCount.Text=$"{page.Total} 首";
            bool popular=query.Length==0;
            PopularLabel.Visibility=popular?Visibility.Visible:Visibility.Collapsed; PopularRow.Height=new GridLength(42);
            LibraryEmpty.Text=popular?"尚未加入歌曲":"找不到符合的歌曲"; LibraryEmpty.Visibility=_songs.Count==0?Visibility.Visible:Visibility.Collapsed;
        }
        catch(Exception error){ ShowError(error); }
        finally { if(version==_queryVersion)_fetching=false; }
    }
    private void Search_Changed(object sender,TextChangedEventArgs e)
    {
        if(SearchPlaceholder==null)return;
        bool empty=SearchBox.Text.Length==0;
        SearchPlaceholder.Visibility=empty?Visibility.Visible:Visibility.Collapsed; ClearSearchButton.Visibility=empty?Visibility.Collapsed:Visibility.Visible;
        _queryVersion++; _fetching=false; _searchTimer.Stop(); _searchTimer.Start();
    }
    private void ClearSearch_Click(object sender,RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private async void Songs_ScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(e.OriginalSource is ScrollViewer scroll&&e.VerticalChange>0&&scroll.VerticalOffset+scroll.ViewportHeight>=scroll.ExtentHeight-8)await SearchAsync(true);
    }
    private void AddSong_Click(object sender,RoutedEventArgs e)=>CreateSongDialog().ShowDialog();
    private AddSongDialog CreateSongDialog()=>new(this,_packages,_creator,_processingQueue);
    private async void DeleteSong_Click(object sender,RoutedEventArgs e)
    {
        if((sender as Button)?.Tag is not Song song)return;
        var dialog=new RemoveSongDialog(this,song);
        if(dialog.ShowDialog()!=true)return;
        await RunAsync(async ()=>{ _store.Remove(song.Id,dialog.Confirmation); await SearchAsync(); Notify("已從歌曲庫移除 · "+song.Title); });
    }
    private async void Enqueue_Click(object sender,RoutedEventArgs e)
    {
        if((sender as Button)?.Tag is not Song song)return;
        await RunAsync(async ()=>
        {
            _queue.Add(song);_queueFeedback[song.Id]=(0,false);
            int feedbackVersion=++_queueFeedbackVersion;
            RefreshQueue();Render(_player.State);
            bool popular=SearchBox.Text.Trim().Length==0;
            if(popular)await SearchAsync();
            if(feedbackVersion!=_queueFeedbackVersion)return;
            var row=_songs.FirstOrDefault(value=>value.Song.Id==song.Id);
            if(row!=null&&popular&&SearchBox.Text.Trim().Length==0)SongList.ScrollIntoView(row);
            await Dispatcher.InvokeAsync(SongList.UpdateLayout,DispatcherPriority.Loaded);
            if(feedbackVersion!=_queueFeedbackVersion)return;
            foreach(var (id,feedback) in _queueFeedback.ToArray())
            {
                var visible=_songs.FirstOrDefault(value=>value.Song.Id==id);
                var target=visible==null?null:FindQueueFeedback(SongList.ItemContainerGenerator.ContainerFromItem(visible));
                long remaining=feedback.Shown?900-(Environment.TickCount64-feedback.At):900;
                if(target==null||remaining<=0){_queueFeedback.Remove(id);continue;}
                if(!feedback.Shown)_queueFeedback[id]=(Environment.TickCount64,true);
                if(!target.IsConfirming||id==song.Id)target.ShowAdded(TimeSpan.FromMilliseconds(remaining));
            }
        });
    }
    private static QueueAddedFeedback? FindQueueFeedback(DependencyObject? node)
    {
        if(node is QueueAddedFeedback feedback)return feedback;
        if(node==null)return null;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
            if(FindQueueFeedback(VisualTreeHelper.GetChild(node,i)) is {} child)return child;
        return null;
    }
    private void QueueUp_Click(object sender,RoutedEventArgs e)=>ChangeQueue(sender,id=>_queue.Move(id,-1));
    private void QueueDown_Click(object sender,RoutedEventArgs e)=>ChangeQueue(sender,id=>_queue.Move(id,1));
    private void QueueRemove_Click(object sender,RoutedEventArgs e)=>ChangeQueue(sender,_queue.Remove);
    private void ChangeQueue(object sender,Action<string> action)
    {
        if(_busy)return;
        try { if((sender as Button)?.Tag is string id)action(id); RefreshQueue(); Render(_player.State); }
        catch(Exception error){ ShowError(error); }
    }
    private void ShowPlayed_Changed(object sender,RoutedEventArgs e)
    {
        if(_queue==null||_updating)return;
        bool original=_queue.ShowPlayed;ToggleStatus.Begin(ShowPlayedToggle,ShowPlayedToggle.IsChecked==true);
        try { _queue.ShowPlayed=ShowPlayedToggle.IsChecked==true; _queue.Save(); RefreshQueue(); }
        catch(Exception error){_queue.ShowPlayed=original;_updating=true;ShowPlayedToggle.IsChecked=original;_updating=false;ShowError(error);}
        finally{ToggleStatus.Complete(ShowPlayedToggle,_queue.ShowPlayed);}
    }
    private void RefreshQueue()
    {
        var state=_player.State; var pending=_queue.Entries.Where(e=>e.Status=="pending").Select(e=>e.Id).ToArray();
        var visible=_queue.Entries.Where(entry=>_queue.ShowPlayed||entry.Status!="played").ToArray();
        for(int i=0;i<visible.Length;i++)
        {
            int old=-1;for(int j=i;j<_queueRows.Count;j++)if(_queueRows[j].Id==visible[i].Id){old=j;break;}
            if(old<0)_queueRows.Insert(i,new QueueRow(visible[i],i+1,pending,state,_stopped,_performances.GetValueOrDefault(visible[i].Id)));
            else {if(old!=i)_queueRows.Move(old,i);_queueRows[i]=new QueueRow(visible[i],i+1,pending,state,_stopped,_performances.GetValueOrDefault(visible[i].Id));}
        }
        while(_queueRows.Count>visible.Length)_queueRows.RemoveAt(_queueRows.Count-1);
        QueueCount.Text=$"{_queue.PendingCount} 首";
        QueueEmpty.Text=_queue.CurrentSong==null?"目前沒有播放任何歌曲":"目前沒有待播歌曲";
        QueueEmpty.Visibility=QueueList.Items.Count==0?Visibility.Visible:Visibility.Collapsed;
        _queueSignature=QueueSignature(state);
        RefreshSongHighlight();
    }
    private string QueueSignature(PlaybackState state)=>string.Join(';',_queue.Entries.Select(e=>e.Id+e.Status))+_queue.Mode+state.Paused+state.Loaded+state.Ended+_stopped;

    private async Task AdvanceAsync()
    {
        if(_busy||_closing)return;
        _busy=true;_resultsDeadline=0;
        _advanceCompletion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation=new CancellationTokenSource();_transitionCancellation=cancellation;
        try
        {
            await _player.SetPausedAsync(true);RecordPlayback(_player.State);
            await StopMicrophoneAsync();await EndPerformanceAsync(false);
            _referenceCancellation?.Cancel();_referenceLoading=false;
            var next=_queue.Next();
            if(next==null){_queue.Complete();_songFlow=SongFlow.Idle;_transitionDeadline=0;return;}
            _queue.Complete();
            await ApplySubtitleSettingsAsync(_voice);
            await _player.StartAsync(Path.Combine(_root,"tools","media","mpv.exe"),VideoHost.VideoHandle);
            await _player.LoadAsync(next.Song.MediaPath,play:false);
            await _player.SelectTrackAsync(next.Mode=="original"?next.Song.OriginalTrack:next.Song.InstrumentalTrack);
            await _player.SetVolumeAsync(_volume);
            _queue.Start(next);_generation++;_stopped=false;
            await BeginSongAsync(next.Song,cancellation.Token);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){ }
        catch(Exception)
        {
            _songFlow=SongFlow.Playing;_transitionDeadline=0;_stopped=true;
            _reference=null;_scoreSession=null;_finalScore=null;_referenceLoading=false;
            _scoringError="歌曲載入失敗，請重試或移除該待播項目。";
            try{await _player.UnloadAsync();}catch(Exception error){Log(error.ToString());}
            throw;
        }
        finally{_busy=false;_advanceCompletion.TrySetResult();if(_transitionCancellation==cancellation)_transitionCancellation=null;cancellation.Dispose();RefreshQueue();Render(_player.State);WriteDiagnostics();}
    }
    private async Task BeginSongAsync(OpenKaraoke.Library.Song song,CancellationToken cancellation,bool prepare=true)
    {
        _finishedPerformance=null;_finalScore=null;_songFlow=SongFlow.Transition;
        _transitionDeadline=MicrophoneCapture.Now+3;
        TransitionTitle.Text=song.Title;TransitionArtist.Text=song.Artist;Render(_player.State);
        await Task.WhenAll(prepare?PrepareScoringAsync():Task.CompletedTask,Task.Delay(TimeSpan.FromSeconds(3),cancellation));
        cancellation.ThrowIfCancellationRequested();
        await SetSongCaptionAsync();_songFlow=SongFlow.Playing;StartPerformance();
        await _player.SetPausedAsync(false);RecordPlayback(_player.State);
    }
    private double CaptionScale=>Math.Max(.1,_videoBounds.Width/Math.Max(1,VideoViewport.ActualWidth));
    private Task SetSongCaptionAsync()
    {
        var song=_queue.CurrentSong;
        return song==null?Task.CompletedTask:_player.SetCaptionAsync(song.Title,song.Artist,VideoHost.ActualWidth,VideoHost.ActualHeight,CaptionScale);
    }
    private void RefreshSongHighlight()
    {
        var queued=_queue.Entries.Where(entry=>entry.Status!="played").Select(entry=>entry.Song.Id).ToHashSet();
        for(int i=0;i<_songs.Count;i++)
        {
            bool inQueue=queued.Contains(_songs[i].Song.Id);
            if(_songs[i].IsQueued!=inQueue)_songs[i]=new SongRow(_songs[i].Song,i+1,inQueue);
        }
    }
    private async void Pause_Click(object sender,RoutedEventArgs e)=>await RunAsync(PauseAsync);
    private async Task PauseAsync()
    {
        if(_busy)return;
        if(_songFlow==SongFlow.Results){await AdvanceAsync();return;}
        if(_songFlow==SongFlow.Idle||!_player.State.Loaded)
        {
            if(_queue.PendingCount>0)await AdvanceAsync();
            return;
        }
        if(_player.State.Ended)return;
        if(_player.State.Paused)_queue.Replay();
        _stopped=false;
        if(!_player.State.Paused)await StopMicrophoneAsync();
        await _player.TogglePauseAsync(); RefreshQueue();
    }
    private async void Cut_Click(object sender,RoutedEventArgs e)=>await RunAsync(()=>FinishSongAsync(skipped:true));
    private async void Restart_Click(object sender,RoutedEventArgs e)=>await RunAsync(RestartAsync);
    private async Task RestartAsync()
    {
        _resultsDeadline=0;await CancelTransitionAsync();
        if(_busy||!_player.State.Loaded||_queue.CurrentSong is not {} song)return;
        _busy=true;_advanceCompletion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation=new CancellationTokenSource();_transitionCancellation=cancellation;
        try
        {
            await _player.SetPausedAsync(true);RecordPlayback(_player.State);await StopMicrophoneAsync();
            if(_recording!=null){await EndPerformanceAsync(false);_queue.Complete();}
            _finalScore=null;_microphoneFailed=false;_scoringError="";_generation++;_stopped=false;
            await _player.SeekAsync(0);_queue.Replay();
            if(_reference!=null)ResetScoringSessions();
            await BeginSongAsync(song,cancellation.Token,prepare:_reference==null);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){ }
        finally{_busy=false;_advanceCompletion.TrySetResult();if(_transitionCancellation==cancellation)_transitionCancellation=null;cancellation.Dispose();RefreshQueue();Render(_player.State);}
    }
    private async void Original_Click(object sender,RoutedEventArgs e)=>await RunAsync(()=>ModeAsync("original"));
    private async void Instrumental_Click(object sender,RoutedEventArgs e)=>await RunAsync(()=>ModeAsync("instrumental"));
    private async Task ModeAsync(string mode)
    {
        if(_busy)return;
        if(_player.State.Loaded&&_queue.CurrentSong is {} song)await _player.SelectTrackAsync(mode=="original"?song.OriginalTrack:song.InstrumentalTrack);
        _queue.SetMode(mode); Render(_player.State); RefreshQueue();
    }
    private async void KeyDown_Click(object sender,RoutedEventArgs e)=>await RunAsync(()=>_player.ChangeKeyAsync(-1));
    private async void KeyUp_Click(object sender,RoutedEventArgs e)=>await RunAsync(()=>_player.ChangeKeyAsync(1));
    private async void KeyReset_Click(object sender,RoutedEventArgs e)=>await RunAsync(_player.ResetKeyAsync);
    private void EchoDown_Click(object sender,RoutedEventArgs e)=>SetEchoLevel(_voice.EchoLevel-1);
    private void EchoUp_Click(object sender,RoutedEventArgs e)=>SetEchoLevel(_voice.EchoLevel+1);
    private void EchoReset_Click(object sender,RoutedEventArgs e)=>SetEchoLevel(0);
    private void SetEchoLevel(int level)
    {
        if(_busy||_closing)return;level=Math.Clamp(level,-3,3);if(level==_voice.EchoLevel)return;
        try
        {
            var value=_voice with{EchoLevel=level};value.Save(VoiceSettingsPath);_voice=value;
            _phone.Mixer.EchoLevel=level;_localInput.Mixer.EchoLevel=level;
        }
        catch(Exception error){ShowError(error);}
        Render(_player.State);
    }
    private void Volume_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(_updating||_player==null)return;
        _volume=Math.Round(e.NewValue); VolumeLabel.Text=$"{_volume:0}%"; VolumeIcon.Kind=_volume==0?"mute":"volume";
        _volumeTimer.Stop(); _volumeTimer.Start();
    }
    private void Mute_Click(object sender,RoutedEventArgs e)
    {
        if(VolumeSlider.Value>0){ _oldVolume=VolumeSlider.Value; VolumeSlider.Value=0; }
        else VolumeSlider.Value=_oldVolume>0?_oldVolume:100;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if(_closing)return;
        try { await action(); }
        catch(Exception error){ ShowError(error); }
    }
    private void Render(PlaybackState state)
    {
        if(_closing)return;
        RecordPlayback(state);
        _updating=true;
        if(!ToggleStatus.IsChanging(SubtitlesToggle)){SubtitlesToggle.IsChecked=_voice.SubtitlesEnabled;ToggleStatus.Complete(SubtitlesToggle,_voice.SubtitlesEnabled);}
        if(!ToggleStatus.IsChanging(AutoPlayNextToggle)){AutoPlayNextToggle.IsChecked=_voice.AutoPlayNext;ToggleStatus.Complete(AutoPlayNextToggle,_voice.AutoPlayNext);}
        bool loaded=state.Loaded&&!_busy;
        PauseButton.IsEnabled=!_busy&&(_songFlow==SongFlow.Results||state.Loaded&&!state.Ended&&_songFlow==SongFlow.Playing||(!state.Loaded||_songFlow==SongFlow.Idle)&&_queue.PendingCount>0);
        CutButton.IsEnabled=!_busy&&state.Loaded&&!state.Ended&&_songFlow==SongFlow.Playing;
        QueueList.IsEnabled=!_busy;
        RestartButton.IsEnabled=loaded||_songFlow==SongFlow.Transition;
        OriginalButton.IsEnabled=InstrumentalButton.IsEnabled=!_busy;
        KeyDownButton.IsEnabled=loaded&&state.Key>-6; KeyUpButton.IsEnabled=loaded&&state.Key<6; KeyResetButton.IsEnabled=loaded;
        PlayIcon.Kind=_songFlow==SongFlow.Results?"next":state.Loaded&&!state.Paused&&!state.Ended&&_songFlow==SongFlow.Playing?"pause":"play";
        PauseButton.ToolTip=PlayIcon.Kind=="next"?"下一首":PlayIcon.Kind=="pause"?"暫停":"播放";
        AutomationProperties.SetName(PauseButton,(string)PauseButton.ToolTip);
        KeyResetButton.Content=state.Key==0?"正常調":state.Key>0?$"+{state.Key}":state.Key.ToString();
        EchoDownButton.IsEnabled=!_busy&&_voice.EchoLevel>-3;EchoUpButton.IsEnabled=!_busy&&_voice.EchoLevel<3;EchoResetButton.IsEnabled=!_busy;
        EchoResetButton.Content=_voice.EchoLevel==0?"正常":_voice.EchoLevel>0?$"+{_voice.EchoLevel}":_voice.EchoLevel.ToString();
        OriginalButton.Style=(Style)FindResource(_queue.Mode=="original"?"SelectedButton":typeof(Button));
        InstrumentalButton.Style=(Style)FindResource(_queue.Mode=="instrumental"?"SelectedButton":typeof(Button));
        OriginalButton.Foreground=Brush(_queue.Mode=="original"?"Accent":"Sub"); OriginalButton.Background=Brush(_queue.Mode=="original"?"Tint":"Raised");
        InstrumentalButton.Foreground=Brush(_queue.Mode=="instrumental"?"Accent":"Sub"); InstrumentalButton.Background=Brush(_queue.Mode=="instrumental"?"Tint":"Raised");
        if(state.Loaded&&!_volumeTimer.IsEnabled&&!_busy)_volume=state.Volume;
        VolumeSlider.Value=_volume; VolumeLabel.Text=$"{_volume:0}%"; VolumeIcon.Kind=_volume==0?"mute":"volume";
        _updating=false;
        RenderMicrophoneVolume();
        RenderScoring();
        if(QueueSignature(state)!=_queueSignature)RefreshQueue();
        if(state.Ended&&state.Loaded&&_songFlow==SongFlow.Playing&&!_busy&&_endedGeneration!=_generation)
        {
            _endedGeneration=_generation;
            _=RunAsync(()=>FinishSongAsync());
        }
    }
    private static Brush Brush(string key)=>(Brush)Application.Current.FindResource(key);
    private void Notify(string message) { ToastText.Text=message; Toast.Visibility=Visibility.Visible; _toastTimer.Stop(); _toastTimer.Start(); }
    private void ShowError(Exception error) { Notify(error.Message); Log(error.ToString()); }
    private void Log(string message)
    {
        lock(_logLock)
        {
            string folder=Path.Combine(_store.DataDirectory,"artifacts"); Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder,"player.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }
    private void Fullscreen_Click(object sender,RoutedEventArgs e)=>SetFullscreen(!_fullscreen);
    private async void Video_SizeChanged(object sender,SizeChangedEventArgs e)
    {
        if(_player==null||_queue?.CurrentSong==null||_busy||_closing)return;
        await RunAsync(SetSongCaptionAsync);
    }
    private void SetFullscreen(bool enabled)
    {
        if(_fullscreen==enabled)return;
        if(enabled)
        {
            _previousWindowState=WindowState; _previousBounds=RestoreBounds; _previousChrome=WindowChrome.GetWindowChrome(this);
            WindowState=WindowState.Normal; WindowChrome.SetWindowChrome(this,null); ResizeMode=ResizeMode.NoResize; WindowState=WindowState.Maximized;
        }
        else
        {
            WindowState=WindowState.Normal; WindowChrome.SetWindowChrome(this,_previousChrome); ResizeMode=ResizeMode.CanResize;
            Left=_previousBounds.Left; Top=_previousBounds.Top; Width=_previousBounds.Width; Height=_previousBounds.Height; WindowState=_previousWindowState;
        }
        _fullscreen=enabled; HeaderRow.Height=new GridLength(enabled?0:48); Header.Visibility=enabled?Visibility.Collapsed:Visibility.Visible;
        Catalog.Visibility=enabled?Visibility.Collapsed:Visibility.Visible; CatalogColumn.Width=enabled?new GridLength(0):new GridLength(3,GridUnitType.Star);
        VideoRow.Height=new GridLength(1,GridUnitType.Star);
        Performance.Margin=enabled?new Thickness(0):new Thickness(0,0,20,0); VideoFrame.Fullscreen=enabled; VideoFrame.InvalidateMeasure();
        FullscreenIcon.Kind=enabled?"collapse":"expand"; FullscreenButton.ToolTip=enabled?"視窗化":"全螢幕";
        AutomationProperties.SetName(FullscreenButton,(string)FullscreenButton.ToolTip); WriteDiagnostics();
    }
    private void TitleBar_MouseDown(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left||FindButton(e.OriginalSource as DependencyObject))return;
        if(e.ClickCount==2)Maximize_Click(sender,e); else DragMove();
    }
    private static bool FindButton(DependencyObject? element)
    {
        while(element!=null){ if(element is Button)return true; element=VisualTreeHelper.GetParent(element); } return false;
    }
    private void Minimize_Click(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    private void Maximize_Click(object sender,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private async void Window_KeyDown(object sender,KeyEventArgs e)
    {
        if(e.OriginalSource is TextBoxBase or ComboBox)return;
        string? action=e.Key switch { Key.F11=>"fullscreen", Key.Escape when _fullscreen=>"exit-fullscreen", Key.Space when e.OriginalSource is not ButtonBase and not Slider=>"pause", _=>null };
        if(Keyboard.Modifiers==ModifierKeys.Control)action=e.Key switch { Key.D1=>"original", Key.D2=>"instrumental", Key.Up=>"key-up", Key.Down=>"key-down", Key.D0=>"key-reset", _=>action };
        if(action==null)return; e.Handled=true; await HandleShortcutAsync(action);
    }
    private Task HandleShortcutAsync(string action)=>RunAsync(async ()=>
    {
        switch(action)
        {
            case "fullscreen": SetFullscreen(!_fullscreen);break;
            case "exit-fullscreen": SetFullscreen(false);break;
            case "pause":await PauseAsync();break;
            case "original":await ModeAsync("original");break;
            case "instrumental":await ModeAsync("instrumental");break;
            case "key-up":if(!_busy)await _player.ChangeKeyAsync(1);break;
            case "key-down":if(!_busy)await _player.ChangeKeyAsync(-1);break;
            case "key-reset":if(!_busy)await _player.ResetKeyAsync();break;
        }
    });
    private void WriteDiagnostics()
    {
        if(_closing)return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticsPath)!);
            nint handle=new WindowInteropHelper(this).Handle; GetWindowRect(handle,out var bounds);
            string temporary=_diagnosticsPath+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(new
            {
                Timestamp=DateTimeOffset.Now, AppProcessId=Environment.ProcessId, MainWindow=handle.ToInt64(), IsVisible, IsLoaded,
                NativeVisible=IsWindowVisible(handle), NativeIconic=IsIconic(handle), Bounds=new {bounds.Left,bounds.Top,bounds.Right,bounds.Bottom},
                VideoWindow=VideoHost.VideoHandle.ToInt64(), MpvProcessId=_player.ProcessId, _player.PipeName, Fullscreen=_fullscreen,
                WindowStyle=WindowStyle.ToString(), WindowState=WindowState.ToString(),VideoWidth=VideoHost.ActualWidth,VideoHeight=VideoHost.ActualHeight,
                State=_player.State, Queue=_queue.Snapshot(), _queue.CurrentId, CurrentSong=_queue.CurrentSong, DataDirectory=_store.DataDirectory,
                Search=SearchBox.Text, SearchTotal=_searchTotal, DisplayedSongs=_songs.Count
                ,Scoring=new {Enabled=_voice.ScoringEnabled,Flow=_songFlow.ToString(),HasReference=_reference!=null,Score=ScoringFailed?null:_finalScore??_scoreSession?.Result(CurrentPosition),Error=_scoringError.Length>0?_scoringError:PhoneScoringError,MicrophoneActive=_microphone!=null,Voice=_voice}
            },new JsonSerializerOptions {WriteIndented=true}));
            File.Move(temporary,_diagnosticsPath,true);
        }
        catch(IOException){ }
    }
    private async Task CaptureAsync(string path)
    {
        await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(this);
        var drawing=new DrawingVisual();
        using(var dc=drawing.RenderOpen())
        {
            dc.DrawImage(bitmap,new Rect(0,0,ActualWidth,ActualHeight));
            if(_player.State.Loaded&&VideoHost.Visibility==Visibility.Visible)
            {
                string frame=Path.ChangeExtension(path,"video.png");await _player.CaptureFrameAsync(frame);
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.UriSource=new Uri(frame);image.EndInit();image.Freeze();
                var videoBounds=new Rect(VideoHost.TranslatePoint(new Point(),this),new Size(VideoHost.ActualWidth,VideoHost.ActualHeight));
                dc.PushClip(new RectangleGeometry(videoBounds,11,11));dc.DrawImage(image,videoBounds);dc.Pop();
            }
        }
        bitmap.Render(drawing);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);png.Save(output);
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowBounds { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint hwnd,out WindowBounds bounds);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint hwnd);
    private async void Window_Closing(object? sender,CancelEventArgs e)
    {
        if(_closed)return; e.Cancel=true; if(_closing)return; _closing=true;
        _searchTimer.Stop(); _volumeTimer.Stop(); _toastTimer.Stop(); _diagnosticsTimer.Stop();_scoreTimer.Stop(); IsEnabled=false;
        _transitionCancellation?.Cancel();_referenceCancellation?.Cancel();
        async Task Cleanup(Func<Task> action){try{await action();}catch(OperationCanceledException){}catch(Exception error){Log(error.ToString());}}
        await Cleanup(async ()=>{if(_advanceCompletion!=null)await _advanceCompletion.Task;if(_referenceOperation!=null)await _referenceOperation;});
        _phone.Mixer.SuppressMonitoring=_localInput.Mixer.SuppressMonitoring=true;
        await Cleanup(()=>_player.SetPausedAsync(true));RecordPlayback(_player.State);
        await Cleanup(StopMicrophoneAsync);
        await Cleanup(()=>EndPerformanceAsync(false));
        await Cleanup(()=>Task.WhenAll(_recordingTasks));
        await Cleanup(()=>_localInput.DisposeAsync().AsTask());
        await Cleanup(()=>_phone.DisposeAsync().AsTask());
        await Cleanup(()=>_processingQueue.DisposeAsync().AsTask());
        await Cleanup(()=>{_queue.Save();return Task.CompletedTask;});
        await Cleanup(()=>_player.DisposeAsync().AsTask());
        _closed=true; _ = Dispatcher.BeginInvoke(Close);
    }
}

public sealed class SongRow(Song song,int rank,bool inQueue)
{
    public Song Song=>song;
    public int Rank=>rank;
    public bool IsQueued=>inQueue;
    public Brush TitleBrush=>(Brush)Application.Current.FindResource(inQueue?"Accent":"Text");
    public Brush CoverBrush=>Covers.For(song.Id);
}
public sealed class QueueRow(QueueEntry entry,int order,string[] pending,PlaybackState state,bool stopped,PerformanceResult? performance=null)
{
    public bool CanReview=>entry.Status=="played"&&performance is{Skipped:false};
    public string ReviewHint=>performance?.Skipped==true?"本次已切歌，未保留評分與錄音":CanReview?"回顧演唱":"演唱結束後可回顧";
    public string Id=>entry.Id;
    public Song Song=>entry.Song;
    public string Status=>entry.Status;
    public string Order=>order.ToString("00");
    public bool CanUp=>Array.IndexOf(pending,Id)>0;
    public bool CanDown=>Array.IndexOf(pending,Id)>=0&&Array.IndexOf(pending,Id)<pending.Length-1;
    public Visibility ReorderVisible=>Status=="pending"?Visibility.Visible:Visibility.Collapsed;
    public Visibility RemoveVisible=>Status=="pending"?Visibility.Visible:Visibility.Collapsed;
    public string Badge=>Status=="played"?"已播放":Status=="playing"?(stopped?"已停止":state.Paused?"已暫停":"播放中"):pending.FirstOrDefault()==Id?"下一首":"";
    public Visibility BadgeVisible=>Badge.Length>0?Visibility.Visible:Visibility.Collapsed;
    public Brush RowBrush=>(Brush)Application.Current.FindResource(Status=="played"?"Sub":"Accent");
    public Brush TitleBrush=>(Brush)Application.Current.FindResource(Status=="played"?"Sub":"Text");
    public Brush BadgeBrush=>(Brush)Application.Current.FindResource(Status=="played"?"Raised":"Tint");
    public Brush CoverBrush=>Covers.For(Song.Id);
    public double CoverOpacity=>Status=="played"?0.45:1;
}
internal static class Covers
{
    public static Brush For(string id)
    {
        byte value=(byte)(id.Sum(c=>(int)c)%35);
        return new LinearGradientBrush(Color.FromRgb((byte)(74+value),(byte)(96+value),(byte)(113+value)),Color.FromRgb(30,49,61),45);
    }
}
