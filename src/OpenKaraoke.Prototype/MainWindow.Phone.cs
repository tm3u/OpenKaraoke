using System.Windows;
using System.IO;
using OpenKaraoke.Phone;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private sealed class PhoneInputState(PhoneChannel channel)
    {
        public PhoneChannel Channel=channel;

        public PhoneCapture? Capture;
        public SongScoreSession? Session;
        public int Version=channel.Version;
        public bool Failed;
    }
    private PhoneHost _phone=null!;
    private LocalMicrophone _localInput=null!;
    private int _oldLocalMicrophoneVolume=100,_oldPhoneMicrophoneVolume=100;
    private readonly Dictionary<int,PhoneInputState> _phoneInputs=[];
    private readonly HashSet<int> _phoneScoreFailures=[];
    private string PhoneScoringError=>_phoneScoreFailures.Count==0?"":$"手機 {string.Join("、",_phoneScoreFailures.Order())} 麥克風分析失敗，本次評分資料不完整；請重新連線或重唱。";
    private bool ScoringFailed=>_microphoneFailed||_phoneScoreFailures.Count>0;

    private bool PhoneScoring=>_voice.DeviceId=="phone";

    private void InitializePhone()
    {
        bool verify=Environment.GetCommandLineArgs().Contains("--verify-phone");
        _phone=verify?new PhoneHost(System.IO.Path.Combine(_store.DataDirectory,"phone"),0,0,true,false):new PhoneHost(System.IO.Path.Combine(_store.DataDirectory,"phone"));
        _localInput=new LocalMicrophone(!Environment.GetCommandLineArgs().Any(a=>a.StartsWith("--verify-",StringComparison.Ordinal)));
        _phone.Mixer.EchoLevel=_voice.EchoLevel;_localInput.Mixer.EchoLevel=_voice.EchoLevel;
        _phone.Changed+=RefreshVolumeControls;_localInput.Changed+=RefreshVolumeControls;
        _localInput.Failed+=error=>
        {
            if(_testingMicrophone)return;
            Dispatcher.BeginInvoke(()=>{if(_closing||PhoneScoring||_testingMicrophone)return;_microphoneFailed=true;_scoringError="電腦麥克風收音或播放失敗";Log(error.ToString());RenderScoring();ShowError(new IOException(_scoringError,error));});
        };
        _phone.Mixer.Changed+=()=>Dispatcher.BeginInvoke(()=>{if(!_closing)RenderScoring();});

    }
    private void RefreshVolumeControls()=>Dispatcher.BeginInvoke(()=>{if(!_closing)RenderMicrophoneVolume();});
    private void MicrophoneMute_Click(object sender,RoutedEventArgs e)
    {
        if(_updating||_closing||!MicrophoneVolumeSlider.IsEnabled)return;
        MicrophoneVolumeSlider.Value=MicrophoneVolumeSlider.Value>0?0:PhoneScoring?_oldPhoneMicrophoneVolume:_oldLocalMicrophoneVolume;
    }
    private void MicrophoneVolume_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(_updating||_closing||_phone==null||_localInput==null)return;
        int volume=(int)Math.Clamp(Math.Round(e.NewValue),0,100);
        var value=PhoneScoring?_voice with{PhoneMonitorVolume=volume}:_voice with{LocalVolume=volume};
        if(value==_voice)return;
        try
        {
            value.Save(VoiceSettingsPath);_voice=value;
            if(PhoneScoring)_phone.SetMonitorVolume(value.PhoneMonitorVolume);
            else _localInput.Audio.Configure(_localInput.Audio.Muted,value.LocalVolume);
        }
        catch(Exception error){ShowError(error);}
        RenderMicrophoneVolume();
    }
    private void RenderMicrophoneVolume()
    {
        if(_phone==null||_localInput==null)return;
        bool updating=_updating;_updating=true;
        int volume=PhoneScoring?_voice.PhoneMonitorVolume:_voice.LocalVolume;
        if(volume>0){if(PhoneScoring)_oldPhoneMicrophoneVolume=volume;else _oldLocalMicrophoneVolume=volume;}
        MicrophoneVolumeSlider.Value=volume;MicrophoneVolumeLabel.Text=$"{volume}%";
        MicrophoneIcon.Kind=volume==0?"mic-off":"mic";
        bool changing=_phone.IsChanging||_localInput.IsChanging;
        MicrophoneMuteButton.IsEnabled=MicrophoneVolumeSlider.IsEnabled=!changing&&!_closing;
        System.Windows.Automation.AutomationProperties.SetName(MicrophoneMuteButton,volume==0?"取消麥克風靜音":"麥克風靜音");
        _updating=updating;
    }
    private async Task SyncPhoneMicrophonesAsync(bool wanted)
    {
        if(_microphone!=null){await _microphone.DisposeAsync();_microphone=null;}
        foreach(var channel in _phone.Mixer.Channels)
        {
            if(!_phoneInputs.TryGetValue(channel.Slot,out var state)||state.Channel.Id!=channel.Id)
            {
                if(state!=null)await StopPhoneCaptureAsync(state);
                state=new(channel);_phoneInputs[channel.Slot]=state;
            }
            if(state.Version!=channel.Version){await StopPhoneCaptureAsync(state);state.Version=channel.Version;state.Failed=false;}

            if(!wanted||!channel.Audio.Connected||_scoreSession==null||state.Failed){await StopPhoneCaptureAsync(state);continue;}
            if(state.Capture!=null)continue;
            var capture=new PhoneCapture(channel.Audio);state.Capture=capture;var session=_scoreSession!;state.Session=session;int generation=_generation,delay=_voice.DelayMilliseconds;
            capture.Frame+=(frame,timestamp)=>
            {
                if(state.Capture!=capture||generation!=_generation)return;
                int key;lock(_clockGate){int at=_keyChanges.FindLastIndex(change=>change.Time<=timestamp-delay/1000d);key=at>=0?_keyChanges[at].Key:0;}
                session.Add(frame with{Time=PositionAt(timestamp)-delay/1000d},key,channel.Id);
            };
            capture.Failed+=error=>Dispatcher.BeginInvoke(()=>{if(_closing||state.Capture!=capture)return;RecordPhoneFailure(state,error);RenderScoring();});
            try{await capture.StartAsync("phone",_references.Model);}
            catch(Exception error){if(channel.Audio.Connected)RecordPhoneFailure(state,error);await StopPhoneCaptureAsync(state);}
        }
    }
    private void RecordPhoneFailure(PhoneInputState state,Exception error)
    {
        state.Failed=true;
        if(ReferenceEquals(state.Session,_scoreSession)&&_scoreSession!=null&&_phoneScoreFailures.Add(state.Channel.Slot))Log(error.ToString());
    }
    private async Task StopPhoneCaptureAsync(PhoneInputState state)
    {
        var capture=state.Capture;if(capture==null)return;
        await capture.DisposeAsync();
        if(capture.Error!=null)RecordPhoneFailure(state,capture.Error);
        if(state.Capture==capture)state.Capture=null;
    }
    private void ResetPhoneInputs()
    {

        foreach(var state in _phoneInputs.Values){state.Failed=false;}
    }
    private void ResetScoringSessions()
    {_phoneScoreFailures.Clear();if(_reference!=null)_scoreSession=new(_reference,_voice.Difficulty);ResetPhoneInputs();}
    private void RenderPhoneScores()
    {
        if(!PhoneScoring||!_voice.ScoringEnabled)return;
        if(_phoneScoreFailures.Count==0&&_phone.Mixer.ConnectedCount==0&&_songFlow!=SongFlow.Results)
        {ScoringMessage.Text="尚未連接手機麥克風";ScoringMessage.Visibility=Visibility.Visible;}
    }
}


