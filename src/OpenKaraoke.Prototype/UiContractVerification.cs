using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private static object[] ButtonAlignmentIssues(DependencyObject scope)
    {
        var issues=new List<object>();
        foreach(var button in UiElements<Button>(scope).Where(b=>b.IsVisible&&b.ActualWidth>0&&b.ActualHeight>0))
        {
            var presenter=UiElements<ContentPresenter>(button).FirstOrDefault(p=>p.TemplatedParent==button);
            if(presenter==null)continue;
            var bounds=presenter.TransformToAncestor(button).TransformBounds(new Rect(presenter.RenderSize));
            double dx=bounds.Left+bounds.Width/2-button.ActualWidth/2,dy=bounds.Top+bounds.Height/2-button.ActualHeight/2;
            bool centered=(button.HorizontalContentAlignment!=HorizontalAlignment.Center||Math.Abs(dx)<=1)
                &&(button.VerticalContentAlignment!=VerticalAlignment.Center||Math.Abs(dy)<=1);
            bool fits=button.Content is not Icon icon||icon.ActualWidth<=presenter.ActualWidth+.01&&icon.ActualHeight<=presenter.ActualHeight+.01;
            if(!centered||!fits)issues.Add(new{button.Name,Label=AutomationProperties.GetName(button),Content=button.Content?.ToString(),Dx=dx,Dy=dy,Fits=fits,Bounds=bounds,button.ActualWidth,button.ActualHeight,ContentWidth=(button.Content as FrameworkElement)?.ActualWidth,ContentHeight=(button.Content as FrameworkElement)?.ActualHeight,RequestedWidth=(button.Content as FrameworkElement)?.Width,RequestedHeight=(button.Content as FrameworkElement)?.Height,PresenterWidth=presenter.ActualWidth,PresenterHeight=presenter.ActualHeight});
        }
        return issues.ToArray();
    }
    private static IEnumerable<T> UiElements<T>(DependencyObject node) where T : DependencyObject
    {
        if(node is T element)yield return element;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
            foreach(var child in UiElements<T>(VisualTreeHelper.GetChild(node,i)))yield return child;
    }
    private static object UiInventory(Window window)=>new
    {
        window.Title,
        Buttons=UiElements<Button>(window).Where(b=>b.IsVisible).Select(b=>new
        {
            b.Name,Label=AutomationProperties.GetName(b),Text=b.Content is string s?s:string.Join("",UiElements<TextBlock>(b).Select(t=>t.Text)),
            Tooltip=b.ToolTip?.ToString(),Icons=UiElements<Icon>(b).Select(i=>i.Kind).ToArray(),b.IsEnabled
        }).ToArray(),
        Tooltips=UiElements<FrameworkElement>(window).Where(e=>e.IsVisible&&e.ToolTip!=null).Select(e=>new{e.Name,Text=e.ToolTip.ToString()}).ToArray()
    };

    private async Task VerifyUiContractAsync(string folder)
    {
        var checks=new List<object>();var inventory=new List<object>();
        void Check(string name,bool passed,object? evidence=null)
        {checks.Add(new{Name=name,Passed=passed,Evidence=evidence});if(!passed)throw new InvalidOperationException(name);}
        async Task Layout()=>await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
        try
        {
            await Layout();inventory.Add(UiInventory(this));
            Check("啟動預設仍為 1600×900",ActualWidth==1600&&ActualHeight==900);
            Check("主視窗外框及系統圓角皆已移除",AppCanvas.CornerRadius==new CornerRadius(0)&&System.Windows.Shell.WindowChrome.GetWindowChrome(this).CornerRadius==new CornerRadius(0)&&SquareWindowPreference(this));
            var logo=VerifyApplicationLogo(folder);
            Check("視窗及執行檔均使用相同聲波 LOGO",logo,new{WindowIcon=Icon?.ToString()});
            var header=UiElements<Button>(Header).ToArray();
            Check("標題列三處空白皆接收拖曳事件",new[]{.25,.5,.75}.All(x=>ReferenceEquals(Header.InputHitTest(new Point(Header.ActualWidth*x,Header.ActualHeight/2)),Header)));
            Check("標題列四個按鈕仍各自接收點擊",header.All(b=>FindButton(Header.InputHitTest(b.TranslatePoint(new Point(b.ActualWidth/2,b.ActualHeight/2),Header)) as DependencyObject)));
            Check("標題列依序只有設定、最小化、視窗控制、關閉",header.Length==4&&header[0]==SettingsButton&&header[2]==MaximizeButton&&
                header.Select(b=>UiElements<Icon>(b).Single().Kind).SequenceEqual(new[]{"settings","minus","square","x"}));
            SettingsButton.Focus();await Layout();
            var settingsSurface=(Border)SettingsButton.Template.FindName("Surface",SettingsButton);
            Check("設定按鈕取得焦點仍沒有外框，保留鍵盤提示",settingsSurface.BorderThickness==new Thickness(0)&&SettingsButton.FocusVisualStyle==FindResource("WindowKeyboardFocus"),new{SettingsButton.IsKeyboardFocused,Border=settingsSurface.BorderThickness.ToString()});
            await CaptureAsync(Path.Combine(folder,"window-focused.png"));
            var expected=new Dictionary<Button,string>{{SettingsButton,"設定"},{header[1],"最小化"},{MaximizeButton,"最大化"},{PauseButton,"暫停"},
                {CutButton,"切歌"},{RestartButton,"重唱"},{OriginalButton,"導唱"},{InstrumentalButton,"伴奏"},
                {KeyDownButton,"降調"},{KeyUpButton,"升調"},{KeyResetButton,"正常調"},{EchoDownButton,"減少迴聲"},{EchoUpButton,"增加迴聲"},{EchoResetButton,"正常迴聲"},{FullscreenButton,"全螢幕"}};
            Check("控制列逐項懸停文字與確認內容相同",expected.All(pair=>Equals(pair.Key.ToolTip,pair.Value)),expected.Select(p=>new{p.Key.Name,Text=p.Key.ToolTip}).ToArray());
            var alignment=ButtonAlignmentIssues(this);Check("主畫面按鈕的圖示與文字置中且未裁切",alignment.Length==0,alignment);
            Check("關閉、清除搜尋、音量、剩餘數量沒有懸停文字",new FrameworkElement[]{header[3],ClearSearchButton,MuteButton,VolumeSlider,VolumeLabel,QueueCount}.All(e=>e.ToolTip==null));
            Check("升降調維持確認文字且沒有額外符號",(string)KeyDownButton.Content=="降調"&&(string)KeyUpButton.Content=="升調"&&Equals(KeyResetButton.Content,"正常調")&&UiElements<Icon>(KeyDownButton).Count()==0&&UiElements<Icon>(KeyUpButton).Count()==0);
            var primary=(Style)FindResource("PrimaryButton");var template=(ControlTemplate)primary.Setters.OfType<Setter>().Single(s=>s.Property==Control.TemplateProperty).Value;
            var hover=template.Triggers.OfType<Trigger>().Single(t=>t.Property==IsMouseOverProperty&&Equals(t.Value,true));
            Check("主要按鈕的懸停樣式保留底色與文字色，只增加亮度",hover.Setters.OfType<Setter>().All(s=>s.Property!=Control.BackgroundProperty&&s.Property!=Control.ForegroundProperty)&&hover.Setters.OfType<Setter>().Any(s=>s.TargetName=="Hover"&&s.Property==OpacityProperty&&Equals(s.Value,.04)));
            var keyContent=UiElements<ContentPresenter>(KeyDownButton).First();var contentBounds=BoundsOnWindow(keyContent);
            KeyDownButton.Focus();await Layout();
            Check("鍵盤焦點不改變按鈕內容位置或邊框尺寸",BoundsOnWindow(keyContent)==contentBounds&&((Border)KeyDownButton.Template.FindName("Surface",KeyDownButton)).BorderThickness==KeyDownButton.BorderThickness&&KeyDownButton.FocusVisualStyle==FindResource("KeyboardFocus"));
            var searchBounds=BoundsOnWindow(SearchSurface);SearchBox.Focus();await Layout();
            Check("搜尋輸入取得焦點時外框明確且不跳動",SearchBox.IsKeyboardFocused&&Equals(SearchSurface.BorderBrush,FindResource("Accent"))&&BoundsOnWindow(SearchSurface)==searchBounds);
            KeyDownButton.Focus();await Layout();Check("搜尋失焦後恢復原外框",Equals(SearchSurface.BorderBrush,FindResource("Line")));
            Check("主視窗移除歌曲播放進度、已播時間與總時間",FindName("PositionSlider")==null&&FindName("ElapsedLabel")==null&&FindName("DurationLabel")==null);
            Check("原唱伴奏選取狀態沿用保留底色的按鈕樣式",(_queue.Mode=="original"?OriginalButton:InstrumentalButton).Style==FindResource("SelectedButton"));
            var catalogRow=_songs[0];SongList.ScrollIntoView(catalogRow);await Layout();
            var item=(ListBoxItem)SongList.ItemContainerGenerator.ContainerFromItem(catalogRow);var actions=UiElements<Button>(item).ToArray();
            Check("歌曲庫與待播共用右側 30% 頁籤",Math.Abs(Catalog.ActualWidth/MainArea.ActualWidth-.3)<.005&&LibraryTabs.Items.Count==2&&SongLibraryTab.IsSelected);
            Check("頁籤依序為待播清單與歌曲庫，各佔一半寬",LibraryTabs.Items[0]==QueueTab&&LibraryTabs.Items[1]==SongLibraryTab&&Math.Abs(QueueTab.ActualWidth-SongLibraryTab.ActualWidth)<1&&Math.Abs(QueueTab.ActualWidth*2-LibraryTabs.ActualWidth)<2);
            Check("兩個頁籤的留白區皆可點擊",new[]{QueueTab,SongLibraryTab}.All(t=>t.InputHitTest(new Point(8,t.ActualHeight/2))!=null&&t.InputHitTest(new Point(t.ActualWidth-8,t.ActualHeight/2))!=null));
            Check("評分系統開關位於升降調右側，完成後不顯示狀態文字",AutomationProperties.GetName(ScoringToggle)=="評分系統"&&Equals(ScoringToggle.Content,"評分系統")&&BoundsOnWindow(ScoringToggle).Left>BoundsOnWindow(PitchGroup).Right&&FindName("ScoringExpander")==null&&UiElements<TextBlock>(ScoringToggle).All(t=>t.Name!="StateLabel"||!t.IsVisible));
            Check("歌詞開關位於評分系統旁，字級與控制樣式一致",AutomationProperties.GetName(SubtitlesToggle)=="顯示歌詞(如果有)"&&Equals(SubtitlesToggle.Content,"顯示歌詞(如果有)")
                &&BoundsOnWindow(SubtitlesToggle).Left>BoundsOnWindow(ScoringToggle).Right&&Math.Abs(BoundsOnWindow(SubtitlesToggle).Top-BoundsOnWindow(ScoringToggle).Top)<1
                &&SubtitlesToggle.FontSize==ScoringToggle.FontSize&&SubtitlesToggle.Style==ScoringToggle.Style);
            Check("升降調在左、迴聲在右，兩組尺寸相同且頂部對齊",PitchGroup.RenderSize==EchoGroup.RenderSize&&BoundsOnWindow(PitchGroup).Right<=BoundsOnWindow(EchoGroup).Left&&Math.Abs(BoundsOnWindow(PitchGroup).Top-BoundsOnWindow(EchoGroup).Top)<1&&BoundsOnWindow(EchoGroup).Right<BoundsOnWindow(ScoringToggle).Left&&EchoDownButton.Content.Equals("迴聲小")&&EchoUpButton.Content.Equals("迴聲大")&&EchoResetButton.Content.Equals("正常"));
            Check("音量由上至下只排列喇叭與麥克風",UiElements<Slider>(VolumeControls).Count()==2&&BoundsOnWindow(VolumeSlider).Bottom<=BoundsOnWindow(MicrophoneVolumeSlider).Top&&MicrophoneVolumeSlider.ActualWidth==VolumeSlider.ActualWidth);
            Check("麥克風只使用圖示，尺寸及百分比字級與喇叭一致",MicrophoneIcon.Kind=="mic"&&MicrophoneIcon.Width==VolumeIcon.Width&&MicrophoneIcon.Height==VolumeIcon.Height&&UiElements<TextBlock>(MicrophoneIcon).Count()==0&&MicrophoneVolumeLabel.FontSize==VolumeLabel.FontSize&&FindName("Microphone1Symbol")==null&&FindName("Microphone2Symbol")==null);
            Check("麥克風圖示可點擊靜音，按鈕大小及圖示置中與喇叭一致",MicrophoneMuteButton.IsHitTestVisible&&MicrophoneMuteButton.IsEnabled&&MicrophoneMuteButton.Content==MicrophoneIcon&&MicrophoneMuteButton.Style==MuteButton.Style&&MicrophoneMuteButton.RenderSize==MuteButton.RenderSize&&Math.Abs(BoundsOnWindow(MicrophoneIcon).Left-BoundsOnWindow(VolumeIcon).Left)<1);
            Check("兩列音量不拉高既有音效群組，按鈕維持原本高度",new[]{AudioModeGroup,PitchGroup,EchoGroup}.All(group=>group.ActualHeight is >=38 and <=42));
            foreach(int width in new[]{1280,1600,1920})
            {
                Width=width;await Layout();Check($"{width}px 音效兩組等大並排，底部控制不重疊",PitchGroup.RenderSize==EchoGroup.RenderSize&&BoundsOnWindow(AutoPlayNextToggle).Right<BoundsOnWindow(AudioModeGroup).Left&&BoundsOnWindow(AudioModeGroup).Right<=BoundsOnWindow(PitchGroup).Left&&BoundsOnWindow(PitchGroup).Right<=BoundsOnWindow(EchoGroup).Left&&Math.Abs(BoundsOnWindow(PitchGroup).Top-BoundsOnWindow(EchoGroup).Top)<1&&BoundsOnWindow(EchoGroup).Right<BoundsOnWindow(ScoringToggle).Left&&BoundsOnWindow(ScoringToggle).Right<BoundsOnWindow(SubtitlesToggle).Left&&BoundsOnWindow(SubtitlesToggle).Right<BoundsOnWindow(MuteButton).Left&&BoundsOnWindow(VolumeLabel).Right<BoundsOnWindow(FullscreenButton).Left);
                Check($"{width}px 喇叭與麥克風音量及百分比完整呈現",BoundsOnWindow(MicrophoneVolumeSlider).Bottom<=ActualHeight&&BoundsOnWindow(MicrophoneVolumeLabel).Right<BoundsOnWindow(FullscreenButton).Left&&MicrophoneVolumeSlider.IsVisible&&VolumeSlider.IsVisible);
            }
            Width=1600;await Layout();
            Check("待播數量使用 X 首且沒有額外懸停文字",QueueCount.Text==$"{_queue.PendingCount} 首"&&QueueCount.ToolTip==null);
            Check("原唱伴奏與升降調的間距增加為 32",Math.Abs(PitchGroup.TranslatePoint(new(),AppCanvas).X-AudioModeGroup.TranslatePoint(new Point(AudioModeGroup.ActualWidth,0),AppCanvas).X-32)<1);
            Check("熱門歌曲右側顯示加入歌曲",AddSongButton.IsVisible&&!ShowPlayedToggle.IsVisible&&BoundsOnWindow(AddSongButton).Left>BoundsOnWindow(PopularLabel).Right);
            QueueTab.IsSelected=true;await Layout();
            Check("切到待播頁籤只顯示已播放切換鈕",QueueArea.IsVisible&&ShowPlayedToggle.IsVisible&&!AddSongButton.IsVisible&&!SearchBox.IsVisible);
            Check("顯示已播放不附加狀態文字",UiElements<TextBlock>(ShowPlayedToggle).All(t=>t.Name!="StateLabel"||!t.IsVisible));
            Check("自動播放下一首位於重唱右側，預設關閉",BoundsOnWindow(AutoPlayNextToggle).Left>=BoundsOnWindow(RestartButton).Right&&!_voice.AutoPlayNext&&UiElements<TextBlock>(AutoPlayNextToggle).All(t=>t.Name!="StateLabel"||!t.IsVisible));
            Check("待播清單位於右側並保留完整捲動高度",BoundsOnWindow(QueueArea).Left>=BoundsOnWindow(Catalog).Left&&QueueList.ActualHeight>300);
            await CaptureAsync(Path.Combine(folder,"single-machine-queue.png"));SongLibraryTab.IsSelected=true;await Layout();
            var marquee=UiElements<MarqueeText>(item).Single();string originalTitle=marquee.Text;
            Check("能完整顯示的歌名不捲動",!marquee.Scrolling&&marquee.ToolTip==null);
            marquee.Text="慢速捲動測試歌曲這是一段需要完整顯示的加長歌名";await Layout();
            bool motion=UiMotion.GetAllowAnimation(this);
            Check("長歌名依系統動畫偏好顯示且不改變歌曲庫寬度",marquee.Scrolling==motion&&Math.Abs(Catalog.ActualWidth/MainArea.ActualWidth-.3)<.005);
            if(motion)
            {
                double cycle=marquee.CycleSeconds;await Task.Delay(1600);
                Check("長歌名以慢速向左顯示",marquee.ScrollOffset< -3);
                await CaptureAsync(Path.Combine(folder,"library-marquee.png"));await Task.Delay(TimeSpan.FromSeconds(Math.Max(0,cycle-1.6)+.3));
                Check("歌名顯示完畢後會回到開頭循環",Math.Abs(marquee.ScrollOffset)<1);
            }
            UiMotion.SetAllowAnimation(this,false);await Layout();await Task.Delay(100);
            Check("減少動畫時長歌名停止移動，仍保留完整可讀取名稱",!marquee.Scrolling&&marquee.ScrollOffset==0&&AutomationProperties.GetName(marquee)==marquee.Text&&((TextBlock)marquee.Child).TextTrimming==TextTrimming.CharacterEllipsis);
            ClearValue(UiMotion.AllowAnimationProperty);await Layout();
            marquee.Text=originalTitle;await Layout();
            Check("歌曲列只有加入與從歌曲庫移除，圖示及懸停文字一致",actions.Length==2&&Equals(actions[0].ToolTip,"加入待播清單")&&Equals(actions[1].ToolTip,"從歌曲庫移除")&&UiElements<Icon>(actions[0]).Single().Kind=="plus"&&UiElements<Icon>(actions[1]).Single().Kind=="trash");
            await VerifyQueueFeedbackAsync(folder,Check);
            var remove=new RemoveSongDialog(this,catalogRow.Song);remove.Show();await Dispatcher.InvokeAsync(remove.UpdateLayout,DispatcherPriority.ApplicationIdle);
            try
            {
                inventory.Add(UiInventory(remove));var buttons=UiElements<Button>(remove).Where(b=>b.IsVisible).ToArray();
                Check("刪除對話框使用直角外框",((Border)remove.Content).CornerRadius==new CornerRadius(0)&&SquareWindowPreference(remove));
                Check("刪除確認只有取消及確認刪除，沒有第二個叉叉",buttons.Length==2&&buttons.Select(b=>b.Content).SequenceEqual(new object[]{"取消","確認刪除"})&&UiElements<Icon>(remove).Count()==0);
                Check("刪除欄位沒有懸停文字，顯示封面及輸入提示",((TextBox)remove.FindName("ConfirmationInput")).ToolTip==null&&UiElements<TextBlock>(remove).Any(t=>t.Text==catalogRow.Song.Initial)&&UiElements<TextBlock>(remove).Any(t=>t.Text=="歌曲名稱"));
                CaptureDialog(remove,Path.Combine(folder,"contract-delete.png"));
            }
            finally{remove.Close();}
            var voice=new VoiceSettingsDialog(this,_voice,_references.Model,_phone,_localInput,_store.DataDirectory);voice.Show();await Dispatcher.InvokeAsync(voice.UpdateLayout,DispatcherPriority.ApplicationIdle);
            try
            {
                inventory.Add(UiInventory(voice));var tabs=(TabControl)voice.FindName("VoiceTabs");var tab=(TabItem)tabs.Items[0];
                alignment=ButtonAlignmentIssues(voice);Check("設定視窗按鈕的圖示與文字置中且未裁切",alignment.Length==0,alignment);
                Check("設定對話框使用直角外框",((Border)voice.Content).CornerRadius==new CornerRadius(0)&&SquareWindowPreference(voice));
                Check("語音、評分與顯示分頁沿用強調色，沒有獨立手機頁籤",tabs.Items.Count==3&&voice.FindName("DisplayTab")!=null&&voice.FindName("PhoneTab")==null&&tab.IsSelected&&Equals(tab.Foreground,FindResource("Accent"))&&voice.FindName("ScoringDifficultyInput")!=null);
                var openData=(Button)voice.FindName("OpenDataDirectoryButton");
                Check("設定保留麥克風音量，僅資料入口提供儲存位置提示",!UiElements<Icon>(voice).Any(i=>i.IsVisible&&i.Kind is "x" or "close")&&UiElements<Slider>(voice).All(s=>s.Name=="LocalVolume")&&Equals(openData.ToolTip,"開啟歌曲庫、演唱錄音、評分紀錄與設定的儲存位置")&&UiElements<FrameworkElement>(voice).All(e=>e.ToolTip==null||e==openData));
                Check("語音設定按鈕沿用加減及收音圖示",UiElements<Icon>((Button)voice.FindName("DelayDecreaseButton")).Single().Kind=="minus"&&UiElements<Icon>((Button)voice.FindName("DelayIncreaseButton")).Single().Kind=="plus"&&UiElements<Icon>((Button)voice.FindName("TestMicrophoneButton")).Single().Kind=="audio-lines");
                CaptureDialog(voice,Path.Combine(folder,"contract-settings.png"));
                ((TabItem)voice.FindName("DisplayTab")).IsSelected=true;await Dispatcher.InvokeAsync(voice.UpdateLayout,DispatcherPriority.ApplicationIdle);
                var subtitleFont=(Slider)voice.FindName("SubtitleFontSize");
                Check("顯示頁籤提供歌詞字體大小與恢復預設",subtitleFont.IsVisible&&AutomationProperties.GetName(subtitleFont)=="歌詞字體大小"&&subtitleFont.Minimum==75&&subtitleFont.Maximum==125&&subtitleFont.Value==_voice.SubtitleFontPercent&&((Button)voice.FindName("ResetSubtitleFontButton")).IsVisible
                    &&UiElements<TextBlock>(voice).Any(t=>t.Text=="歌詞字體大小")&&UiElements<TextBlock>(voice).All(t=>!t.Text.Contains("字幕")));
                CaptureDialog(voice,Path.Combine(folder,"contract-display-settings.png"));
                tab.IsSelected=true;await Dispatcher.InvokeAsync(voice.UpdateLayout,DispatcherPriority.ApplicationIdle);
                var devices=(ComboBox)voice.FindName("MicrophoneInput");
                Check("麥克風選單完整顯示目前選取名稱",((TextBlock)devices.Template.FindName("SelectionText",devices)).Text==devices.SelectedItem?.ToString());
                var deviceItems=(List<OpenKaraoke.Scoring.MicrophoneDevice>)devices.ItemsSource;
                var longDevice=new OpenKaraoke.Scoring.MicrophoneDevice(_voice.DeviceId,"麥克風陣列（Realtek High Definition Audio）－USB 專業錄音介面與外接會議麥克風");deviceItems.RemoveAll(d=>d.Id==longDevice.Id);deviceItems.Add(longDevice);devices.Items.Refresh();devices.SelectedValue=longDevice.Id;
                devices.Focus();devices.IsDropDownOpen=true;await Dispatcher.InvokeAsync(voice.UpdateLayout,DispatcherPriority.ApplicationIdle);
                var deviceText=(TextBlock)devices.Template.FindName("SelectionText",devices);var dropdown=(Border)devices.Template.FindName("PopupSurface",devices);
                Check("長裝置名稱不擠壓箭頭或讓選單超出欄位",deviceText.Text==longDevice.Name&&deviceText.TextTrimming==TextTrimming.CharacterEllipsis&&deviceText.ActualWidth<=devices.ActualWidth-40&&Math.Abs(dropdown.ActualWidth-devices.ActualWidth)<1);
                devices.IsDropDownOpen=false;
                var deviceError=(TextBlock)voice.FindName("DialogError");
                deviceError.Text="無法開啟麥克風：裝置目前無法使用。\n裝置：麥克風陣列（Realtek High Definition Audio）－外接專業錄音介面。\nAudioClient.Initialize: AUDCLNT_E_DEVICE_INVALIDATED\n裝置識別碼：{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}\n呼叫來源：NAudio.CoreAudioApi.AudioClient.Initialize\n呼叫來源：NAudio.Wave.WasapiCapture.StartRecording\n呼叫來源：OpenKaraoke.Scoring.MicrophoneCapture.StartAsync\n請檢查音訊裝置連線與使用中的程式，再重新測試收音。";
                await Dispatcher.InvokeAsync(voice.UpdateLayout,DispatcherPriority.ApplicationIdle);
                var errorViewport=(ScrollViewer)voice.FindName("DialogErrorViewport");var apply=(Button)voice.FindName("ApplyButton");
                CaptureDialog(voice,Path.Combine(folder,"contract-settings-long-error.png"));
                Check("設定長錯誤可捲動，套用與取消仍在視窗內",errorViewport.IsVisible&&errorViewport.ActualHeight<=96&&errorViewport.ExtentHeight>errorViewport.ViewportHeight&&apply.IsVisible&&apply.TranslatePoint(new Point(0,apply.ActualHeight),voice).Y<voice.ActualHeight,
                    new{errorViewport.IsVisible,errorViewport.ActualHeight,errorViewport.ExtentHeight,errorViewport.ViewportHeight,ApplyVisible=apply.IsVisible,ApplyBottom=apply.TranslatePoint(new Point(0,apply.ActualHeight),voice).Y,WindowHeight=voice.ActualHeight});
            }
            finally{voice.Close();}
            var saved=_queue.Entries.ToArray();
            try
            {
                QueueTab.IsSelected=true;await Layout();
                for(int i=0;i<25;i++)_queue.Add(catalogRow.Song);
                RefreshQueue();await Layout();var scroll=UiElements<ScrollViewer>(QueueList).First();scroll.ScrollToEnd();await Layout();double offset=scroll.VerticalOffset;
                await _player.SetPausedAsync(true);Render(_player.State);await Layout();
                Check("待播清單捲動後暫停不跳回頂端",offset>0&&Math.Abs(scroll.VerticalOffset-offset)<.1,new{Before=offset,After=scroll.VerticalOffset});
                Check("暫停後懸停文字切換為播放",Equals(PauseButton.ToolTip,"播放"));
                var row=QueueList.Items.Cast<QueueRow>().Last();QueueList.ScrollIntoView(row);await Layout();
                var pending=(ListBoxItem)QueueList.ItemContainerGenerator.ContainerFromItem(row);var allTools=UiElements<Button>(pending).Where(b=>b.IsVisible).ToArray();
                Check("待播列時間旁只有合併回顧入口，未演唱前停用",allTools.Length==4&&!allTools[0].IsEnabled&&AutomationProperties.GetName(allTools[0])=="回顧演唱");
                var tools=allTools.Where(b=>b.ToolTip==null).ToArray();
                Check("待播列上移、下移、移除只有圖示且沒有懸停文字",tools.Length==3&&tools.All(b=>b.ToolTip==null)&&tools.Select(b=>UiElements<Icon>(b).Single().Kind).SequenceEqual(new[]{"up","down","close"}));
                int at=_queue.Entries.FindIndex(e=>e.Id==row.Id);tools[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Layout();
                Check("待播上移按鈕實際調整順序",_queue.Entries.FindIndex(e=>e.Id==row.Id)==at-1);
                row=QueueList.Items.Cast<QueueRow>().Single(r=>r.Id==row.Id);QueueList.ScrollIntoView(row);await Layout();
                tools=UiElements<Button>((ListBoxItem)QueueList.ItemContainerGenerator.ContainerFromItem(row)).Where(b=>b.IsVisible&&b.ToolTip==null).ToArray();tools[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("移除只影響指定待播項目",_queue.Entries.All(e=>e.Id!=row.Id)&&_queue.CurrentId==saved.Single(e=>e.Status=="playing").Id);
                int last=_queue.Entries.FindLastIndex(e=>e.Status=="pending");_queue.Entries[last]=_queue.Entries[last] with{Status="played"};RefreshQueue();
                var played=QueueList.Items.Cast<QueueRow>().Single(r=>r.Id==_queue.Entries[last].Id);QueueList.ScrollIntoView(played);await Layout();
                var playedItem=(ListBoxItem)QueueList.ItemContainerGenerator.ContainerFromItem(played);
                Check("已播放列灰色且只保留合併回顧入口",Equals(played.TitleBrush,FindResource("Sub"))&&UiElements<Button>(playedItem).Count(b=>b.IsVisible)==1&&UiElements<Button>(playedItem).Where(b=>b.IsVisible).All(b=>AutomationProperties.GetName(b)=="回顧演唱"));
                var phases=new List<TogglePhase>();
                var phaseProperty=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ToggleStatus.PhaseProperty,typeof(CheckBox));
                EventHandler phaseChanged=(_,_)=>phases.Add(ToggleStatus.GetPhase(ShowPlayedToggle));
                phaseProperty.AddValueChanged(ShowPlayedToggle,phaseChanged);
                ShowPlayedToggle.IsChecked=false;await Layout();
                Check("隱藏已播放期間保留防重複操作機制，完成後清除附加狀態",phases.SequenceEqual(new[]{TogglePhase.Changing,TogglePhase.Disabled})&&AutomationProperties.GetItemStatus(ShowPlayedToggle)==""&&ShowPlayedToggle.IsEnabled);
                Check("隱藏已播放時歌曲庫仍標示已加入",!QueueList.Items.Cast<QueueRow>().Any(r=>r.Status=="played")&&_songs.Single(s=>s.Song.Id==catalogRow.Song.Id).IsQueued);
                Check("隱藏歷史後清單編號連續排列",QueueList.Items.Cast<QueueRow>().Select(r=>r.Order).SequenceEqual(Enumerable.Range(1,QueueList.Items.Count).Select(n=>n.ToString("00"))));
                var beforePlayedOnly=_queue.Entries.ToArray();
                for(int i=0;i<_queue.Entries.Count;i++)if(_queue.Entries[i].Song.Id==catalogRow.Song.Id)_queue.Entries[i]=_queue.Entries[i] with{Status="played"};
                RefreshQueue();await Layout();
                Check("只有已播放紀錄的歌曲庫歌名恢復一般文字色",!_songs.Single(s=>s.Song.Id==catalogRow.Song.Id).IsQueued&&Equals(_songs.Single(s=>s.Song.Id==catalogRow.Song.Id).TitleBrush,FindResource("Text")));
                _queue.Entries.Clear();_queue.Entries.AddRange(beforePlayedOnly);RefreshQueue();await Layout();
                Check("仍有待播或播放中紀錄時繼續標示歌曲",_songs.Single(s=>s.Song.Id==catalogRow.Song.Id).IsQueued);
                phases.Clear();ShowPlayedToggle.IsChecked=true;phaseProperty.RemoveValueChanged(ShowPlayedToggle,phaseChanged);await Layout();
                Check("顯示已播放只以開關本身表達選取狀態",phases.SequenceEqual(new[]{TogglePhase.Changing,TogglePhase.Enabled})&&!UiElements<TextBlock>(ShowPlayedToggle).Any(t=>t.Name=="StateLabel")&&AutomationProperties.GetItemStatus(ShowPlayedToggle)=="");
                var nextEntry=_queue.Next()!;int nextAt=_queue.Entries.FindIndex(e=>e.Id==nextEntry.Id);
                var longSong=nextEntry.Song with { Title="音樂會現場完整演出：回到最初相遇的地方（特別合唱與延伸編曲版本）",Artist="OpenKaraoke 合唱驗收歌手、現場演奏樂團與特別來賓共同演出",Duration=3661 };
                _queue.Entries[nextAt]=nextEntry with{Song=longSong};RefreshQueue();
                var longRow=QueueList.Items.Cast<QueueRow>().Single(r=>r.Id==nextEntry.Id);QueueList.ScrollIntoView(longRow);await Layout();
                var longItem=(ListBoxItem)QueueList.ItemContainerGenerator.ContainerFromItem(longRow);
                var rowText=UiElements<TextBlock>(longItem).ToArray();var longTitle=rowText.Single(t=>t.Text==longSong.Title);var longArtist=rowText.Single(t=>t.Text==longSong.Artist);var clock=rowText.Single(t=>t.Text==longSong.Clock);var badge=rowText.Single(t=>t.Text=="下一首");
                Check("待播長歌名、歌手與一小時時間不擠掉狀態或四個操作",longTitle.TextTrimming==TextTrimming.CharacterEllipsis&&longArtist.TextTrimming==TextTrimming.CharacterEllipsis&&BoundsOnWindow(longTitle).Right<=BoundsOnWindow(badge).Left&&clock.ActualWidth+.5>=clock.DesiredSize.Width&&UiElements<Button>(longItem).Count(b=>b.IsVisible)==4&&UiElements<Button>(longItem).Count(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="回顧演唱")==1);
                await CaptureAsync(Path.Combine(folder,"queue-long-text.png"));
            }
            finally
            {
                _queue.Entries.Clear();_queue.Entries.AddRange(saved);ShowPlayedToggle.IsChecked=true;_queue.Save();RefreshQueue();await _player.SetPausedAsync(false);Render(_player.State);SongLibraryTab.IsSelected=true;await Layout();
            }
            var allowed=expected.Values.Concat(new[]{"加入待播清單","從歌曲庫移除"}).ToHashSet();
            Check("主視窗沒有未確認的額外懸停文字",UiElements<FrameworkElement>(this).Where(e=>e.IsVisible&&e.ToolTip!=null).All(e=>e.ToolTip is string text&&allowed.Contains(text)));
            await File.WriteAllTextAsync(Path.Combine(folder,"ui-contract-report.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks,Inventory=inventory},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(folder,"ui-contract-report.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Inventory=inventory,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));throw;
        }
    }

    private async Task VerifyEmptyInterfaceAsync(string report)
    {
        var checks=new List<object>();
        void Check(string name,bool passed,object? evidence=null){checks.Add(new{Name=name,Passed=passed,Evidence=evidence});if(!passed)throw new InvalidOperationException(name);}
        try
        {
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("初次啟動不預填示範歌曲",_songs.Count==0&&_queue.Entries.Count==0&&_queue.CurrentSong==null);
            Check("沒有歌曲時顯示待機山景並隱藏原生影片",IdleOverlay.IsVisible&&IdleScene.IsVisible&&VideoHost.Visibility==Visibility.Hidden&&IdleScene.IsAnimating);
            double started=IdleScene.CyclePositionSeconds;await Task.Delay(250);
            Check("待機晝夜循環持續運作",IdleScene.CyclePositionSeconds>started);
            IdleScene.Visibility=Visibility.Collapsed;
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);await Task.Delay(100);
            double paused=IdleScene.CyclePositionSeconds;await Task.Delay(200);
            Check("待機畫面隱藏後移除動畫時鐘並保留進度",!IdleScene.IsAnimating&&!IdleScene.HasAnimationClock&&Math.Abs(IdleScene.CyclePositionSeconds-paused)<.001,new{IdleScene.IsVisible,IdleScene.IsAnimating,IdleScene.HasAnimationClock,Before=paused,After=IdleScene.CyclePositionSeconds});
            IdleScene.Visibility=Visibility.Visible;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            UiMotion.SetAllowAnimation(IdleScene,false);
            Check("停用動態效果時待機畫面維持靜態",!IdleScene.IsAnimating);
            UiMotion.SetAllowAnimation(IdleScene,true);
            var folder=Path.GetDirectoryName(report)!;
            await VerifyIdleLoopAsync(Check,folder);
            IdleScene.PreviewPhase(.25);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);await CaptureAsync(Path.Combine(folder,"idle-dusk.png"));
            IdleScene.PreviewPhase(.5);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);await CaptureAsync(Path.Combine(folder,"idle-night.png"));
            IdleScene.PreviewPhase(0);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);await CaptureAsync(Path.Combine(folder,"idle-day.png"));
            IdleScene.ResumeAnimation();
            WindowState=WindowState.Minimized;Check("最小化時暫停待機動畫",!IdleScene.IsAnimating);WindowState=WindowState.Normal;
            QueueTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("空白清單顯示指定文字且沒有多餘符號",QueueEmpty.Text=="目前沒有播放任何歌曲"&&QueueEmpty.IsVisible&&UiElements<Icon>(QueueArea).Count(i=>i.IsVisible)==0);
            SongLibraryTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("歌曲庫預設熱門歌曲及空庫提示",PopularLabel.IsVisible&&LibraryEmpty.Text=="尚未加入歌曲"&&LibraryEmpty.IsVisible);
            Check("空白搜尋沒有清除叉叉",!ClearSearchButton.IsVisible);
            Check("沒有歌曲時停用播放及升降調按鈕",new[]{PauseButton,CutButton,RestartButton,KeyDownButton,KeyUpButton,KeyResetButton}.All(b=>!b.IsEnabled));
            Check("初始音量 100%，原唱與伴奏內容均完整呈現",VolumeSlider.Value==100&&VolumeSlider.Minimum==0&&VolumeSlider.Maximum==100&&OriginalButton.IsVisible&&InstrumentalButton.IsVisible&&UiElements<Icon>(OriginalButton).Single().Kind=="mic"&&UiElements<Icon>(InstrumentalButton).Single().Kind=="music"&&OriginalButton.ActualWidth>40&&InstrumentalButton.ActualWidth>40);
            await CaptureAsync(Path.Combine(Path.GetDirectoryName(report)!,"empty-window.png"));
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{Passed=true,Checks=checks,Inventory=UiInventory(this)},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));}
        finally{Close();}
    }
}

