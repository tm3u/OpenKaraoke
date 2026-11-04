using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenKaraoke.Player;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private enum SongFlow { Idle, Playing, Results, Transition }
    private SongFlow _songFlow;
    private ScoringReferenceService _references = null!;
    private VoiceSettings _voice = new();
    private string VoiceSettingsPath => Path.Combine(_store.DataDirectory, "voice-settings.json");
    private ReferenceTrack? _reference;
    private SongScoreSession? _scoreSession;
    private ScoreResult? _finalScore;
    private string _scoringError = "";
    private bool _referenceLoading, _settingsOpen, _microphoneFailed, _microphoneSyncing;
    private readonly SemaphoreSlim _microphoneGate = new(1, 1);
    private IMicrophoneCapture? _microphone;
    private CancellationTokenSource? _referenceCancellation, _transitionCancellation;
    private Task? _referenceOperation;
    private TaskCompletionSource? _advanceCompletion;
    private readonly DispatcherTimer _scoreTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private double _transitionDeadline;
    private readonly object _clockGate = new();
    private double _clockPosition, _clockStamp;
    private bool _clockRunning;
    private int _scoringKey;
    private readonly List<(double Time,int Key)> _keyChanges = [(0,0)];

    private void InitializeScoring()
    {
        CudaLibraries.DirectoryPath=System.IO.Path.Combine(_aiRuntime.DirectoryPath,"python","Lib","site-packages","torch","lib");
        _ = PitchAcceleration.PrepareAsync(_references.Model);
        _voice = VoiceSettings.Load(VoiceSettingsPath);
        _updating = true; ScoringToggle.IsChecked = _voice.ScoringEnabled;ToggleStatus.Complete(ScoringToggle,_voice.ScoringEnabled);
        SubtitlesToggle.IsChecked = _voice.SubtitlesEnabled; ToggleStatus.Complete(SubtitlesToggle, _voice.SubtitlesEnabled); _updating = false;
        _scoreTimer.Tick += async (_, _) =>
        {
            if (_closing) return;
            RenderScoring();
            if(_songFlow==SongFlow.Results&&_resultsDeadline>0&&MicrophoneCapture.Now>=_resultsDeadline&&!_busy&&!_reviewOpen)
            {
                _resultsDeadline=0;
                if(_voice.AutoPlayNext)await RunAsync(AdvanceAsync);
            }
            if (_microphoneSyncing) return;
            _microphoneSyncing = true;
            try { await SyncMicrophoneAsync(); }
            catch (Exception error) { _microphoneFailed = true; _scoringError = "無法使用麥克風"; Log(error.ToString()); }
            finally { _microphoneSyncing = false; }
        };
        Loaded += (_, _) => _scoreTimer.Start();
        MainArea.SizeChanged += (_, _) => UpdateVideoLimit();
    }

    private void UpdatePlaybackClock(PlaybackState state)
    {
        lock (_clockGate)
        {
            bool running = state.Loaded && !state.Paused && !state.Ended && !_stopped;
            if (_clockPosition != state.Position || running != _clockRunning)
            { _clockPosition = state.Position; _clockStamp = MicrophoneCapture.Now; _clockRunning = running; }
            if(_scoringKey!=state.Key)
            {
                _scoringKey = state.Key;_keyChanges.Add((MicrophoneCapture.Now,state.Key));
                if(_keyChanges.Count>256)_keyChanges.RemoveRange(0,128);
            }
        }
    }
    private double PositionAt(double timestamp)
    {
        lock (_clockGate) return Math.Max(0, _clockPosition + (_clockRunning ? timestamp - _clockStamp : Math.Min(0, timestamp - _clockStamp)));
    }
    private double CurrentPosition => _player.State.Paused || _player.State.Ended ? _player.State.Position : Math.Min(_player.State.Duration, PositionAt(MicrophoneCapture.Now));

    private async Task PrepareScoringAsync()
    {
        _referenceCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _referenceCancellation = cancellation;
        _reference = null; _scoreSession = null; _finalScore = null; _scoringError = ""; _microphoneFailed = false;
        _phoneScoreFailures.Clear();ResetPhoneInputs();
        if (!_voice.ScoringEnabled || _queue.CurrentSong is not {} song) { RenderScoring(); return; }
        _referenceLoading = true; RenderScoring();
        try
        {
            var operation = _references.LoadAsync(song, cancellation.Token); _referenceOperation=operation;
            var reference = await operation;
            if (cancellation.IsCancellationRequested || _closing) return;
            _reference = reference; _scoreSession = reference == null ? null : new SongScoreSession(reference,_voice.Difficulty);
            ResetPhoneInputs();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!cancellation.IsCancellationRequested) { _scoringError = "無法讀取評分資料"; Log(error.ToString()); } }
        finally
        {
            if (_referenceCancellation == cancellation) { _referenceLoading = false; RenderScoring(); }
        }
    }

    private async void Scoring_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _references == null || _closing) return;
        if(ToggleStatus.IsChanging(ScoringToggle))
        {_updating=true;ScoringToggle.IsChecked=ToggleStatus.GetTargetEnabled(ScoringToggle);_updating=false;return;}
        bool enabled=ScoringToggle.IsChecked==true,saved=false;var original=_voice;
        ToggleStatus.Begin(ScoringToggle,enabled);
        try
        {
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
            var value=_voice with{ScoringEnabled=enabled};value.Save(VoiceSettingsPath);_voice=value;saved=true;
            await StopMicrophoneAsync();
            if (!_voice.ScoringEnabled)
            {
                _referenceCancellation?.Cancel();
            }
            else if (_reference == null) await PrepareScoringAsync();
        }
        catch(Exception error)
        {
            _voice=_voice with{ScoringEnabled=original.ScoringEnabled};
            if(saved)try{_voice.Save(VoiceSettingsPath);}catch(Exception restore){Log(restore.ToString());}
            ShowError(error);
        }
        finally{ToggleStatus.Complete(ScoringToggle,_voice.ScoringEnabled);RenderScoring();}
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsOpen || _closing) return;
        await RunAsync(async () =>
        {
            _settingsOpen = true;
            try
            {
                await StopMicrophoneAsync();
                var dialog = new VoiceSettingsDialog(this, _voice, _references.Model,_phone,_localInput,_store.DataDirectory,value=>
                {
                    value.Save(VoiceSettingsPath);_voice=value;
                    _microphoneFailed=false;_scoringError="";
                },testing=>_testingMicrophone=testing,error=>
                {if(!PhoneScoring){_microphoneFailed=true;_scoringError="無法恢復原本的收音裝置";}ShowError(error);},applyDisplaySettings:ApplySubtitleSettingsAsync);
                if (dialog.ShowDialog() == true)
                {
                    _voice = dialog.Value;
                    _microphoneFailed = false; _scoringError = "";
                }
            }
            finally { _settingsOpen = false;RenderMicrophoneVolume();RenderScoring(); }
        });
    }

    private async Task SyncMicrophoneAsync()
    {
        await _microphoneGate.WaitAsync();
        try
        {
            var state = _player.State;
            bool active = !_closing && !_settingsOpen && !_busy && !_stopped && _songFlow == SongFlow.Playing&&_voice.ScoringEnabled&&state.Loaded&&!state.Paused&&!state.Ended;
            if(PhoneScoring){await SyncPhoneMicrophonesAsync(active&&_reference!=null);return;}
            foreach(var phone in _phoneInputs.Values)await StopPhoneCaptureAsync(phone);
            bool wanted=active&&_scoreSession!=null&&!_microphoneFailed&&_localInput.Running;
            if (!wanted) { await StopMicrophoneCoreAsync(); return; }
            if (_microphone != null) return;
            var capture = new OpenKaraoke.Phone.PhoneCapture(_localInput.Audio,"電腦麥克風"); var session = _scoreSession!;
            int generation = _generation, delay = _voice.DelayMilliseconds;
            _microphone = capture;
            capture.Frame += (frame, timestamp) =>
            {
                if (_microphone != capture || generation != _generation) return;
                int key;lock(_clockGate){int at=_keyChanges.FindLastIndex(change=>change.Time<=timestamp-delay/1000d);key=at>=0?_keyChanges[at].Key:0;}
                session.Add(frame with { Time = PositionAt(timestamp) - delay / 1000d }, key);
            };
            capture.Failed += error => Dispatcher.BeginInvoke(() =>
            {
                if (_microphone != capture || _closing) return;
                _microphoneFailed = true; _scoringError = _voice.DeviceId=="phone"?error.Message:"無法使用麥克風"; Log(error.ToString()); RenderScoring();
            });
            try { await capture.StartAsync(_voice.DeviceId, _references.Model); }
            catch (Exception error)
            {
                _microphoneFailed = true; _scoringError = _voice.DeviceId=="phone"?error.Message:"未連接麥克風或無法存取"; Log(error.ToString());
                await StopMicrophoneCoreAsync();
            }
        }
        finally { _microphoneGate.Release(); }
    }
    private async Task StopMicrophoneAsync()
    {
        await _microphoneGate.WaitAsync();
        try { await StopMicrophoneCoreAsync(); }
        finally { _microphoneGate.Release(); }
    }
    private async Task StopMicrophoneCoreAsync()
    {
        foreach(var phone in _phoneInputs.Values)await StopPhoneCaptureAsync(phone);
        var capture = _microphone;
        if (capture == null) return;
        try { await capture.DisposeAsync(); }
        finally { if (_microphone == capture) _microphone = null; }
    }

    private async Task FinishSongAsync(bool skipped=false)
    {
        if(_busy||_closing||_songFlow!=SongFlow.Playing||!_player.State.Loaded)return;
        _busy = true;
        _endedGeneration=_generation;
        _advanceCompletion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if(skipped){await _player.SetPausedAsync(true);RecordPlayback(_player.State);}
            await StopMicrophoneAsync();
            if(skipped)await DiscardPerformanceAsync();else await EndPerformanceAsync(true);
            _finalScore = _finishedPerformance?.Score;
            _queue.Complete(); _songFlow = SongFlow.Results;
            _resultsDeadline=_voice.AutoPlayNext?MicrophoneCapture.Now+ResultsDisplaySeconds:0;
        }
        finally { _busy = false;_advanceCompletion.TrySetResult(); RefreshQueue(); Render(_player.State); }
    }

    private async Task CancelTransitionAsync()
    {
        if (_songFlow != SongFlow.Transition) return;
        _transitionCancellation?.Cancel();
        if (_advanceCompletion != null) await _advanceCompletion.Task;
        _songFlow = SongFlow.Playing;
    }

    private void UpdateVideoLimit()
    {
        if (_references == null) return;
        VideoFrame.MaxHeight = _fullscreen ? double.PositiveInfinity
            : Math.Max(120, MainArea.ActualHeight - (ScoringBar.IsVisible ? ScoringBar.ActualHeight + 8 : 0));
    }

    private void RenderScoring()
    {
        if (_closing || _references == null) return;
        bool enabled = _voice.ScoringEnabled;
        if(!ToggleStatus.IsChanging(ScoringToggle))
        {_updating=true;ScoringToggle.IsChecked=enabled;ToggleStatus.Complete(ScoringToggle,enabled);_updating=false;}
        bool idle=_songFlow==SongFlow.Idle||!_player.State.Loaded;
        ScoringBar.Visibility = enabled&&!idle&&_songFlow!=SongFlow.Results ? Visibility.Visible : Visibility.Collapsed;
        string scoringError = _scoringError.Length > 0 ? _scoringError : PhoneScoringError;
        string Number(double? value) => value?.ToString("0") ?? "—";
        bool ready=enabled&&_reference!=null&&!ScoringFailed&&!idle&&_songFlow!=SongFlow.Results;
        ScoreBody.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        bool building = _referenceLoading;
        ScoringMessage.Text = !enabled ? "" : building ? "讀取評分基準中" : scoringError.Length > 0 ? scoringError
            : _queue.CurrentSong == null ? "目前沒有播放任何歌曲" : _reference == null ? "這首歌缺少評分基準，請重新匯入" : "";
        ScoringMessage.Visibility=ScoringMessage.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        ScoringStateIcon.Visibility=enabled&&(building||scoringError.Contains("麥克風"))?Visibility.Visible:Visibility.Collapsed;
        ScoringStateIcon.Kind=building?"audio-lines":"mic-off";
        MelodyGuide.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        MelodyGuide.Reference = _reference; MelodyGuide.Position = _songFlow==SongFlow.Transition?Math.Min(0,MicrophoneCapture.Now-_transitionDeadline):CurrentPosition; MelodyGuide.Key = _player.State.Key;
        MelodyGuide.Records=_scoreSession?.Notes()??[];
        MelodyGuide.InvalidateVisual();
        bool transition = _songFlow == SongFlow.Transition, results = _songFlow == SongFlow.Results;
        SongFlowOverlay.Visibility = transition ? Visibility.Visible : Visibility.Collapsed;
        TransitionPanel.Visibility = transition ? Visibility.Visible : Visibility.Collapsed;
        ScoreResultOverlay.Visibility = results ? Visibility.Visible : Visibility.Collapsed;
        IdleOverlay.Visibility=idle?Visibility.Visible:Visibility.Collapsed;
        VideoHost.Visibility = transition || results || idle ? Visibility.Hidden : Visibility.Visible;
        if (results)
        {
            ResultTitle.Text = _queue.CurrentSong?.Title; ResultArtist.Text = _queue.CurrentSong?.Artist;
            ResultPitch.Text = Number(_finalScore?.Pitch);
            ResultPitch.ToolTip=_finishedPerformance?.Skipped==true?"本次已切歌，未評分":scoringError.Length>0?scoringError:!_voice.ScoringEnabled?"當次未開啟評分":_reference==null?"尚未建立評分資料":_finalScore?.Pitch==null?"沒有足夠的收音資料":null;
            ResultRecordViewport.Visibility=_finishedPerformance?.Skipped==true?Visibility.Collapsed:Visibility.Visible;
            ResultOverview.Duration=_finishedPerformance?.Duration??_player.State.Duration;
            ResultOverview.Width=Math.Max(620,ResultOverview.Duration*80);
            ResultOverview.OriginalNotes=_finishedPerformance?.ReferenceNotes??[];
            ResultOverview.Records=_finishedPerformance?.Notes??[];ResultOverview.InvalidateVisual();
        }
        RenderPhoneScores();UpdateVideoLimit();
    }
}

