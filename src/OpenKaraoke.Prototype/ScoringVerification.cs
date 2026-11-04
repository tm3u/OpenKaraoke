using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyScoringAsync(string report)
    {
        var checks = new List<object>(); string folder = Path.GetDirectoryName(report)!;
        void Check(string name, bool passed, object? evidence = null)
        {
            checks.Add(new { Name = name, Passed = passed, Evidence = evidence });
            if (!passed) throw new InvalidOperationException(name);
        }
        async Task Wait(Func<bool> condition, int timeout = 9000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("評分驗收等待逾時。"); await Task.Delay(30); }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        try
        {
            await using var silent = new OpenKaraoke.Player.MpvConnection(_player.PipeName); await silent.ConnectAsync(); await silent.SetAsync("mute", true);
            using(var model=new PitchDetector(_references.Model))
            {
                var tone=Enumerable.Range(0,16000).Select(i=>(float)(.2*Math.Sin(2*Math.PI*440*i/16000))).ToArray();
                Check("隨附模型及原生推論元件可載入",model.Detect(tone)[20..40].All(f=>f.Voiced&&Math.Abs(f.Midi-69)<.25));
            }
            VerifyPitchGuide(Check, folder);
            await VerifyPerformanceAsync(Check,folder);
            _settingsOpen = true;
            var song = _queue.CurrentSong!; _queue.Add(song); RefreshQueue();
            _voice = new VoiceSettings(ScoringEnabled: true);
            EchoUpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("增加迴聲立即套用兩類來源並保存，不改變升降調",_voice.EchoLevel==1&&VoiceSettings.Load(VoiceSettingsPath).EchoLevel==1&&_phone.Mixer.EchoLevel==1&&_localInput.Mixer.EchoLevel==1&&_player.State.Key==0&&EchoResetButton.Content.Equals("+1"));
            for(int step=0;step<5;step++)EchoUpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("迴聲最大值停用增加按鈕",_voice.EchoLevel==3&&!EchoUpButton.IsEnabled);
            EchoResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("正常按鈕恢復預設適量迴聲",_voice.EchoLevel==0&&_phone.Mixer.EchoLevel==0&&_localInput.Mixer.EchoLevel==0&&EchoResetButton.Content.Equals("正常"));
            for(int step=0;step<5;step++)EchoDownButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("迴聲最小值停用減少按鈕",_voice.EchoLevel==-3&&!EchoDownButton.IsEnabled);EchoResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            int[] melody=[0,2,4,5,7,5,4,2];
            var frames = Enumerable.Range(0, (int)(_player.State.Duration / .016)).Select(i => new PitchFrame(i * .016, 440*Math.Pow(2,melody[(int)(i*.016)%melody.Length]/12d), 1)).ToArray();
            _reference = ReferenceTrack.FromFrames(frames, _player.State.Duration); _scoreSession = new SongScoreSession(_reference);
            Render(_player.State);
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("評分開關控制 MV 下方的分數及旋律橫條", ScoringToggle.IsChecked==true && ScoreBody.IsVisible && MelodyGuide.IsVisible&&BoundsOnWindow(ScoringBar).Top>=BoundsOnWindow(VideoFrame).Bottom);
            Check("評分啟用完成後隱藏狀態文字",UiElements<TextBlock>(ScoringToggle).All(t=>t.Name!="StateLabel"||!t.IsVisible));
            ScoringToggle.IsChecked=false;Check("關閉評分時防止重複操作且不附加狀態文字",ToggleStatus.GetPhase(ScoringToggle)==TogglePhase.Changing&&!ScoringToggle.IsEnabled&&!UiElements<TextBlock>(ScoringToggle).Any(t=>t.Name=="StateLabel"));
            await Wait(()=>!_voice.ScoringEnabled&&!_busy&&!ToggleStatus.IsChanging(ScoringToggle));await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("關閉評分隱藏卡片並儲存停用狀態",!ScoreBody.IsVisible&&ScoringBar.Visibility==Visibility.Collapsed&&!VoiceSettings.Load(VoiceSettingsPath).ScoringEnabled);
            Check("關閉評分不停止電腦麥克風監聽",_localInput.Running&&_localInput.Audio.Connected&&_microphone==null);
            ScoringToggle.IsChecked=true;Check("開啟評分時防止重複操作且不附加狀態文字",ToggleStatus.GetPhase(ScoringToggle)==TogglePhase.Changing&&!ScoringToggle.IsEnabled&&!UiElements<TextBlock>(ScoringToggle).Any(t=>t.Name=="StateLabel"));
            ScoringToggle.IsChecked=false;Check("評分切換中忽略重複操作",ScoringToggle.IsChecked==true);
            await Wait(()=>_voice.ScoringEnabled&&!_busy&&!ToggleStatus.IsChanging(ScoringToggle));await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);Check("開啟評分恢復原位卡片並儲存啟用狀態",ScoreBody.IsVisible&&ScoringToggle.IsChecked==true&&VoiceSettings.Load(VoiceSettingsPath).ScoringEnabled&&ToggleStatus.GetPhase(ScoringToggle)==TogglePhase.Enabled&&ScoringToggle.IsEnabled);
            var loadedReference=_reference;_reference=null;RenderScoring();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("歌曲缺少評分基準時顯示原因",ScoringMessage.Text=="這首歌缺少評分基準，請重新匯入"&&!ScoreBody.IsVisible);
            await CaptureAsync(Path.Combine(folder,"scoring-no-reference.png"));_reference=loadedReference;RenderScoring();
            Check("設定按鈕位於最小化左側", SettingsButton.TranslatePoint(new(), this).X < MaximizeButton.TranslatePoint(new(), this).X - 46);
            await _player.SetPausedAsync(true); await Wait(() => _player.State.Paused); Render(_player.State);
            Check("暫停不顯示已暫停文字", ScoringMessage.Text == "");
            Check("歌曲暫停仍保留電腦麥克風監聽",_localInput.Running&&_localInput.Audio.Connected);
            double pausedAt=_player.State.Position;
            foreach(string key in new[]{"LEFT","RIGHT","HOME","END"})await silent.CommandAsync("keypress",key);
            await Task.Delay(100);
            Check("播放器不提供鍵盤跳轉，暫停位置維持不變",_player.State.Paused&&Math.Abs(_player.State.Position-pausedAt)<.04&&FindName("PositionSlider")==null);
            _microphoneFailed=true;_scoringError="未連接麥克風";RenderScoring();
            Check("未連接麥克風顯示停用圖示並隱藏無效分數",ScoringStateIcon.Kind=="mic-off"&&ScoringStateIcon.IsVisible&&ScoreBody.Visibility==Visibility.Collapsed&&MelodyGuide.Visibility==Visibility.Collapsed);
            _scoringError="麥克風分析發生錯誤："+string.Concat(Enumerable.Repeat("本次評分資料不完整，請檢查收音來源與裝置連線。",5));RenderScoring();
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("長評分錯誤在卡片內換行，不侵入歌曲庫",ScoringMessage.ActualHeight>20&&BoundsOnWindow(ScoringMessage).Right<=BoundsOnWindow(ScoringBar).Right&&BoundsOnWindow(ScoringMessage).Bottom<=BoundsOnWindow(ScoringBar).Bottom);
            _microphoneFailed=false;_scoringError="";RenderScoring();
            foreach (var size in new[] { new Size(1280, 720), new Size(1600, 900) })
            {
                Width = size.Width; Height = size.Height; await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
                QueueTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
                var metrics = BoundsOnWindow(ScoreBody); var guide = BoundsOnWindow(MelodyGuide);
                Check($"{size.Width}×{size.Height} 旋律使用整個卡片寬度且保留清單空間", Math.Abs(guide.Width-metrics.Width)<1 && QueueList.ActualHeight >= 130,
                    new { QueueHeight = QueueList.ActualHeight, MelodyHeight = MelodyGuide.ActualHeight, VideoHeight = VideoHost.ActualHeight });
                Check($"{size.Width}×{size.Height} 原生影片不覆蓋評分卡片", Math.Abs(BoundsOnWindow(VideoHost).Height-BoundsOnWindow(VideoFrame).Height)<1 && BoundsOnWindow(VideoHost).Bottom<=BoundsOnWindow(ScoringBar).Top);
                SongLibraryTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            }
            await _player.SeekAsync(6.2);await Wait(()=>Math.Abs(_player.State.Position-6.2)<.2);
            foreach(var frame in frames.Where(f=>f.Time<=6.2))_scoreSession.Add(frame,0);
            Render(_player.State);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("演唱中不公布分數，保留整幅旋律譜",FindName("LivePitch")==null&&FindName("ScoreMetrics")==null&&_scoreSession.Result(CurrentPosition).Pitch==null&&MelodyGuide.IsVisible);
            await CaptureAsync(Path.Combine(folder,"scoring-player.png"));
            var dialog = new VoiceSettingsDialog(this, _voice, _references.Model,_phone,_localInput,_store.DataDirectory);
            Exception? dialogError = null;
            dialog.Loaded += async (_, _) =>
            {
                try
                {
                Check("設定提供語音、評分與顯示頁籤，電腦音源隱藏手機區塊", ((TabControl)dialog.FindName("VoiceTabs")).Items.Count == 3&&dialog.FindName("DisplayTab")!=null&&dialog.FindName("PhoneTab")==null&&((PhoneSettingsPanel)dialog.FindName("PhonePanel")).Visibility==Visibility.Collapsed&&!((PhoneSettingsPanel)dialog.FindName("PhonePanel")).IsEnabled);
                ((TextBox)dialog.FindName("DelayInput")).Text = "1001";
                Check("延遲補償拒絕超過 1000 毫秒", !((Button)dialog.FindName("ApplyButton")).IsEnabled);
                ((TextBox)dialog.FindName("DelayInput")).Text = "180";
                ((Button)dialog.FindName("DelayIncreaseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check("延遲補償按鈕每次增加 10 毫秒",((TextBox)dialog.FindName("DelayInput")).Text=="190");
                ((Button)dialog.FindName("DelayDecreaseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
                CaptureDialog(dialog, Path.Combine(folder, "scoring-settings.png"));
                ((TabItem)dialog.FindName("ScoringTab")).IsSelected=true;
                var difficulty=(ComboBox)dialog.FindName("ScoringDifficultyInput");
                Check("設定提供三種難度且預設休閒",difficulty.Items.Count==3&&Equals(difficulty.SelectedValue,ScoringDifficulty.Casual));
                difficulty.SelectedValue=ScoringDifficulty.Professional;
                await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
                CaptureDialog(dialog,Path.Combine(folder,"scoring-difficulty.png"));
                ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch(Exception error) { dialogError=error;dialog.DialogResult=false; }
            };
            Check("語音設定套用選擇的延遲", dialog.ShowDialog() == true && dialog.Value.DelayMilliseconds == 180);
            Check("按套用才保存選取的難度，當次演唱難度維持不變",dialog.Value.Difficulty==ScoringDifficulty.Professional&&_scoreSession?.Difficulty==ScoringDifficulty.Casual);
            if(dialogError!=null)throw dialogError;
            var cancelled=new VoiceSettingsDialog(this,dialog.Value,_references.Model,_phone,_localInput,_store.DataDirectory);
            cancelled.Loaded+=(_,_)=>{((TextBox)cancelled.FindName("DelayInput")).Text="600";((ComboBox)cancelled.FindName("ScoringDifficultyInput")).SelectedValue=ScoringDifficulty.Advanced;((Button)cancelled.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));};
            Check("取消語音設定保留音量、延遲與原難度",cancelled.ShowDialog()!=true&&cancelled.Value.DelayMilliseconds==180&&cancelled.Value.Difficulty==ScoringDifficulty.Professional);
            await _player.ChangeKeyAsync(2); await Wait(() => _player.State.Key == 2);
            await _player.SelectTrackAsync(song.OriginalTrack); await _player.SelectTrackAsync(song.InstrumentalTrack);
            Check("評分模式保留原唱伴奏及升降調", _player.State.TrackId == song.InstrumentalTrack && _player.State.Key == 2 && Math.Abs(_player.State.Speed - 1) < .001);
            await _player.ResetKeyAsync();
            _voice=_voice with{AutoPlayNext=false};_voice.Save(VoiceSettingsPath);
            _scoreSession=new SongScoreSession(_reference,_voice.Difficulty);
            foreach(var frame in frames)_scoreSession.Add(frame with{Hertz=frame.Hertz*(frame.Time<60?1:Math.Pow(2,4d/12))},0);
            await _player.SeekAsync(_player.State.Duration-.3);await _player.SetPausedAsync(false);
            await Wait(()=>_songFlow==SongFlow.Results&&!_busy);
            Check("真正 EOF 顯示音準與全曲紅綠記錄",_player.State.Ended&&ScoreResultOverlay.IsVisible&&_finalScore?.Pitch is >40 and <60&&_finishedPerformance?.Notes.Any(n=>n.Correct)==true&&_finishedPerformance.Notes.Any(n=>!n.Correct)&&_finishedPerformance.Notes.Min(n=>n.Start)<1&&_finishedPerformance.Notes.Max(n=>n.End)>115);
            Check("結算依序為歌名、歌手、單一分數及全曲記錄，沒有操作按鈕",UiElements<Button>(ScoreResultOverlay).Count()==0&&BoundsOnWindow(ResultTitle).Bottom<=BoundsOnWindow(ResultArtist).Top&&BoundsOnWindow(ResultArtist).Bottom<=BoundsOnWindow(ResultPitch).Top&&BoundsOnWindow(ResultPitch).Bottom<=BoundsOnWindow(ResultOverview).Top);
            Check("結算記錄也維持固定時間比例並可左右捲動",Math.Abs(ResultOverview.ActualWidth-_player.State.Duration*80)<1&&ResultRecordViewport.ScrollableWidth>0&&ResultOverview.OriginalNotes.Length>0);
            Check("結算時底部可控制下一首與重唱，切歌停用",RestartButton.IsEnabled&&!CutButton.IsEnabled&&PauseButton.IsEnabled&&PlayIcon.Kind=="next"&&Equals(PauseButton.ToolTip,"下一首")&&FindName("StopButton")==null);
            var before=new Rect(Left,Top,Width,Height);var videoBounds=BoundsOnWindow(VideoFrame);
            Check("成績只覆蓋 MV，其他介面可用",BoundsOnWindow(ScoreResultOverlay)==videoBounds&&Header.IsVisible&&Catalog.IsVisible);
            await CaptureAsync(Path.Combine(folder,"scoring-result.png"));
            await Wait(()=>_finishedPerformance?.Rendering==false);
            var performance=_finishedPerformance!;QueueTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("已演唱的待播列提供合併回顧入口",_queueRows.Any(row=>row.CanReview));
            await CaptureAsync(Path.Combine(folder,"performance-queue.png"));
            await VerifyReviewAsync(Check,folder,performance);
            await Task.Delay(10200);Check("預設關閉自動播放，超過十秒仍停留結算",_songFlow==SongFlow.Results&&!_voice.AutoPlayNext);
            var timer=Stopwatch.StartNew();AutoPlayNextToggle.IsChecked=true;
            Check("自動播放開關先進入切換中並鎖住重複操作",ToggleStatus.IsChanging(AutoPlayNextToggle)&&!AutoPlayNextToggle.IsEnabled);
            await Wait(()=>_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            await Task.Delay(5500);Check("自動播放超過五秒仍保留結算，等待十秒",_songFlow==SongFlow.Results);
            await Wait(()=>_songFlow==SongFlow.Transition);
            Check("十秒後進入下一首三秒歌名畫面且沒有倒數",timer.Elapsed.TotalSeconds>=10&&_player.State.Paused&&TransitionTitle.Text==song.Title&&TransitionArtist.Text==song.Artist&&UiElements<TextBlock>(TransitionPanel).Count()==2);
            await CaptureAsync(Path.Combine(folder,"scoring-transition.png"));
            await Wait(()=>!_busy&&_songFlow==SongFlow.Playing&&!_player.State.Paused);
            Check("結算十秒加上新歌三秒靜音後才開始播放",timer.Elapsed.TotalSeconds is >=13 and <18,timer.Elapsed.TotalSeconds);
            AutoPlayNextToggle.IsChecked=false;await Wait(()=>!_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            _queue.Add(song);RefreshQueue();Render(_player.State);string cutId=_queue.CurrentId!;int pendingBeforeCut=_queue.PendingCount;
            string cutFolder=Path.Combine(_store.DataDirectory,"performances",cutId);_recording!.AcceptOutput(new float[480],48000,MicrophoneCapture.Now);
            await Wait(()=>Directory.EnumerateFiles(cutFolder,"*.wav").Any());
            _scoreSession=new(ReferenceTrack.FromFrames(frames,_player.State.Duration));for(int i=0;i<20;i++)_scoreSession.Add(frames[i],0);
            string previousRecording=performance.AudioFile!;byte[] previousRecordingBytes=await File.ReadAllBytesAsync(previousRecording);int tasksBeforeCut=_recordingTasks.Count;
            CutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));CutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_songFlow==SongFlow.Results&&!_busy);
            Check("切歌只結束本首並結算，不消耗下一首待播項目",_queue.CurrentId==null&&_queue.PendingCount==pendingBeforeCut&&_queue.Entries.Any(e=>e.Id==cutId&&e.Status=="played")&&_player.State.Paused);
            Check("切歌捨棄本次音準與錄音，不進行錄音合成",_finishedPerformance?.Skipped==true&&_finishedPerformance.Score==null&&_finishedPerformance.Notes.Length==0&&_finishedPerformance.AudioFile==null&&_scoreSession==null&&_recording==null&&_recordingTasks.Count==tasksBeforeCut&&!Directory.Exists(cutFolder));
            byte[] retainedRecordingBytes=await File.ReadAllBytesAsync(previousRecording);
            Check("切歌不影響先前完成的演唱錄音",previousRecordingBytes.SequenceEqual(retainedRecordingBytes));
            Check("切歌結算不提供分數與錄音入口",!_queueRows.Single(r=>r.Id==cutId).CanReview&&ResultPitch.Text=="—"&&!ResultRecordViewport.IsVisible);
            Check("切歌圖示改為結束方形，主播放鍵切為下一首",UiElements<Icon>(CutButton).Single().Kind=="square"&&!CutButton.IsEnabled&&PauseButton.IsEnabled&&PlayIcon.Kind=="next"&&Equals(PauseButton.ToolTip,"下一首"));
            await CaptureAsync(Path.Combine(folder,"cut-results.png"));
            timer.Restart();PauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));PauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_songFlow==SongFlow.Transition);
            Check("手動下一首立即進入新歌靜音畫面，不另等十秒",timer.Elapsed.TotalSeconds<2&&_player.State.Paused&&FindName("TransitionCountdown")==null&&_queue.PendingCount==pendingBeforeCut-1);
            await Wait(()=>!_busy&&_songFlow==SongFlow.Playing&&!_player.State.Paused);
            Check("手動下一首的靜音畫面為三秒",timer.Elapsed.TotalSeconds is >=3 and <6,timer.Elapsed.TotalSeconds);
            _queue.Add(song);RefreshQueue();AutoPlayNextToggle.IsChecked=true;await Wait(()=>_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            CutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_songFlow==SongFlow.Results&&!_busy);
            Check("切歌時已開啟自動播放會保留十秒結算",_resultsDeadline-MicrophoneCapture.Now is >9 and <=10&&_finishedPerformance?.Skipped==true);
            AutoPlayNextToggle.IsChecked=false;await Wait(()=>!_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            Check("結算期間關閉自動播放會取消待換歌計時",_resultsDeadline==0&&_songFlow==SongFlow.Results);
            AutoPlayNextToggle.IsChecked=true;await Wait(()=>_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            timer.Restart();PauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_songFlow==SongFlow.Transition);
            Check("啟用自動播放時仍可手動略過十秒等待",timer.Elapsed.TotalSeconds<2&&_resultsDeadline==0);
            await RestartAsync();Check("重唱可取消新歌畫面並重新給三秒準備時間",_songFlow==SongFlow.Playing&&!_player.State.Paused&&!_busy&&_player.State.Position<1);
            AutoPlayNextToggle.IsChecked=false;await Wait(()=>!_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            _voice=_voice with{ScoringEnabled=false};await RestartAsync();
            _queue.Add(song);RefreshQueue();await _player.SeekAsync(_player.State.Duration-.2);await _player.SetPausedAsync(false);
            await Wait(()=>_songFlow==SongFlow.Results&&!_busy);
            Check("未開評分仍顯示結算，不捏造分數",ScoreResultOverlay.IsVisible&&ResultPitch.Text=="—"&&_finishedPerformance?.Score==null);
            timer.Restart();AutoPlayNextToggle.IsChecked=true;await Wait(()=>_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            await Wait(()=>_songFlow==SongFlow.Transition,14000);Check("未開評分時同樣保留十秒結算",timer.Elapsed.TotalSeconds>=10);
            await Wait(()=>!_busy&&_songFlow==SongFlow.Playing&&!_player.State.Paused);
            AutoPlayNextToggle.IsChecked=false;await Wait(()=>!_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            _voice=_voice with{ScoringEnabled=true};_reference=null;_scoreSession=null;_queue.Entries.RemoveAll(e=>e.Status=="pending");
            await _player.SeekAsync(_player.State.Duration-.2);await _player.SetPausedAsync(false);await Wait(()=>_songFlow==SongFlow.Results&&!_busy);
            Check("沒有評分資料不捏造分數",ResultPitch.Text=="—"&&(ResultPitch.ToolTip as string)=="尚未建立評分資料");
            Check("最後一首透過底部控制重唱，沒有結算區按鈕",!CutButton.IsEnabled&&RestartButton.IsEnabled&&!UiElements<Button>(ScoreResultOverlay).Any());
            Check("沒有待播歌曲時下一首仍可結束結算",_songFlow==SongFlow.Results&&_player.State.Ended&&PauseButton.IsEnabled&&PlayIcon.Kind=="next"&&_resultsDeadline==0);
            await PauseAsync();Check("最後一首按下一首回到待機，保留演唱記錄",_songFlow==SongFlow.Idle&&IdleOverlay.IsVisible&&!ScoreResultOverlay.IsVisible&&VideoHost.Visibility==Visibility.Hidden&&_performances.Count>0&&RestartButton.IsEnabled);
            await CaptureAsync(Path.Combine(folder,"idle-after-song.png"));
            await RestartAsync();Check("重唱離開結算並清除上一首畫面",_songFlow==SongFlow.Playing&&_finalScore==null&&!_player.State.Paused);
            await PauseAsync();Render(_player.State);Check("最後一首暫停時仍可切歌",_queue.PendingCount==0&&CutButton.IsEnabled&&Equals(PauseButton.ToolTip,"播放"));
            AutoPlayNextToggle.IsChecked=true;await Wait(()=>_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            timer.Restart();CutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_songFlow==SongFlow.Results&&!_busy);
            await Wait(()=>_songFlow==SongFlow.Idle,14000);
            Check("最後一首切歌後自動模式等待十秒才回待機",timer.Elapsed.TotalSeconds>=10&&IdleOverlay.IsVisible&&_finishedPerformance?.Skipped==true&&_recording==null&&!PauseButton.IsEnabled);
            AutoPlayNextToggle.IsChecked=false;await Wait(()=>!_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));await RestartAsync();
            Check("切歌後仍可重唱並建立全新的收音與錄音",_songFlow==SongFlow.Playing&&_recording!=null&&_finishedPerformance==null&&PlayIcon.Kind=="pause");
            await PauseAsync(); RenderScoring();
            _queue.Add(song with { MediaPath = Path.Combine(folder,"missing.opok") });RefreshQueue();
            string missingId=_queue.Next()!.Id;bool missingFailed=false;
            try{await AdvanceAsync();}catch(FileNotFoundException){missingFailed=true;}
            Check("下一首檔案遺失會退出過場並保留待播項目",missingFailed&&!_busy&&_songFlow==SongFlow.Playing&&SongFlowOverlay.Visibility==Visibility.Collapsed&&_stopped&&!_player.State.Loaded&&_queue.CurrentId==null&&_queue.Next()?.Id==missingId&&PauseButton.IsEnabled&&!CutButton.IsEnabled);
            _queue.Remove(missingId);_queue.Add(song);RefreshQueue();await AdvanceAsync();
            string invalidMedia=Path.Combine(folder,"invalid-test.mp4");await File.WriteAllTextAsync(invalidMedia,"invalid media fixture");
            _queue.Add(song with { MediaPath = invalidMedia });RefreshQueue();
            string invalidId=_queue.Next()!.Id;bool invalidFailed=false;
            try{await AdvanceAsync();}catch(IOException){invalidFailed=true;}
            Check("下一首解碼失敗會退出過場且不誤標為播放中",invalidFailed&&!_busy&&!_player.State.Loaded&&_songFlow==SongFlow.Playing&&SongFlowOverlay.Visibility==Visibility.Collapsed&&_queue.CurrentId==null&&_queue.Next()?.Id==invalidId);
            _queue.Remove(invalidId);_queue.Add(song);RefreshQueue();await AdvanceAsync();
            Check("切歌失敗後仍能載入並播放正常歌曲",_queue.CurrentSong?.Id==song.Id&&_queue.CurrentId!=null&&_player.State.Loaded&&!_player.State.Paused&&_songFlow==SongFlow.Playing);
            await PauseAsync();
            ScoringToggle.IsChecked=false;await Wait(()=>!ToggleStatus.IsChanging(ScoringToggle)&&!_voice.ScoringEnabled);
            string blockedSave=VoiceSettingsPath+".tmp";Directory.CreateDirectory(blockedSave);
            try
            {
                AutoPlayNextToggle.IsChecked=true;AutoPlayNextToggle.IsChecked=false;
                Check("自動播放切換期間忽略重複操作",AutoPlayNextToggle.IsChecked==true);
                await Wait(()=>!ToggleStatus.IsChanging(AutoPlayNextToggle));
                Check("自動播放保存失敗還原實際停用狀態",!_voice.AutoPlayNext&&AutoPlayNextToggle.IsChecked==false&&AutoPlayNextToggle.IsEnabled&&!VoiceSettings.Load(VoiceSettingsPath).AutoPlayNext);
                ScoringToggle.IsChecked=true;await Wait(()=>!ToggleStatus.IsChanging(ScoringToggle));
                Check("評分保存失敗恢復關閉並顯示錯誤",!_voice.ScoringEnabled&&!ScoringToggle.IsChecked.GetValueOrDefault()&&ToggleStatus.GetPhase(ScoringToggle)==TogglePhase.Disabled&&ScoringToggle.IsEnabled&&!VoiceSettings.Load(VoiceSettingsPath).ScoringEnabled&&Toast.IsVisible&&ToastText.Text.Length>0);
            }
            finally{Directory.Delete(blockedSave);}
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = true, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Log(error.ToString());
        }
        finally { _settingsOpen = false; Close(); }
    }
}

