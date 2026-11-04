using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private sealed class TestPhone(string id,double frequency)
    {
        public string Id=id;
        public double Frequency=frequency;
        public ClientWebSocket? Socket;
        public CancellationTokenSource? Cancellation;
        public Task? Sending;
        public int Slot;
    }
    private async Task VerifyPhoneAsync(string report)
    {
        var checks=new List<object>();string folder=Path.GetDirectoryName(report)!;VoiceSettingsDialog? dialog=null;int automaticSaves=0;
        var phones=new[]{440d,466.1637615}.Select(hz=>new TestPhone(Guid.NewGuid().ToString("N"),hz)).ToArray();
        void Check(string name,bool passed,object? evidence=null){checks.Add(new{Name=name,Passed=passed,Evidence=evidence});if(!passed)throw new InvalidOperationException(name);}
        async Task Wait(Func<bool> condition)
        {var watch=Stopwatch.StartNew();while(!condition()){if(watch.Elapsed.TotalSeconds>18)throw new TimeoutException("手機介面驗收逾時。");await Task.Delay(30);}await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);}
        Task ShowModal(VoiceSettingsDialog window)
        {var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Dispatcher.BeginInvoke(()=>{try{window.ShowDialog();completion.TrySetResult();}catch(Exception e){completion.TrySetException(e);}});return completion.Task;}
        VoiceSettingsDialog Settings(VoiceSettings value)=>new(this,value,_references.Model,_phone,_localInput,_store.DataDirectory,committed=>
        {committed.Save(VoiceSettingsPath);_voice=committed;_microphoneFailed=false;_scoringError="";automaticSaves++;},testing=>_testingMicrophone=testing);
        async Task CheckTestRecording(bool phone,bool testing=true)
        {
            var previousRecording=_recording;var probe=new PerformanceCapture(Path.Combine(folder,phone?"phone-preview-recording":"pc-preview-recording"),_queue.CurrentSong!);
            var mixer=phone?_phone.Mixer:_localInput.Mixer;int outputBlocks=0;bool silent=true;
            void Observe(float[] samples,int rate,double time)=>Interlocked.Increment(ref outputBlocks);
            mixer.MonitorSamples+=Observe;
            try
            {
                probe.Update(_player.State,true,MicrophoneCapture.Now);_recording=probe;
                var output=new byte[mixer.WaveFormat.AverageBytesPerSecond/100];
                for(int block=0;block<5;block++){mixer.Read(output,0,output.Length);silent&=output.All(value=>value==0);}
            }
            finally{_recording=previousRecording;mixer.MonitorSamples-=Observe;}
            var recorded=await probe.CompleteAsync(MicrophoneCapture.Now);
            Check((phone?"手機":"電腦")+(testing?"測試監聽持續輸出，但測試人聲不進入演唱錄音":"準備連線時監聽保持靜音，人聲不進入正式錄音"),_testingMicrophone==testing&&(testing||mixer.SuppressMonitoring&&silent)&&recorded.Voices.Length==0&&recorded.Error==null,new{OutputBlocks=outputBlocks,RecordedVoiceParts=recorded.Voices.Length});
            await probe.DiscardAsync();
        }
        async Task Connect(TestPhone phone)
        {
            using var ca=X509CertificateLoader.LoadCertificate(_phone.PublicCertificate!);
            var ws=new ClientWebSocket();ws.Options.SetRequestHeader("Origin",_phone.SecureUrl);ws.Options.SetRequestHeader("Authorization","Bearer "+new Uri(_phone.MicrophoneUrl).Fragment[6..]);ws.Options.Cookies=new();ws.Options.Cookies.Add(new Uri(_phone.SecureUrl),new Cookie("openkaraoke-device",phone.Id));
            ws.Options.RemoteCertificateValidationCallback=(_,certificate,chain,errors)=>
            {if(certificate==null||(errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0)return false;using var leaf=X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());using var trust=new X509Chain();trust.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;trust.ChainPolicy.CustomTrustStore.Add(ca);trust.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;return trust.Build(leaf);};
            await ws.ConnectAsync(new Uri(_phone.SecureUrl.Replace("https:","wss:")+"/ws"),CancellationToken.None);
            byte[] hello=new byte[1024];var received=await ws.ReceiveAsync(hello,CancellationToken.None);using(var document=JsonDocument.Parse(hello.AsMemory(0,received.Count)))phone.Slot=document.RootElement.GetProperty("slot").GetInt32();
            await ws.SendAsync(Encoding.UTF8.GetBytes("{\"sampleRate\":48000}"),WebSocketMessageType.Text,true,CancellationToken.None);
            phone.Socket=ws;phone.Cancellation=new();var token=phone.Cancellation.Token;
            phone.Sending=Task.Run(async()=>
            {
                int sample=0;try{while(!token.IsCancellationRequested){var bytes=new byte[960];for(int i=0;i<480;i++)System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i*2,2),(short)(Math.Sin(2*Math.PI*phone.Frequency*sample++/48000)*10000));await ws.SendAsync(bytes,WebSocketMessageType.Binary,true,CancellationToken.None);await Task.Delay(10,token);}}catch(Exception error)when(error is OperationCanceledException or WebSocketException){}
            });
        }
        async Task Disconnect(TestPhone phone)
        {
            phone.Cancellation?.Cancel();if(phone.Sending!=null)await phone.Sending;phone.Cancellation?.Dispose();phone.Cancellation=null;phone.Sending=null;
            if(phone.Socket!=null){await phone.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure,"test",CancellationToken.None);phone.Socket.Dispose();phone.Socket=null;}
            await Wait(()=>_phone.Mixer.Channels.Single(c=>c.Id==phone.Id).Audio.Connected==false);await Task.Delay(80);
        }
        try
        {
            await using var silent=new OpenKaraoke.Player.MpvConnection(_player.PipeName);await silent.ConnectAsync();await silent.SetAsync("mute",true);_settingsOpen=true;
            var options=_voice with{PhoneEnabled=false,PhoneAddress="127.0.0.1",DeviceId="default"};_voice=options;_voice.Save(VoiceSettingsPath);await _phone.ApplyAsync(options);
            Render(_player.State);double speakerVolume=VolumeSlider.Value;
            Check("電腦模式主列只有喇叭與目前來源的麥克風音量",MicrophoneVolumeSlider.IsEnabled&&UiElements<Slider>(VolumeControls).Count()==2&&FindName("Microphone2VolumeSlider")==null);
            MicrophoneVolumeSlider.Value=42;
            Check("主列電腦麥克風音量立即生效並保存，不改喇叭音量",_localInput.Audio.Volume==42&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==42&&MicrophoneVolumeLabel.Text=="42%"&&VolumeSlider.Value==speakerVolume);
            MicrophoneMuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("點擊電腦麥克風圖示立即靜音並保存，不改喇叭音量",_localInput.Audio.Volume==0&&MicrophoneVolumeSlider.Value==0&&MicrophoneVolumeLabel.Text=="0%"&&MicrophoneIcon.Kind=="mic-off"&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==0&&VolumeSlider.Value==speakerVolume);
            MicrophoneMuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("再次點擊電腦麥克風圖示恢復靜音前音量",_localInput.Audio.Volume==42&&MicrophoneVolumeSlider.Value==42&&MicrophoneIcon.Kind=="mic"&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==42);
            string blockedVolumeSave=VoiceSettingsPath+".tmp";Directory.CreateDirectory(blockedVolumeSave);
            try
            {
                MicrophoneMuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("快速靜音保存失敗還原原音量與圖示",_localInput.Audio.Volume==42&&MicrophoneVolumeSlider.Value==42&&MicrophoneIcon.Kind=="mic"&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==42&&Toast.IsVisible);
                MicrophoneVolumeSlider.Value=10;Check("主列音量保存失敗保留原本聲音與百分比",_localInput.Audio.Volume==42&&MicrophoneVolumeSlider.Value==42&&MicrophoneVolumeLabel.Text=="42%"&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==42&&Toast.IsVisible);
            }
            finally{Directory.Delete(blockedVolumeSave);_toastTimer.Stop();Toast.Visibility=Visibility.Collapsed;}
            MicrophoneVolumeSlider.Value=100;options=_voice;
            dialog=Settings(options);var modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);
            var source=(ComboBox)dialog.FindName("MicrophoneInput");var panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            Check("選擇電腦音源時手機區塊隱藏且停用，服務未啟動",panel.Visibility==Visibility.Collapsed&&!panel.IsEnabled&&!_phone.Running&&dialog.ActualWidth==390);
            Check("設定提供語音、評分與顯示頁籤，沒有獨立手機頁籤",((TabControl)dialog.FindName("VoiceTabs")).Items.Count==3&&dialog.FindName("DisplayTab")!=null&&dialog.FindName("PhoneTab")==null);
            double localSettingsHeight=dialog.ActualHeight;
            Check("電腦來源不依賴歌曲或評分，開啟設定仍持續收音監聽",_localInput.Running&&_localInput.Audio.Connected&&_localInput.DeviceId=="default");
            ((Slider)dialog.FindName("LocalVolume")).Value=25;
            Check("電腦麥克風音量只修改草稿，未按套用不生效",_localInput.Audio.Volume==100&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==100);
            Check("語音頁籤沒有獨立來源狀態文字",dialog.FindName("SourceStatus")==null);
            int beforeTestSaves=automaticSaves;
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_localInput.Audio.Volume==25);
            Check("開始測試立即預覽音量草稿，不保存正式設定",dialog.Value.LocalVolume==100&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==100&&automaticSaves==beforeTestSaves);
            await CheckTestRecording(false);
            ((Slider)dialog.FindName("LocalVolume")).Value=0;
            Check("測試中音量降至零立即生效，不修改已保存音量",_localInput.Audio.Volume==0&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==100);
            ((Slider)dialog.FindName("LocalVolume")).Value=60;
            Check("測試中連續調整音量即時生效",_localInput.Audio.Volume==60);
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_localInput.Audio.Volume==100);
            Check("停止測試恢復正式音量，保留未套用草稿",((Slider)dialog.FindName("LocalVolume")).Value==60&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==100);
            CaptureDialog(dialog,Path.Combine(folder,"phone-disabled.png"));
            ((TextBox)dialog.FindName("DelayInput")).Text="180";
            source.SelectedValue="phone";
            Check("選擇手機即自動準備連線，暫時鎖定重複操作",ToggleStatus.IsChanging(source)&&!source.IsEnabled&&!((Button)dialog.FindName("ApplyButton")).IsEnabled&&panel.Visibility==Visibility.Visible&&!panel.IsEnabled&&UiElements<Image>(panel).All(i=>i.Source==null));
            await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.Render);
            CaptureDialog(dialog,Path.Combine(folder,"phone-preparing.png"));
            await Wait(()=>_phone.Running&&!ToggleStatus.IsChanging(source));await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("未按測試或套用即可顯示 QR Code，正式電腦收音與設定不變",!_testingMicrophone&&_localInput.Running&&!_localInput.Mixer.SuppressMonitoring&&_phone.Mixer.SuppressMonitoring&&!_phone.Mixer.OutputActive&&VoiceSettings.Load(VoiceSettingsPath)==options&&automaticSaves==0&&UiElements<Image>(panel).Count(i=>i.Source!=null)==2);
            CaptureDialog(dialog,Path.Combine(folder,"phone-auto-ready.png"));
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("只準備連線後取消也會清除暫時服務並保留正式收音",!_phone.Running&&_localInput.Running&&!_localInput.Mixer.SuppressMonitoring&&VoiceSettings.Load(VoiceSettingsPath)==options&&automaticSaves==0);
            dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);source=(ComboBox)dialog.FindName("MicrophoneInput");panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            ((Slider)dialog.FindName("LocalVolume")).Value=60;((TextBox)dialog.FindName("DelayInput")).Text="180";
            source.SelectedValue="phone";await Wait(()=>_phone.Running&&!ToggleStatus.IsChanging(source));string preparedPair=_phone.MicrophoneUrl;
            await Connect(phones[0]);await Wait(()=>_phone.Mixer.Channels.Any(c=>c.Audio.Connected&&c.Audio.Level>.01));
            Check("測試前手機可先連線，正式來源與電腦監聽不變",!_testingMicrophone&&!PhoneScoring&&!_localInput.Mixer.SuppressMonitoring&&_phone.Mixer.SuppressMonitoring&&VoiceSettings.Load(VoiceSettingsPath)==options);
            await CheckTestRecording(true,testing:false);
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>_phone.Running&&_testingMicrophone&&!ToggleStatus.IsChanging(source));await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("按測試才預覽手機收音，連線網址維持不變",_phone.MicrophoneUrl==preparedPair&&source.IsEnabled&&panel.IsEnabled&&dialog.ActualWidth==430&&System.Windows.Automation.AutomationProperties.GetItemStatus(source)==""&&dialog.FindName("SourceStatus")==null);
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>!_testingMicrophone&&!ToggleStatus.IsChanging(source));
            Check("停止測試保留 QR Code 與手機連線，還原正式電腦監聽",_phone.Running&&_phone.MicrophoneUrl==preparedPair&&_phone.Mixer.ConnectedCount==1&&_phone.Mixer.SuppressMonitoring&&!_localInput.Mixer.SuppressMonitoring&&UiElements<Image>(panel).Count(i=>i.Source!=null)==2&&VoiceSettings.Load(VoiceSettingsPath)==options);
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_testingMicrophone&&!ToggleStatus.IsChanging(source));
            Check("手機連線區位於語音控制下方，視窗隨內容加長",panel.IsVisible&&source.IsVisible&&((Button)dialog.FindName("TestMicrophoneButton")).IsVisible&&panel.TranslatePoint(new(),dialog).Y>((TextBox)dialog.FindName("DelayInput")).TranslatePoint(new Point(0,34),dialog).Y&&dialog.ActualHeight>localSettingsHeight+150);
            Check("手機區塊沒有獨立服務開關與頂端狀態列",!UiElements<CheckBox>(panel).Any()&&!UiElements<TextBlock>(panel).Any(t=>t.Name=="PhoneServiceState"||t.Text.Contains("支已連線")));
            using(var client=new HttpClient()){string setup=await client.GetStringAsync(_phone.SetupUrl);Check("選來源後不按套用，設定視窗內已可連線首次設定",dialog.IsVisible&&setup.Contains(_phone.Fingerprint)&&setup.Contains("下載項目"));}
            var saved=VoiceSettings.Load(VoiceSettingsPath);
            Check("測試來源、音量與延遲不寫入正式設定",saved==options&&dialog.Value==options&&automaticSaves==0&&_voice==options&&!UiElements<Slider>(panel).Any());
            Check("手機測試暫停正式電腦監聽，隱藏的音量草稿不保存",_localInput.Running&&_localInput.Mixer.SuppressMonitoring&&saved.LocalVolume==100&&!((Slider)dialog.FindName("LocalVolume")).IsVisible);
            await Disconnect(phones[0]);
            var network=UiElements<ComboBox>(panel).Single();string address=_phone.Address;
            network.ItemsSource=network.ItemsSource.Cast<OpenKaraoke.Phone.LanAddress>().Append(new("203.0.113.1","未套用的網路草稿")).ToArray();network.SelectedValue="203.0.113.1";
            await Wait(()=>!ToggleStatus.IsChanging(source)&&!_testingMicrophone);
            Check("測試網路失敗還原正式電腦收音且不保存草稿",!_phone.Running&&_localInput.Running&&!_localInput.Mixer.SuppressMonitoring&&VoiceSettings.Load(VoiceSettingsPath)==options);
            network.SelectedValue=address;
            await Wait(()=>_phone.Running&&!ToggleStatus.IsChanging(source));
            Check("修正網路會重新準備 QR Code，不切換正式收音或保存",!_testingMicrophone&&!_localInput.Mixer.SuppressMonitoring&&_phone.Mixer.SuppressMonitoring&&automaticSaves==0&&VoiceSettings.Load(VoiceSettingsPath)==options);
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_phone.Running&&!ToggleStatus.IsChanging(source));
            int beforeNetworkToggle=automaticSaves;source.SelectedValue="default";await Wait(()=>!_phone.Running&&!ToggleStatus.IsChanging(source));
            Check("改選電腦會停用服務、清除 QR Code 並保留語音頁籤",panel.Visibility==Visibility.Collapsed&&!panel.IsEnabled&&((TabItem)((TabControl)dialog.FindName("VoiceTabs")).Items[0]).IsSelected&&UiElements<Image>(panel).All(i=>i.Source==null));
            Check("測試中可切回電腦並預覽音量，正式設定維持原值",_localInput.Audio.Volume==60&&_testingMicrophone&&automaticSaves==beforeNetworkToggle&&VoiceSettings.Load(VoiceSettingsPath)==options);
            source.SelectedValue="phone";await Wait(()=>_phone.Running&&!ToggleStatus.IsChanging(source));await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("再次切換測試來源仍不順帶保存草稿",_phone.Address==address&&_phone.Mixer.Channels.All(c=>c.Audio.Volume==100)&&VoiceSettings.Load(VoiceSettingsPath)==options);
            network.SelectedValue=address;
            ((TextBox)dialog.FindName("DelayInput")).Text=options.DelayMilliseconds.ToString();
            var qr=UiElements<Image>(panel).Where(i=>i.Source!=null).ToArray();
            Check("啟用後顯示兩支手機狀態，不提供音量滑桿",!UiElements<Slider>(panel).Any()&&UiElements<TextBlock>(panel).Count(t=>t.Text is "手機 1" or "手機 2")==2&&UiElements<ComboBox>(panel).All(c=>c.IsVisible));
            Check("沒有靜音、輕殘響或連線設定摺疊列",!UiElements<CheckBox>(panel).Any(c=>Equals(c.Content,"靜音")||Equals(c.Content,"輕殘響"))&&!UiElements<Expander>(panel).Any(e=>Equals(e.Header,"連線設定")));
            var phoneScroll=(ScrollViewer)dialog.FindName("VoiceViewport");phoneScroll.ScrollToTop();await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("連線設定的兩個 QR Code 完整呈現",qr.Length==2&&qr.All(i=>i.ActualWidth==168&&i.TranslatePoint(new Point(0,i.ActualHeight),dialog).Y<dialog.ActualHeight));CaptureDialog(dialog,Path.Combine(folder,"phone-pairing.png"));
            Check("兩個 QR Code 並排且沒有水平裁切",qr.All(i=>i.TranslatePoint(new(),dialog).X>=0&&i.TranslatePoint(new Point(i.ActualWidth,0),dialog).X<=dialog.ActualWidth));
            double normalMaxHeight=dialog.MaxHeight;dialog.MaxHeight=Math.Min(normalMaxHeight,560);await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            var applyButton=(Button)dialog.FindName("ApplyButton");
            Check("較矮的視窗捲動語音內容，套用與取消保持可見",dialog.ActualHeight<=560.5&&phoneScroll.ScrollableHeight>0&&applyButton.TranslatePoint(new Point(0,applyButton.ActualHeight),dialog).Y<=dialog.ActualHeight&&((Button)dialog.FindName("CancelButton")).IsVisible);
            phoneScroll.ScrollToBottom();await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);CaptureDialog(dialog,Path.Combine(folder,"phone-compact.png"));
            Check("較矮的視窗仍可捲動查看兩支手機狀態",UiElements<TextBlock>(panel).Where(t=>t.Text is "手機 1" or "手機 2").All(t=>t.TranslatePoint(new(),dialog).Y>phoneScroll.TranslatePoint(new(),dialog).Y&&t.TranslatePoint(new Point(0,t.ActualHeight),dialog).Y<applyButton.TranslatePoint(new(),dialog).Y));
            dialog.MaxHeight=normalMaxHeight;phoneScroll.ScrollToTop();await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            ((TabItem)dialog.FindName("ScoringTab")).IsSelected=true;await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("切到評分頁籤恢復精簡寬度且不影響手機服務",dialog.ActualWidth==390&&_phone.Running);
            ((TabItem)((TabControl)dialog.FindName("VoiceTabs")).Items[0]).IsSelected=true;await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("切回語音恢復手機連線區與 QR Code",dialog.ActualWidth==430&&panel.IsVisible&&qr.All(i=>i.Source!=null));
            Check("兩支手機狀態位於連線設定及 QR Code 下方",UiElements<TextBlock>(panel).Where(t=>t.Text is "手機 1" or "手機 2").All(t=>t.TranslatePoint(new(),panel).Y>qr.Max(i=>i.TranslatePoint(new Point(0,i.ActualHeight),panel).Y)));
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("取消手機測試完整恢復電腦來源、音量、監聽與正式檔案",!_phone.Running&&_localInput.Running&&_localInput.Audio.Volume==100&&!_localInput.Mixer.SuppressMonitoring&&!_testingMicrophone&&dialog.Value==options&&VoiceSettings.Load(VoiceSettingsPath)==options&&automaticSaves==0);
            dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);source=(ComboBox)dialog.FindName("MicrophoneInput");source.SelectedValue="phone";
            await Wait(()=>_phone.Running&&!ToggleStatus.IsChanging(source));
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("只有按套用才正式切換並保存手機來源",_voice.DeviceId=="phone"&&VoiceSettings.Load(VoiceSettingsPath).DeviceId=="phone"&&_phone.Running&&!_localInput.Running&&automaticSaves==1);
            dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            for(int i=0;i<phones.Length;i++){await Connect(phones[i]);int count=i+1;await Wait(()=>_phone.Mixer.ConnectedCount==count);Check($"連線 {count} 支仍只有一組歌曲分數",FindName("PhoneScoresList")==null&&FindName("LivePitch")==null);}
            await Wait(()=>_phone.Mixer.ConnectedCount==2);
            Check("連線後直接以固定原始音量收音",_phone.Mixer.Channels.All(c=>!c.Audio.Muted));
            Check("手機區塊移除音量滑桿，兩支維持原始音量",!UiElements<Slider>(panel).Any()&&_phone.Mixer.Channels.All(c=>c.Audio.Volume==100));
            Check("兩支手機皆以原始音量收音",_phone.Mixer.Channels.Skip(1).All(c=>!c.Audio.Muted&&c.Audio.Volume==100));
            source=(ComboBox)dialog.FindName("MicrophoneInput");string pairedUrl=_phone.MicrophoneUrl;source.SelectedValue="default";
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_localInput.Running&&_testingMicrophone&&!ToggleStatus.IsChanging(source));
            Check("已套用手機來源可試聽電腦，手機連線與正式來源保持不變",_phone.Mixer.ConnectedCount==2&&_phone.Mixer.SuppressMonitoring&&_voice.DeviceId=="phone"&&VoiceSettings.Load(VoiceSettingsPath).DeviceId=="phone");
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("取消設定不更動兩支手機的原始收音",_phone.Mixer.Channels.All(c=>!c.Audio.Muted&&c.Audio.Volume==100));
            Check("取消電腦測試恢復原手機來源且不用重新配對",_phone.Running&&_phone.Mixer.ConnectedCount==2&&_phone.MicrophoneUrl==pairedUrl&&!_phone.Mixer.SuppressMonitoring&&!_localInput.Running&&!_testingMicrophone&&VoiceSettings.Load(VoiceSettingsPath).DeviceId=="phone"&&dialog.Value.DeviceId=="phone");
            Render(_player.State);MicrophoneVolumeSlider.Value=20;
            Check("主列一個麥克風音量同步調整兩支手機並保存",_phone.Mixer.Channels.Length==2&&_phone.Mixer.Channels.All(c=>c.Audio.Volume==20)&&VoiceSettings.Load(VoiceSettingsPath).PhoneMonitorVolume==20&&MicrophoneVolumeLabel.Text=="20%"&&VolumeSlider.Value==speakerVolume);
            MicrophoneVolumeSlider.Value=0;
            Check("麥克風音量歸零同步靜音兩支手機，保留原始收音",_phone.Mixer.Channels.All(c=>c.Audio.Volume==0&&c.Audio.Connected&&c.Audio.Level>.01)&&MicrophoneIcon.Kind=="mic-off");
            MicrophoneMuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("滑桿歸零後點擊圖示可恢復兩支手機原音量",_phone.Mixer.Channels.All(c=>c.Audio.Volume==20&&c.Audio.Connected)&&VoiceSettings.Load(VoiceSettingsPath).PhoneMonitorVolume==20&&MicrophoneVolumeLabel.Text=="20%");
            MicrophoneMuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("點擊麥克風圖示同步靜音兩支手機，不影響原始收音與喇叭",_phone.Mixer.Channels.All(c=>c.Audio.Volume==0&&c.Audio.Connected&&c.Audio.Level>.01)&&VoiceSettings.Load(VoiceSettingsPath).PhoneMonitorVolume==0&&VolumeSlider.Value==speakerVolume);
            await CaptureAsync(Path.Combine(folder,"microphone-volumes.png"));
            MicrophoneMuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("再次點擊麥克風圖示恢復兩支手機靜音前音量",_phone.Mixer.Channels.All(c=>c.Audio.Volume==20&&c.Audio.Connected)&&VoiceSettings.Load(VoiceSettingsPath).PhoneMonitorVolume==20&&MicrophoneIcon.Kind=="mic");
            MicrophoneVolumeSlider.Value=100;
            options=_voice;dialog=Settings(options);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            await Wait(()=>_phone.Mixer.Channels.Any(c=>c.Audio.Level>.01));await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            phoneScroll=(ScrollViewer)dialog.FindName("VoiceViewport");phoneScroll.ScrollToBottom();await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("手機區塊僅顯示兩支手機的個別連線狀態",UiElements<TextBlock>(panel).Count(t=>t.Text=="已連線")==2&&!UiElements<TextBlock>(panel).Any(t=>t.Text.Contains("支已連線")));
            Check("手機控制沒有額外懸停文字",UiElements<Button>(panel).All(b=>b.ToolTip==null)&&UiElements<Slider>(panel).All(s=>s.ToolTip==null));CaptureDialog(dialog,Path.Combine(folder,"phone-settings.png"));
            ((TabItem)((TabControl)dialog.FindName("VoiceTabs")).Items[0]).IsSelected=true;await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>((ProgressBar)dialog.FindName("MicrophoneLevelMeter")).Value>.1);Check("語音收音測試可監看手機輸入",true);CaptureDialog(dialog,Path.Combine(folder,"phone-voice-settings.png"));
            await CheckTestRecording(true);
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));_voice=dialog.Value;_voice.Save(VoiceSettingsPath);
            Check("手機測試按套用保留兩支連線且結束預覽",_phone.Mixer.ConnectedCount==2&&_phone.MicrophoneUrl==pairedUrl&&!_testingMicrophone&&!_phone.Mixer.SuppressMonitoring&&!_localInput.Running);
            Check("設定套用保留共用麥克風音量",_phone.Mixer.Channels.All(c=>c.Audio.Volume==100)&&VoiceSettings.Load(VoiceSettingsPath).PhoneMonitorVolume==100);
            string? current=_queue.CurrentId;await ModeAsync("original");await _player.ChangeKeyAsync(2);double position=_player.State.Position;await Task.Delay(200);
            Check("兩路手機不影響目前播放、音軌與升降調",_queue.CurrentId==current&&_player.State.Position>position&&_player.State.TrackId==1&&_player.State.Key==2&&!_player.State.Paused);
            await _player.ResetKeyAsync();_voice=_voice with{ScoringEnabled=true};int[] melody=[0,2,4,5,7,5,4,2];_reference=ReferenceTrack.FromFrames(Enumerable.Range(0,(int)(_player.State.Duration/.016)).Select(i=>new PitchFrame(i*.016,440*Math.Pow(2,melody[(int)(i*.016)%melody.Length]/12d),1)).ToArray(),_player.State.Duration);ResetScoringSessions();_settingsOpen=false;Render(_player.State);
            await Wait(()=>_phoneInputs.Count==2&&_scoreSession?.Result(CurrentPosition).CapturedFrames>30);
            Check("兩路音高分別辨識，不先混音",_scoreSession!.Trace(phones[0].Id).Length>0&&_scoreSession.Trace(phones[0].Id).All(p=>Math.Abs(p.Midi-69)<.4)&&_scoreSession.Trace(phones[1].Id).Length>0&&_scoreSession.Trace(phones[1].Id).All(p=>Math.Abs(p.Midi-70)<.4));
            await Wait(()=>_scoreSession?.Result(CurrentPosition).CapturedFrames>30);
            Check("多支手機接續累計同一首歌曲分數",ScoreBody.IsVisible&&_scoreSession?.Result(CurrentPosition).Pitch==null&&FindName("PhoneScoresList")==null);
            Check("旋律譜保留可辨識高度",MelodyGuide.ActualHeight>=110);
            Check("音高譜保留整幅卡片且沒有即時成績",ScoringBar.IsAncestorOf(MelodyGuide)&&FindName("LivePitch")==null&&FindName("ScoreMetrics")==null);
            foreach(var size in new[]{new Size(1280,720),new Size(1600,900),new Size(1920,1080),new Size(1280,960),new Size(1920,720)})
            {
                Width=size.Width;Height=size.Height;await Task.Delay(150);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
                QueueTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
                Check($"{size.Width}×{size.Height} 旋律卡片與影片、待播清單不重疊",Math.Abs(BoundsOnWindow(MelodyGuide).Width-BoundsOnWindow(ScoreBody).Width)<1&&BoundsOnWindow(VideoHost).Bottom<=BoundsOnWindow(ScoringBar).Top&&ScoringBar.ActualHeight<180&&QueueList.ActualHeight>=130,new{VideoHeight=VideoHost.ActualHeight,ScoreHeight=ScoringBar.ActualHeight,QueueHeight=QueueList.ActualHeight});
                var canvas=BoundsOnWindow(AppCanvas);
                Check($"{size.Width}×{size.Height} 自由比例視窗填滿介面且圖文等比縮放",Math.Abs(ActualWidth-size.Width)<1&&Math.Abs(ActualHeight-size.Height)<1&&Math.Abs(canvas.Width-Surface.ActualWidth)<1&&Math.Abs(canvas.Height-Surface.ActualHeight)<1&&Math.Abs(canvas.Width/AppCanvas.Width-canvas.Height/AppCanvas.Height)<.001);
                SongLibraryTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
                if(size==new Size(1280,960))await CaptureAsync(Path.Combine(folder,"window-tall.png"));
                if(size==new Size(1920,720))await CaptureAsync(Path.Combine(folder,"window-wide.png"));
            }
            Width=1600;Height=900;await Task.Delay(100);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            nint sizing=System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<WindowBounds>());
            try
            {
                bool valid=true;
                for(int edge=1;edge<=8;edge++)
                {
                    System.Runtime.InteropServices.Marshal.StructureToPtr(new WindowBounds{Left=100,Top=100,Right=900,Bottom=600},sizing,false);
                    SendSizingMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle,0x0214,edge,sizing);
                    var sized=System.Runtime.InteropServices.Marshal.PtrToStructure<WindowBounds>(sizing);
                    valid&=sized.Left==100&&sized.Top==100&&sized.Right==900&&sized.Bottom==600;
                }
                Check("八個調整方向均不再強制改寫長寬比",valid);
            }
            finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(sizing);}
            WindowState=WindowState.Maximized;await Task.Delay(180);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle,out var maximized);
            var monitor=new MonitorInformation{Size=System.Runtime.InteropServices.Marshal.SizeOf<MonitorInformation>()};GetMonitorInfo(MonitorFromWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle,2),ref monitor);
            Check("最大化填滿螢幕可用區並保留工作列",Math.Abs(maximized.Left-monitor.Work.Left)<2&&Math.Abs(maximized.Top-monitor.Work.Top)<2&&Math.Abs(maximized.Right-monitor.Work.Right)<2&&Math.Abs(maximized.Bottom-monitor.Work.Bottom)<2,new{maximized.Left,maximized.Top,maximized.Right,maximized.Bottom});
            WindowState=WindowState.Normal;Width=1600;Height=900;await Task.Delay(100);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);PositionVideo();
            var expectedVideo=VideoHost.PointToScreen(new());GetWindowRect(VideoHost.VideoHandle,out var nativeVideo);
            Check("縮放後原生影片 HWND 與介面座標一致",Math.Abs(expectedVideo.X-nativeVideo.Left)<2&&Math.Abs(expectedVideo.Y-nativeVideo.Top)<2,new{expectedVideo.X,expectedVideo.Y,nativeVideo.Left,nativeVideo.Top});
            await CaptureAsync(Path.Combine(folder,"phone-scoring.png"));QueueTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);await CaptureAsync(Path.Combine(folder,"phone-queue.png"));SongLibraryTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);var unaffected=_scoreSession;int frames=unaffected!.Result(CurrentPosition).CapturedFrames;
            await Disconnect(phones[1]);await Wait(()=>_phoneInputs[2].Capture==null);await Wait(()=>unaffected.Result(CurrentPosition).CapturedFrames>frames+10);
            Check("第二支離線不停止歌曲或另一支評分",ReferenceEquals(_scoreSession,unaffected)&&!_player.State.Paused&&_phone.Mixer.ConnectedCount==1);
            Check("手機離線不移除共同歌曲成績",ScoreBody.IsVisible&&_scoreSession?.Result(CurrentPosition).Pitch==null);
            await Connect(phones[1]);await Wait(()=>_phoneInputs[2].Capture!=null&&!_phoneInputs[2].Failed);Check("同一手機重新連線維持編號並恢復評分",phones[1].Slot==2&&_phone.Mixer.ConnectedCount==2);
            _queue.Add(_queue.CurrentSong!);RefreshQueue();await _player.SeekAsync(_player.State.Duration-.2);await _player.SetPausedAsync(false);await Wait(()=>_songFlow==SongFlow.Results&&!_busy);
            Check("唱完只顯示一份歌曲音準分數",SingleResultSummary.IsVisible&&_finalScore?.Pitch!=null&&ResultPitch.IsVisible&&UiElements<TextBlock>(SingleResultSummary).Count(t=>int.TryParse(t.Text,out _))==1);
            Check("評分旁沒有已完成文字",ScoringMessage.Text=="");
            var result=_finalScore;_voice=_voice with{DeviceId="default"};Render(_player.State);Check("唱完後切換輸入來源不改寫歌曲成績",ReferenceEquals(_finalScore,result)&&SingleResultSummary.IsVisible);_voice=_voice with{DeviceId="phone"};Render(_player.State);
            await CaptureAsync(Path.Combine(folder,"phone-results.png"));await Task.Delay(5200);Check("共同成績停留直到手動下一首",_songFlow==SongFlow.Results&&_player.State.Ended);
            var previous=_scoreSession;await RestartAsync();Check("重播清除整首歌的上一輪分數",_finalScore==null&&!ReferenceEquals(previous,_scoreSession));
            await Wait(()=>_phoneInputs.Values.All(s=>s.Capture!=null)&&_scoreSession!.Result(CurrentPosition).CapturedFrames>15);
            var failedCapture=_phoneInputs[2].Capture!;var failureSession=_scoreSession!;
            failedCapture.Frame+=(_,_)=>throw new IOException("驗收注入的手機分析故障");
            await Wait(()=>_phoneScoreFailures.Contains(2)&&_phoneInputs[2].Capture==null);
            int beforeFailureFrames=failureSession.Result(CurrentPosition).CapturedFrames;
            await Wait(()=>failureSession.Result(CurrentPosition).CapturedFrames>beforeFailureFrames+10);
            Check("手機分析失敗立即顯示來源並隱藏無效成績，其他路繼續處理",ScoringMessage.Text.Contains("手機 2")&&ScoringMessage.Text.Contains("資料不完整")&&ScoreBody.Visibility==Visibility.Collapsed&&!_player.State.Paused&&_phoneInputs.Where(p=>p.Key!=2).All(p=>p.Value.Capture!=null));
            await Disconnect(phones[1]);await Connect(phones[1]);await Wait(()=>_phoneInputs[2].Capture!=null&&!_phoneInputs[2].Failed);
            Check("故障來源可重新連線，但不將已缺漏的整首成績改成有效",_phoneInputs[2].Capture!=failedCapture&&ScoringFailed&&ScoreBody.Visibility==Visibility.Collapsed);
            await _player.SeekAsync(_player.State.Duration-.2);await _player.SetPausedAsync(false);await Wait(()=>_songFlow==SongFlow.Results&&!_busy);
            Check("手機分析故障後結算不提供誤導分數",_finalScore==null&&ResultPitch.Text=="—"&&(ResultPitch.ToolTip as string)?.Contains("資料不完整")==true);
            await RestartAsync();await Wait(()=>_scoreSession!.Result(CurrentPosition).CapturedFrames>15);
            Check("重播建立新一輪評分並清除先前故障狀態",!ScoringFailed&&ScoreBody.IsVisible&&_scoreSession?.Result(CurrentPosition).Pitch==null&&_phoneInputs.Values.All(s=>!s.Failed));
            foreach(var phone in phones)await Disconnect(phone);
            _voice.Save(VoiceSettingsPath);
            _settingsOpen=true;await StopMicrophoneAsync();dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);
            panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");var networkDraft=UiElements<ComboBox>(panel).Single();
            string originalAddress=_phone.Address,originalPair=new Uri(_phone.MicrophoneUrl).Fragment;var beforeNetworkPreview=VoiceSettings.Load(VoiceSettingsPath);
            networkDraft.ItemsSource=networkDraft.ItemsSource.Cast<OpenKaraoke.Phone.LanAddress>().Append(new("127.0.0.2","測試網路草稿")).ToArray();networkDraft.SelectedValue="127.0.0.2";
            Check("未測試的網路草稿不改動正式連線位址",_phone.Address==originalAddress&&VoiceSettings.Load(VoiceSettingsPath)==beforeNetworkPreview);
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_phone.Address=="127.0.0.2"&&_testingMicrophone&&!ToggleStatus.IsChanging((ComboBox)dialog.FindName("MicrophoneInput")));
            Check("測試網路只供預覽，不儲存正式網路",VoiceSettings.Load(VoiceSettingsPath)==beforeNetworkPreview);
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("取消網路測試還原位址與原配對，正式設定完全不變",_phone.Address==originalAddress&&new Uri(_phone.MicrophoneUrl).Fragment==originalPair&&VoiceSettings.Load(VoiceSettingsPath)==beforeNetworkPreview&&!_testingMicrophone);
            _settingsOpen=true;await StopMicrophoneAsync();dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);
            source=(ComboBox)dialog.FindName("MicrophoneInput");panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            string previousUrl=_phone.SetupUrl;var originalPhoneSettings=_voice;source.SelectedValue="default";
            Check("選電腦草稿不會停止已套用的手機來源",_phone.Running&&VoiceSettings.Load(VoiceSettingsPath)==originalPhoneSettings&&panel.Visibility==Visibility.Collapsed);
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_localInput.Running&&_testingMicrophone&&!ToggleStatus.IsChanging(source));
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("取消電腦來源草稿與測試會恢復原手機設定",_phone.Running&&!_localInput.Running&&VoiceSettings.Load(VoiceSettingsPath)==originalPhoneSettings&&dialog.Value==originalPhoneSettings);
            dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);source=(ComboBox)dialog.FindName("MicrophoneInput");source.SelectedValue="default";
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            bool stopped=false;using(var client=new HttpClient{Timeout=TimeSpan.FromSeconds(2)})try{await client.GetStringAsync(previousUrl);}catch(Exception error)when(error is HttpRequestException or TaskCanceledException){stopped=true;}
            Check("按套用才保存電腦來源並停止手機服務",stopped&&dialog.Value.DeviceId=="default"&&!_phone.Running&&VoiceSettings.Load(VoiceSettingsPath).DeviceId=="default");
            Check("正式來源與回傳設定一致",_voice==dialog.Value);
            Check("關閉設定後電腦收音與監聽持續",_localInput.Running&&_localInput.Audio.Connected);
            _settingsOpen=false;await SyncMicrophoneAsync();
            Check("電腦評分分支不啟動任何手機分析或服務",!PhoneScoring&&!_phone.Running&&_phoneInputs.Values.All(p=>p.Capture==null)&&(_microphone is OpenKaraoke.Phone.PhoneCapture&&_localInput.Running||_microphoneFailed),new{Device=_voice.DeviceId,LocalCapture=_microphone?.GetType().Name,DeviceUnavailable=_microphoneFailed});
            await StopMicrophoneAsync();_settingsOpen=true;
            dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);source=(ComboBox)dialog.FindName("MicrophoneInput");panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            Check("重新開啟設定保留電腦來源及隱藏的手機區塊",Equals(source.SelectedValue,"default")&&panel.Visibility==Visibility.Collapsed&&!_phone.Running);
            ((Slider)dialog.FindName("LocalVolume")).Value=40;
            ((TextBox)dialog.FindName("DelayInput")).Text="240";((ComboBox)dialog.FindName("ScoringDifficultyInput")).SelectedValue=ScoringDifficulty.Professional;
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_localInput.Audio.Volume==40);
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("測試中按取消恢復來源、音量、延遲與難度，不保存草稿",_localInput.Running&&_localInput.Audio.Volume==100&&VoiceSettings.Load(VoiceSettingsPath)==_voice&&dialog.Value==_voice);
            dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);
            ((Slider)dialog.FindName("LocalVolume")).Value=35;
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_localInput.Audio.Volume==35);
            dialog.Close();await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("測試中直接關閉視窗也恢復正式音量",_localInput.Running&&_localInput.Audio.Volume==100&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==100);
            dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            ((Slider)dialog.FindName("LocalVolume")).Value=40;
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_localInput.Audio.Volume==40);
            await _localInput.StopAsync();await Wait(()=>System.Windows.Automation.AutomationProperties.GetName((Button)dialog.FindName("TestMicrophoneButton"))=="測試收音");
            Check("測試中裝置中斷會結束預覽並重新恢復正式收音",_localInput.Running&&_localInput.Audio.Volume==100&&!_testingMicrophone&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==100);
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_localInput.Running&&_localInput.Audio.Volume==40);
            Check("測試中斷後重試恢復收音及草稿預覽，不保存正式音量",VoiceSettings.Load(VoiceSettingsPath).LocalVolume==100&&dialog.Value.LocalVolume==100);
            CaptureDialog(dialog,Path.Combine(folder,"phone-local-preview.png"));
            var hiddenNetwork=UiElements<ComboBox>(panel).Single();hiddenNetwork.ItemsSource=new[]{new OpenKaraoke.Phone.LanAddress("203.0.113.1","隱藏的網路草稿")};hiddenNetwork.SelectedValue="203.0.113.1";
            await Wait(()=>((Button)dialog.FindName("ApplyButton")).IsEnabled);
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("手機區塊隱藏時按套用不採用其中的網路草稿",dialog.Value.DeviceId=="default"&&!dialog.Value.PhoneEnabled&&dialog.Value.PhoneAddress==_voice.PhoneAddress&&!_phone.Running);
            Check("按套用才保存並生效電腦麥克風音量",_localInput.Running&&_localInput.Audio.Volume==40&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==40);
            dialog=new VoiceSettingsDialog(this,_voice,_references.Model,_phone,_localInput,_store.DataDirectory,_=>throw new IOException("驗收注入的測試音量保存失敗"),testing=>_testingMicrophone=testing);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);
            ((Slider)dialog.FindName("LocalVolume")).Value=5;
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_localInput.Audio.Volume==5);
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>dialog.IsEnabled);
            Check("預覽後套用保存失敗會恢復正式音量並保留視窗",dialog.IsVisible&&_localInput.Audio.Volume==40&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==40&&UiElements<TextBlock>(dialog).Any(t=>t.IsVisible&&t.Text.Contains("驗收注入的測試音量保存失敗")));
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);source=(ComboBox)dialog.FindName("MicrophoneInput");panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            ((Slider)dialog.FindName("LocalVolume")).Value=75;
            int beforeToggle=automaticSaves;source.SelectedValue="phone";
            source.SelectedValue="default";source.SelectedValue="phone";
            await Wait(()=>_phone.Running&&!ToggleStatus.IsChanging(source));
            Check("準備連線時忽略重複選擇，沒有自動保存",Equals(source.SelectedValue,"phone")&&!_testingMicrophone&&automaticSaves==beforeToggle&&VoiceSettings.Load(VoiceSettingsPath).DeviceId=="default"&&_phone.Mixer.Channels.All(c=>c.Audio.Volume==100));
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_testingMicrophone&&!ToggleStatus.IsChanging(source));
            Check("測試只有選定來源可監聽，正式電腦音量仍保留",_localInput.Mixer.SuppressMonitoring&&!_phone.Mixer.SuppressMonitoring&&VoiceSettings.Load(VoiceSettingsPath).LocalVolume==40);
            source.SelectedValue="default";dialog.Close();await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("測試切換後立即關閉會還原，沒有儲存草稿",!_phone.Running&&VoiceSettings.Load(VoiceSettingsPath).DeviceId=="default"&&automaticSaves==beforeToggle&&!_testingMicrophone);
            Check("切回電腦後恢復已保存音量並持續監聽",_localInput.Running&&_localInput.Audio.Volume==40);
            dialog=Settings(_voice with{PhoneAddress="203.0.113.1"});modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);source=(ComboBox)dialog.FindName("MicrophoneInput");panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            beforeToggle=automaticSaves;source.SelectedValue="phone";
            await Wait(()=>!ToggleStatus.IsChanging(source));
            ((Button)dialog.FindName("TestMicrophoneButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>!ToggleStatus.IsChanging(source));
            Check("測試失敗恢復正式電腦來源並保留可修正草稿",dialog.IsVisible&&!_phone.Running&&_localInput.Running&&Equals(source.SelectedValue,"phone")&&source.IsEnabled&&panel.Visibility==Visibility.Visible&&automaticSaves==beforeToggle&&UiElements<TextBlock>(dialog).Any(t=>t.IsVisible&&t.Text.Contains("請選擇電腦的區域網路位址")));
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            dialog=new VoiceSettingsDialog(this,_voice,_references.Model,_phone,_localInput,_store.DataDirectory,_=>throw new IOException("驗收注入的設定儲存失敗"),testing=>_testingMicrophone=testing);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);source=(ComboBox)dialog.FindName("MicrophoneInput");panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            source.SelectedValue="phone";await Wait(()=>_phone.Running&&!ToggleStatus.IsChanging(source));((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>dialog.IsEnabled);
            Check("套用保存失敗會停止暫時手機服務並還原電腦來源",dialog.IsVisible&&!_phone.Running&&_localInput.Running&&dialog.Value.DeviceId=="default"&&Equals(source.SelectedValue,"phone")&&panel.Visibility==Visibility.Visible&&UiElements<TextBlock>(dialog).Any(t=>t.IsVisible&&t.Text.Contains("驗收注入的設定儲存失敗")));
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            dialog=Settings(_voice with{DeviceId="phone",PhoneEnabled=true,PhoneAddress="203.0.113.1"});modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");
            source=(ComboBox)dialog.FindName("MicrophoneInput");await Wait(()=>!ToggleStatus.IsChanging(source));network=UiElements<ComboBox>(panel).Single();
            bool canRepair=panel.IsEnabled&&network.IsEnabled&&UiElements<Image>(panel).All(i=>i.Source==null)&&UiElements<Button>(panel).Any(b=>b.IsVisible&&Equals(b.Content,"重新準備連線"));
            network.ItemsSource=new[]{new OpenKaraoke.Phone.LanAddress("127.0.0.1","驗收網路")};network.SelectedValue="127.0.0.1";await Wait(()=>_phone.Running&&!ToggleStatus.IsChanging(source));
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            Check("手機連線準備失敗後仍可修正網路並重新建立 QR Code",canRepair&&dialog.Value.DeviceId=="phone"&&dialog.Value.PhoneAddress=="127.0.0.1"&&_phone.Running);
            _voice=dialog.Value;dialog=Settings(_voice);modal=ShowModal(dialog);await Wait(()=>dialog.IsVisible);panel=(PhoneSettingsPanel)dialog.FindName("PhonePanel");network=UiElements<ComboBox>(panel).Single();network.ItemsSource=new[]{new OpenKaraoke.Phone.LanAddress("203.0.113.1","錯誤網路")};network.SelectedValue="203.0.113.1";
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>dialog.IsEnabled&&((Button)dialog.FindName("ApplyButton")).IsEnabled);
            Check("網路套用失敗還原手機服務並保留可修改的設定視窗",dialog.IsVisible&&_phone.Running&&_phone.Address=="127.0.0.1"&&panel.IsEnabled&&UiElements<TextBlock>(dialog).Any(t=>t.IsVisible&&t.Text.Contains("請選擇電腦的區域網路位址")));
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await modal.WaitAsync(TimeSpan.FromSeconds(15));
            await _phone.ApplyAsync(_voice with{PhoneEnabled=false});
            var lifecycle=new List<(bool Changing,bool Target)>();void ObserveLifecycle()=>lifecycle.Add((_phone.IsChanging,_phone.TargetEnabled));
            _phone.Changed+=ObserveLifecycle;
            try
            {
                await _phone.ApplyAsync(_voice with{PhoneEnabled=true});
                Check("服務啟動仍保留防重複操作與就緒判斷",lifecycle.Contains((true,true))&&!_phone.IsChanging&&_phone.Running);
                lifecycle.Clear();await _phone.ApplyAsync(_voice with{PhoneEnabled=false});
                Check("服務關閉仍保留防重複操作與完成判斷",lifecycle.Contains((true,false))&&!_phone.IsChanging&&!_phone.Running);
            }
            finally{_phone.Changed-=ObserveLifecycle;}
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));}
        finally{foreach(var phone in phones){phone.Cancellation?.Cancel();if(phone.Sending!=null)await phone.Sending;phone.Socket?.Abort();phone.Socket?.Dispose();phone.Cancellation?.Dispose();}dialog?.Close();_settingsOpen=false;Close();}
    }
}


