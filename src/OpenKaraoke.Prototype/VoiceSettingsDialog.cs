using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenKaraoke.Scoring;
using OpenKaraoke.Phone;

namespace OpenKaraoke.Prototype;

public sealed class VoiceSettingsDialog : KaraokeDialog
{
    private readonly ComboBox _devices = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly TextBox _delay = new();
    private readonly ComboBox _difficulty = new() { DisplayMemberPath = "Name", SelectedValuePath = "Value" };
    private readonly Slider _subtitleFont = new() { Name="SubtitleFontSize",Minimum=75,Maximum=125,TickFrequency=5,SmallChange=5,LargeChange=10,IsSnapToTickEnabled=true,IsMoveToPointEnabled=true };
    private readonly ProgressBar _meter = new() { Minimum = 0, Maximum = 1, Height = 6 };
    private readonly Button _test;
    private readonly Button _apply;
    private readonly PhoneHost _phone;
    private readonly LocalMicrophone _local;
    private readonly Slider _localVolume=new(){Name="LocalVolume",Minimum=0,Maximum=100,TickFrequency=1,IsSnapToTickEnabled=true,IsMoveToPointEnabled=true};
    private readonly StackPanel _localVolumeRow=new(){Margin=new(0,16,0,0)};
    private readonly PhoneSettingsPanel _phonePanel;
    private readonly TabItem _voiceTab;
    private readonly Action<VoiceSettings>? _applySettings;
    private readonly Func<VoiceSettings,Task>? _applyDisplaySettings;
    private readonly Action<bool>? _testChanged;
    private readonly Action<Exception>? _restoreFailed;
    private readonly VoiceSettings _original,_originalPhone,_initialDraft;
    private readonly bool _localWasRunning,_phoneWasRunning,_localSuppressed,_phoneSuppressed,_localWasMuted;
    private readonly string _originalLocalDevice;
    private readonly int _originalLocalVolume;
    private VoiceSettings _draft;
    private bool _previewApplied,_connectionPrepared;
    private Task _sourcePending=Task.CompletedTask;
    private bool _changingSource,_updatingSource;
    private IMicrophoneCapture? _capture;
    private bool _testing, _closing, _finished;
    public VoiceSettings Value { get; private set; }
    public VoiceSettingsDialog(Window owner, VoiceSettings settings, string model,PhoneHost phone,LocalMicrophone local,string dataDirectory,Action<VoiceSettings>? applySettings=null,Action<bool>? testChanged=null,Action<Exception>? restoreFailed=null,Func<VoiceSettings,Task>? applyDisplaySettings=null) : base(owner, "設定", 390)
    {
        Value=_original=_draft=settings with{PhoneEnabled=settings.DeviceId=="phone"};_phone=phone;_local=local;_applySettings=applySettings;_testChanged=testChanged;_restoreFailed=restoreFailed;
        _applyDisplaySettings=applyDisplaySettings;
        _originalPhone=phone.Configuration;_phoneWasRunning=phone.Running;_localWasRunning=local.Running;_originalLocalDevice=local.DeviceId;
        _originalLocalVolume=local.Audio.Volume;_localWasMuted=local.Audio.Muted;_localSuppressed=local.Mixer.SuppressMonitoring;_phoneSuppressed=phone.Mixer.SuppressMonitoring;
        var iconStyle=new Style(typeof(Icon));iconStyle.Setters.Add(new Setter(OpenKaraoke.Prototype.Icon.StrokeThicknessProperty,2d));Resources[typeof(Icon)]=iconStyle;
        HeadingText.FontSize=16;Heading.Margin=new(0,0,0,22);
        var headingIcon=new Icon{Kind="settings",Width=16,Height=16,Foreground=(Brush)FindResource("Accent"),Margin=new(0,0,8,0)};
        DockPanel.SetDock(headingIcon,Dock.Left);Heading.Children.Insert(0,headingIcon);
        var tabs = new TabControl { Style=(Style)FindResource("VoiceTabs"), BorderThickness = new(0), Background = Background };
        RegisterName("VoiceTabs", tabs);
        var body = new StackPanel();
        var viewport=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};RegisterName("VoiceViewport",viewport);
        var voiceHeader=new StackPanel{Orientation=Orientation.Horizontal};voiceHeader.Children.Add(new Icon{Kind="mic",Width=15,Height=15,Margin=new(0,0,7,0)});voiceHeader.Children.Add(new TextBlock{Text="語音",FontSize=13});
        _voiceTab=new TabItem { Style=(Style)FindResource("VoiceTab"), Header = voiceHeader, Content = viewport, Background = Background, IsSelected = true };tabs.Items.Add(_voiceTab);
        var phonePanel=new PhoneSettingsPanel(phone,Value){Margin=new(0,8,0,0)};_phonePanel=phonePanel;RegisterName("PhonePanel",phonePanel);
        _initialDraft=phonePanel.Read(_original);
        var phoneHeader=new StackPanel{Orientation=Orientation.Horizontal};phoneHeader.Children.Add(new Icon{Kind="smartphone",Width=15,Height=15,Margin=new(0,0,7,0)});phoneHeader.Children.Add(new TextBlock{Text="手機麥克風",FontSize=13});phonePanel.Children.Insert(0,phoneHeader);
        var scoring = new StackPanel();
        _difficulty.ItemsSource = new[] { new { Name="休閒", Value=ScoringDifficulty.Casual }, new { Name="進階", Value=ScoringDifficulty.Advanced }, new { Name="專業", Value=ScoringDifficulty.Professional } };
        _difficulty.SelectedValue=settings.Difficulty; RegisterName("ScoringDifficultyInput",_difficulty);
        Field(scoring,"評分難度",_difficulty);
        scoring.Children.Add(new TextBlock{Text="套用後於下一首或重唱生效。",FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Sub"),Margin=new(0,12,0,0)});
        var scoringTab=new TabItem{Style=(Style)FindResource("VoiceTab"),Header="評分系統",Content=scoring};
        tabs.Items.Add(scoringTab);RegisterName("ScoringTab",scoringTab);
        var display = new StackPanel();
        var fontRow = new DockPanel();
        var fontPercent = new TextBlock { Text=$"{settings.SubtitleFontPercent}%",FontSize=12,Width=48,TextAlignment=TextAlignment.Right,Foreground=(Brush)FindResource("Sub") };
        DockPanel.SetDock(fontPercent,Dock.Right);fontRow.Children.Add(fontPercent);
        _subtitleFont.Value=settings.SubtitleFontPercent;RegisterName("SubtitleFontSize",_subtitleFont);
        System.Windows.Automation.AutomationProperties.SetName(_subtitleFont,"歌詞字體大小");
        _subtitleFont.ValueChanged+=(_,_)=>fontPercent.Text=$"{_subtitleFont.Value:0}%";fontRow.Children.Add(_subtitleFont);
        Field(display,"歌詞字體大小",fontRow);
        var resetFont=new Button{Content="恢復預設大小",FontSize=12,HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,10,0,0)};
        resetFont.Click+=(_,_)=>_subtitleFont.Value=100;RegisterName("ResetSubtitleFontButton",resetFont);display.Children.Add(resetFont);
        display.Children.Add(new TextBlock{Text="套用後立即更新目前歌詞，適用於兩種歌詞模式。",FontSize=12,Foreground=(Brush)FindResource("Sub"),TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,0)});
        var displayTab=new TabItem{Style=(Style)FindResource("VoiceTab"),Header="顯示",Content=display};
        tabs.Items.Add(displayTab);RegisterName("DisplayTab",displayTab);
        var surface=(Border)Content;((StackPanel)surface.Child).Children.Clear();var layout=new Grid();
        foreach(var height in new[]{GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})layout.RowDefinitions.Add(new(){Height=height});
        var footer=new Grid{Margin=Actions.Margin};footer.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var folderLabel=new StackPanel{Orientation=Orientation.Horizontal};
        folderLabel.Children.Add(new Icon{Kind="folder-open",Width=15,Height=15,Margin=new(0,0,7,0)});
        folderLabel.Children.Add(new TextBlock{Text="歌曲庫與紀錄",FontSize=12});
        var openData=new Button{Content=folderLabel,HorizontalAlignment=HorizontalAlignment.Left,ToolTip="開啟歌曲庫、演唱錄音、評分紀錄與設定的儲存位置"};
        RegisterName("OpenDataDirectoryButton",openData);System.Windows.Automation.AutomationProperties.SetName(openData,"歌曲庫與紀錄");
        openData.Click+=(_,_)=>
        {
            try{Process.Start(new ProcessStartInfo(dataDirectory){UseShellExecute=true})?.Dispose();}
            catch(Exception error){ErrorText.Text="無法開啟資料目錄："+error.Message;}
        };
        Actions.Margin=new(0);Grid.SetColumn(Actions,1);footer.Children.Add(openData);footer.Children.Add(Actions);
        var sections=new UIElement[]{Heading,tabs,(UIElement)FindName("DialogErrorViewport"),footer};
        for(int index=0;index<sections.Length;index++){Grid.SetRow(sections[index],index);layout.Children.Add(sections[index]);}surface.Child=layout;
        MaxHeight=Math.Max(280,SystemParameters.WorkArea.Height-40);
        SizeChanged+=(_,_)=>{var area=SystemParameters.WorkArea;if(IsLoaded&&Left>=area.Left&&Left<area.Right&&Top>=area.Top&&Top<area.Bottom)Top=Math.Clamp(Top,area.Top+20,Math.Max(area.Top+20,area.Bottom-ActualHeight-20));};
        var devices = new List<MicrophoneDevice> { new("default", "系統預設麥克風"),new("phone","手機麥克風") };
        try { devices.AddRange(MicrophoneCapture.Devices()); } catch (Exception error) { ErrorText.Text = "無法讀取麥克風：" + error.Message; }
        if (settings.DeviceId != "default" && devices.All(d => d.Id != settings.DeviceId)) devices.Add(new(settings.DeviceId, "未連接的麥克風"));
        _devices.ItemsSource = devices; _devices.SelectedValue = settings.DeviceId;
        System.Windows.Automation.AutomationProperties.SetName(_devices, "麥克風");
        RegisterName("MicrophoneInput", _devices); Field(body, "麥克風", _devices);
        var row = new DockPanel { Margin = new(0, 16, 0, 0) };
        _test = new Button { BorderBrush=(Brush)FindResource("Line"), BorderThickness=new(1), Margin = new(10, 0, 0, 0) };
        SetTestLabel(false);RegisterName("TestMicrophoneButton",_test);RegisterName("MicrophoneLevelMeter",_meter);_meter.Style=(Style)FindResource("SoftProgress");
        DockPanel.SetDock(_test, Dock.Right); row.Children.Add(_test);row.Children.Add(new TextBlock{Text="收音",FontSize=12,Foreground=(Brush)FindResource("Sub"),Margin=new(0,0,10,0)}); row.Children.Add(_meter); body.Children.Add(row);
        _localVolume.Value=settings.LocalVolume;RegisterName("LocalVolume",_localVolume);System.Windows.Automation.AutomationProperties.SetName(_localVolume,"電腦麥克風播放音量");
        var volumePercent=new TextBlock{FontSize=12,Width=42,TextAlignment=TextAlignment.Right,Text=$"{settings.LocalVolume}%",Foreground=(Brush)FindResource("Sub")};
        _localVolumeRow.Children.Add(new TextBlock{Text="麥克風音量",FontSize=12,Foreground=(Brush)FindResource("Sub")});var volumeRow=new DockPanel{Margin=new(0,6,0,0)};DockPanel.SetDock(volumePercent,Dock.Right);volumeRow.Children.Add(volumePercent);volumeRow.Children.Add(_localVolume);_localVolumeRow.Children.Add(volumeRow);body.Children.Add(_localVolumeRow);
        _localVolume.ValueChanged+=(_,_)=>{volumePercent.Text=$"{_localVolume.Value:0}%";PreviewTestVolume();};
        _delay.Text = settings.DelayMilliseconds.ToString(); RegisterName("DelayInput", _delay);
        System.Windows.Automation.AutomationProperties.SetName(_delay, "延遲補償毫秒");
        _delay.Width=52;_delay.BorderThickness=new(0);_delay.Background=Brushes.Transparent;_delay.HorizontalContentAlignment=HorizontalAlignment.Center;_delay.VerticalContentAlignment=VerticalAlignment.Center;_delay.Padding=new(0);_delay.Height=34;
        var delayRow=new DockPanel{Margin=new(0,16,0,0)};var stepper=new StackPanel{Orientation=Orientation.Horizontal};
        Button Step(string icon,int delta,string name)
        {
            var button=new Button{Content=new Icon{Kind=icon,Width=15,Height=15},Style=(Style)FindResource("IconButton")};
            RegisterName(delta<0?"DelayDecreaseButton":"DelayIncreaseButton",button);System.Windows.Automation.AutomationProperties.SetName(button,name);
            button.Click+=(_,_)=>_delay.Text=Math.Clamp((int.TryParse(_delay.Text,out int value)?value:0)+delta,0,1000).ToString();return button;
        }
        stepper.Children.Add(Step("minus",-10,"減少延遲補償"));stepper.Children.Add(_delay);stepper.Children.Add(new TextBlock{Text="ms",FontSize=12,Foreground=(Brush)FindResource("Sub"),Margin=new(0,0,7,0)});stepper.Children.Add(Step("plus",10,"增加延遲補償"));
        var stepperBorder=new Border{Child=stepper,Background=(Brush)FindResource("Raised"),BorderBrush=(Brush)FindResource("Line"),BorderThickness=new(1),CornerRadius=new(8)};
        DockPanel.SetDock(stepperBorder,Dock.Right);delayRow.Children.Add(stepperBorder);delayRow.Children.Add(new TextBlock{Text="延遲補償",FontSize=12,Foreground=(Brush)FindResource("Sub")});body.Children.Add(delayRow);
        body.Children.Add(phonePanel);
        var cancel = Action("取消"); var apply = Action("套用", true);_apply=apply; RegisterName("ApplyButton", apply);
        RegisterName("CancelButton",cancel);
        _delay.TextChanged += (_, _) =>
        {
            RenderSource();
        };
        cancel.Click += async (_, _) => await CancelAsync(true);
        apply.Click += async (_, _) =>
        {
            if (!int.TryParse(_delay.Text, out int delay) || delay is < 0 or > 1000) return;
            if(_closing||_changingSource||_phone.IsChanging||_local.IsChanging)return;_closing=true;IsEnabled=false;
            bool displayAttempted=false;
            try
            {
                var value=ReadDraft();
                bool onlyDisplay=value.SubtitleFontPercent!=_original.SubtitleFontPercent
                    &&(value with{SubtitleFontPercent=_original.SubtitleFontPercent})==_initialDraft&&!_previewApplied&&!_connectionPrepared;
                if(onlyDisplay)value=_original with{SubtitleFontPercent=value.SubtitleFontPercent};
                await StopMeterAsync();
                if(!onlyDisplay)
                {
                    _previewApplied=true;_phone.Mixer.SuppressMonitoring=_local.Mixer.SuppressMonitoring=true;
                    await ApplyInputsAsync(value);
                }
                if(!onlyDisplay&&value.DeviceId=="phone")value=value with{PhoneAddress=_phone.Configuration.PhoneAddress};
                displayAttempted=true;
                if(_applyDisplaySettings!=null)await _applyDisplaySettings(value);
                _applySettings?.Invoke(value);Value=value;_previewApplied=_connectionPrepared=false;EndPreview();phonePanel.Finish();_finished=true;DialogResult=true;
            }
            catch(Exception error)
            {
                string message=error.Message;
                if(displayAttempted&&_applyDisplaySettings!=null)
                    try{await _applyDisplaySettings(_original);}catch(Exception restore){message+="\n"+restore.Message;}
                try{await RestoreOriginalAsync();}catch(Exception restore){message+="\n"+restore.Message;}
                ErrorText.Text=message;_closing=false;IsEnabled=true;RenderSource();
            }
        };
        _test.Click += async (_, _) =>
        {
            if(_changingSource||_phone.IsChanging||_local.IsChanging||_closing)return;
            _sourcePending=ChangeTestAsync(!_testing,model);await _sourcePending;
        };
        Closing += async (_, e) =>
        {
            if(_finished)return;e.Cancel=true;await CancelAsync(false);
        };
        _devices.SelectionChanged+=(_,_)=>ScheduleSourceChange(model);
        phonePanel.DraftChanged+=()=>{if(!_changingSource&&!_closing)_sourcePending=_testing?ChangeTestAsync(true,model):PrepareConnectionAsync();};
        phonePanel.RetryRequested+=()=>{if(!_changingSource&&!_closing)_sourcePending=_testing?ChangeTestAsync(true,model):PrepareConnectionAsync();};
        Loaded+=(_,_)=>{if(_draft.DeviceId=="phone"&&!_closing)_sourcePending=PrepareConnectionAsync();};
        tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs))RenderSource();};_phone.Changed+=PhoneChanged;_local.Changed+=PhoneChanged;Closed+=(_,_)=>{_phone.Changed-=PhoneChanged;_local.Changed-=PhoneChanged;};RenderSource();
    }
    private void PhoneChanged()=>Dispatcher.BeginInvoke(()=>{if(!_finished)RenderSource();});
    private void RenderSource()
    {
        string selected=_draft.DeviceId;bool phone=selected=="phone",changing=_changingSource||_phone.IsChanging||_local.IsChanging;
        _phonePanel.Visibility=phone?Visibility.Visible:Visibility.Collapsed;_phonePanel.IsEnabled=phone&&!changing;
        _devices.IsEnabled=!changing&&!_closing;_test.IsEnabled=!changing&&!_closing;
        _apply.IsEnabled=!changing&&!_closing&&int.TryParse(_delay.Text,out int delay)&&delay is >=0 and <=1000;
        _localVolumeRow.Visibility=phone?Visibility.Collapsed:Visibility.Visible;_localVolume.IsEnabled=!changing&&!_closing;
        Width=_voiceTab.IsSelected&&phone?430:390;
    }
    private void ScheduleSourceChange(string model)
    {
        if(_updatingSource)return;
        if(_closing||_changingSource||_phone.IsChanging||_local.IsChanging)
        {_updatingSource=true;_devices.SelectedValue=_draft.DeviceId;_updatingSource=false;return;}
        if(_devices.SelectedValue is not string id||id==_draft.DeviceId)return;
        _draft=_draft with{DeviceId=id,PhoneEnabled=id=="phone"};ErrorText.Text="";RenderSource();
        _sourcePending=_testing?ChangeTestAsync(true,model):PrepareConnectionAsync();
    }
    private async Task PrepareConnectionAsync()
    {
        ErrorText.Text="";
        if(_phoneWasRunning||_draft.DeviceId!="phone"&&!_connectionPrepared){RenderSource();return;}
        _changingSource=true;ToggleStatus.Begin(_devices,_draft.DeviceId=="phone");_phonePanel.BeginSourceChange();RenderSource();
        try
        {
            await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Background);
            ErrorText.Text="";
            if(_draft.DeviceId=="phone")
            {
                _connectionPrepared=true;_phone.Mixer.SuppressMonitoring=true;
                await _phone.ApplyAsync(ReadDraft(),startOutput:false);
            }
            else
            {
                await _phone.ApplyAsync(_originalPhone with{PhoneEnabled=false});
                _connectionPrepared=false;_phone.Mixer.SuppressMonitoring=_phoneSuppressed;
            }
        }
        catch(Exception error)
        {
            string message=error.Message;
            try{await _phone.ApplyAsync(_originalPhone with{PhoneEnabled=false});_connectionPrepared=false;}
            catch(Exception restore){_connectionPrepared=true;message+="\n"+restore.Message;}
            _phone.Mixer.SuppressMonitoring=_connectionPrepared||_phoneSuppressed;
            ErrorText.Text="無法準備手機連線："+message;
        }
        finally{_changingSource=false;ToggleStatus.Complete(_devices,_testing);_phonePanel.CompleteSourceChange();RenderSource();}
    }
    private VoiceSettings ReadDraft()=>_phonePanel.Read(_draft with
    {
        DelayMilliseconds=int.TryParse(_delay.Text,out int delay)?Math.Clamp(delay,0,1000):_original.DelayMilliseconds,
        LocalVolume=_draft.DeviceId=="phone"?_original.LocalVolume:(int)_localVolume.Value,
        Difficulty=(ScoringDifficulty)(_difficulty.SelectedValue??ScoringDifficulty.Casual),
        SubtitleFontPercent=(int)_subtitleFont.Value
    });
    private async Task ChangeTestAsync(bool start,string model)
    {
        _changingSource=true;ErrorText.Text="";ToggleStatus.Begin(_devices,start);_phonePanel.BeginSourceChange();RenderSource();
        try
        {
            await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Background);
            await StopMeterAsync();
            if(!start){await RestoreOriginalAsync(keepConnection:_draft.DeviceId=="phone");return;}
            _previewApplied=true;_testing=true;_testChanged?.Invoke(true);
            _phone.Mixer.SuppressMonitoring=_local.Mixer.SuppressMonitoring=true;
            var value=ReadDraft();
            if(value.DeviceId=="phone")
            {
                if(!_phoneWasRunning)_connectionPrepared=true;
                await _phone.ApplyAsync(value,preservePairing:_phoneWasRunning);
                if(!_localWasRunning)await _local.StopAsync();
                _phone.Mixer.SuppressMonitoring=false;
            }
            else
            {
                if(!_phoneWasRunning){await _phone.ApplyAsync(_originalPhone with{PhoneEnabled=false});_connectionPrepared=false;}
                await _local.StartAsync(value.DeviceId,value.LocalVolume);_local.Mixer.SuppressMonitoring=false;
            }
            IMicrophoneCapture capture=value.DeviceId=="phone"?new PhoneLevelCapture(_phone.Mixer,waitForConnection:true):new PhoneLevelCapture(_local.Mixer,"電腦麥克風");_capture=capture;
            capture.Level+=level=>Dispatcher.BeginInvoke(()=>{if(_capture==capture)_meter.Value=Math.Clamp(level*5,0,1);});
            capture.Failed+=error=>Dispatcher.BeginInvoke(async()=>
            {
                if(_capture!=capture||_closing||_changingSource)return;
                _sourcePending=ChangeTestAsync(false,model);await _sourcePending;
                ErrorText.Text="無法使用麥克風："+error.Message;
            });
            await capture.StartAsync(value.DeviceId,model,detectPitch:false);SetTestLabel(true);
        }
        catch(Exception error)
        {
            string message=error.Message;
            await StopMeterAsync();try{await RestoreOriginalAsync();}catch(Exception restore){message+="\n"+restore.Message;}
            ErrorText.Text="無法測試麥克風："+message;
        }
        finally
        {
            _changingSource=false;ToggleStatus.Complete(_devices,_testing);_phonePanel.CompleteSourceChange();RenderSource();
        }
    }
    private async Task ApplyInputsAsync(VoiceSettings value)
    {
        _phone.Mixer.EchoLevel=value.EchoLevel;_local.Mixer.EchoLevel=value.EchoLevel;
        if(value.DeviceId=="phone")await _local.StopAsync();
        await _phone.ApplyAsync(value,preservePairing:_phoneWasRunning);
        if(value.DeviceId!="phone")await _local.StartAsync(value.DeviceId,value.LocalVolume);
    }
    private async Task StopMeterAsync()
    {
        var capture=_capture;_capture=null;if(capture!=null)await capture.DisposeAsync();
        _meter.Value=0;
    }
    private void EndPreview()
    {
        _testing=false;_phone.Mixer.SuppressMonitoring=_connectionPrepared||_phoneSuppressed;_local.Mixer.SuppressMonitoring=_localSuppressed;
        _testChanged?.Invoke(false);SetTestLabel(false);
    }
    private async Task RestoreOriginalAsync(bool keepConnection=false)
    {
        if(!_previewApplied)
        {
            if(_connectionPrepared&&!keepConnection){await _phone.ApplyAsync(_originalPhone with{PhoneEnabled=false});_connectionPrepared=false;}
            EndPreview();return;
        }
        _phone.Mixer.SuppressMonitoring=_local.Mixer.SuppressMonitoring=true;
        var errors=new List<Exception>();
        try
        {
            bool keep=keepConnection&&!_phoneWasRunning&&_phone.Running;
            if(keep){_connectionPrepared=true;_phone.Mixer.StopOutput();}
            else{await _phone.ApplyAsync(_originalPhone with{PhoneEnabled=_phoneWasRunning},preservePairing:_phoneWasRunning);_connectionPrepared=false;}
        }
        catch(Exception error){errors.Add(error);}
        try
        {
            if(_localWasRunning)await _local.StartAsync(_originalLocalDevice,_originalLocalVolume);else await _local.StopAsync();
            _local.Audio.Configure(_localWasMuted,_originalLocalVolume);
        }
        catch(Exception error){errors.Add(error);}
        _previewApplied=errors.Count>0;EndPreview();
        if(errors.Count>0)throw new IOException("無法還原原本的收音："+string.Join("；",errors.Select(e=>e.Message)),errors[0]);
    }
    private async Task CancelAsync(bool dialogResult)
    {
        if(_closing)return;_closing=true;IsEnabled=false;
        var errors=new List<Exception>();
        try{await _sourcePending;}catch(Exception error){errors.Add(error);}
        try{await StopMeterAsync();}catch(Exception error){errors.Add(error);}
        try{await RestoreOriginalAsync();}catch(Exception error){errors.Add(error);}
        Value=_original;EndPreview();_phonePanel.Finish();_finished=true;
        if(errors.Count>0)_restoreFailed?.Invoke(new IOException("原設定已保留，但無法恢復收音裝置："+string.Join("；",errors.Select(e=>e.Message)),errors[0]));
        if(dialogResult)DialogResult=false;else _=Dispatcher.BeginInvoke(Close);
    }
    private void PreviewTestVolume()
    {if(_testing&&!_closing&&!_changingSource&&_draft.DeviceId!="phone"&&_local.Running)_local.Audio.Configure(_local.Audio.Muted,(int)_localVolume.Value);}
    private void SetTestLabel(bool testing)
    {
        var content=new StackPanel{Orientation=Orientation.Horizontal};content.Children.Add(new Icon{Kind="audio-lines",Width=15,Height=15,Margin=new(0,0,7,0)});content.Children.Add(new TextBlock{Text=testing?"停止測試":"測試收音",FontSize=12});_test.Content=content;
        System.Windows.Automation.AutomationProperties.SetName(_test,testing?"停止測試":"測試收音");
    }
}
