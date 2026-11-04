using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyReviewAsync(Action<string,bool,object?> check,string folder,PerformanceResult performance)
    {
        void Check(string name,bool passed,object? evidence=null)=>check(name,passed,evidence);
        using var endpoints=new MMDeviceEnumerator();using var endpoint=endpoints.GetDefaultAudioEndpoint(DataFlow.Render,Role.Console);
        float volume=endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;bool muted=endpoint.AudioEndpointVolume.Mute;
        Exception? failure=null;
        var dialog=new PerformanceDialog(this,performance,mutedPlayback:true);
        dialog.Loaded+=async(_,_)=>
        {
            try
            {
                await Task.Delay(300);await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
                var toggle=(Button)dialog.FindName("RecordingPauseButton");var progress=(Slider)dialog.FindName("RecordingProgress");var guide=(PitchGuide)dialog.FindName("PerformanceOverview");
                string Label()=>((TextBlock)dialog.FindName("RecordingPauseLabel")).Text;
                var mapped=performance.RecordingNotes();
                Check("合併視窗使用標題圖示、分區、右上角關閉與明確分數標籤",dialog.Title=="演唱回顧"&&dialog.FindName("DialogHeader") is Border&&UiElements<Icon>((Border)dialog.FindName("DialogHeader")).Any(i=>i.Kind=="audio-lines")&&dialog.FindName("CloseDialogButton") is Button&&((TextBlock)dialog.FindName("PerformanceScoreLabel")).Text.Contains("分數")&&((TextBlock)dialog.FindName("PerformanceScore")).Text==performance.Score?.Pitch?.ToString("0"));
                Check("音準紀錄使用錄音時間，保留原始旋律與評分顏色",guide.OriginalNotes.SequenceEqual(mapped.Original)&&guide.Records.SequenceEqual(mapped.Sung)&&performance.RecordingTimeline.Length>0);
                Check("音準紀錄跟隨錄音，共用唯一時間軸且沒有獨立水平捲軸",guide.FollowPlayback&&!guide.IsHitTestVisible&&dialog.FindName("PerformanceViewport")==null&&UiElements<Slider>(dialog).Count()==1&&UiElements<ScrollViewer>(dialog).All(v=>v.HorizontalScrollBarVisibility==ScrollBarVisibility.Disabled));
                Check("錄音由實際輸出播放，音條時間同步前進",progress.Value>0&&progress.IsEnabled&&toggle.IsEnabled&&Math.Abs(guide.Position-progress.Value)<.001&&Label()=="暫停");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));double paused=progress.Value;await Task.Delay(150);
                Check("暫停時錄音、音條與時間同步停止",Label()=="繼續"&&Math.Abs(progress.Value-paused)<.001&&Math.Abs(guide.Position-paused)<.001);
                double target=progress.Maximum*.6;progress.Value=target;await Task.Delay(150);
                Check("暫停時拖曳唯一時間軸可同步定位，且不自動播放",Math.Abs(progress.Value-target)<.01&&Math.Abs(guide.Position-target)<.01&&Label()=="繼續");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                progress.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});progress.Value=progress.Maximum*.25;
                Check("拖曳期間立即預覽對應音準紀錄",Math.Abs(guide.Position-progress.Value)<.001&&Label()=="繼續");
                progress.RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});await Task.Delay(180);
                Check("完成拖曳後從選定位置恢復錄音與音條",progress.Value>=progress.Maximum*.25-.05&&progress.Value<progress.Maximum*.25+.5&&Math.Abs(guide.Position-progress.Value)<.001&&Label()=="暫停");
                progress.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});double dragOrigin=progress.Value;progress.Value=progress.Maximum*.8;
                progress.RaiseEvent(new DragCompletedEventArgs(0,0,true){RoutedEvent=Thumb.DragCompletedEvent});await Task.Delay(100);
                Check("取消拖曳回到原位置並恢復原播放狀態",Math.Abs(progress.Value-dragOrigin)<.4&&Label()=="暫停");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));progress.Value=progress.Maximum;
                Check("拖至結束時同步顯示末端並提供重播",Label()=="重播"&&Math.Abs(guide.Position-progress.Maximum)<.001);
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(180);
                Check("重播從起點同步重新開始，舊停止事件不會跳回末端",Label()=="暫停"&&progress.Value<.5&&Math.Abs(guide.Position-progress.Value)<.001);
                progress.Value=Math.Max(0,progress.Maximum-.18);await Task.Delay(650);
                Check("自然播放完畢時音準紀錄停在結尾",Label()=="重播"&&Math.Abs(progress.Value-progress.Maximum)<.001&&guide.Position==progress.Value);
                string download=Path.Combine(folder,"downloaded-recording.m4a");await dialog.SaveRecordingAsync(download);
                Check("合併視窗保留下載，內容與原始錄音一致",((Button)dialog.FindName("RecordingDownloadButton")).IsEnabled&&File.ReadAllBytes(download).SequenceEqual(File.ReadAllBytes(performance.AudioFile!)));
                bool rejected=false;try{await dialog.SaveRecordingAsync(performance.AudioFile!);}catch(InvalidOperationException){rejected=true;}
                Check("下載不可覆寫應用程式保存的原始錄音",rejected);
                progress.Value=progress.Maximum*.6;toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(120);
                CaptureDialog(dialog,Path.Combine(folder,"performance-review.png"));
                dialog.Width=800;dialog.Height=450;await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
                Check("較小回顧視窗的標題、音條及控制項仍在邊界內",guide.ActualHeight>=100&&guide.ActualWidth>450&&((Button)dialog.FindName("CloseDialogButton")).IsVisible&&progress.ActualWidth>100);
                CaptureDialog(dialog,Path.Combine(folder,"performance-review-compact.png"));
            }
            catch(Exception error){failure=error;}finally{dialog.Close();}
        };
        dialog.ShowDialog();if(failure!=null)throw failure;
        Check("測試靜音與關閉視窗不改變 Windows 喇叭音量或靜音狀態",endpoint.AudioEndpointVolume.MasterVolumeLevelScalar==volume&&endpoint.AudioEndpointVolume.Mute==muted);
        await using(var file=new FileStream(performance.AudioFile!,FileMode.Open,FileAccess.ReadWrite,FileShare.None))Check("關閉回顧釋放音訊檔案",file.Length>0);
        async Task Inspect(PerformanceResult result,string name,Action<PerformanceDialog> assertion)
        {
            var view=new PerformanceDialog(this,result,mutedPlayback:true);
            view.Loaded+=async(_,_)=>{try{await Task.Delay(150);assertion(view);await Dispatcher.InvokeAsync(view.UpdateLayout,DispatcherPriority.ApplicationIdle);CaptureDialog(view,Path.Combine(folder,name+".png"));}catch(Exception error){failure=error;}finally{view.Close();}};
            view.ShowDialog();if(failure!=null)throw failure;await Task.CompletedTask;
        }
        await Inspect(new(){Title="沒有錄音的長歌名："+string.Concat(Enumerable.Repeat("音準紀錄",9)),Artist="歌手",Duration=60,ReferenceNotes=[new(2,4,60)],Notes=[new(2,4,62,false)]},"performance-review-score-only",view=>
        {
            var progress=(Slider)view.FindName("RecordingProgress");progress.Value=25;
            Check("沒有錄音時仍可用共用時間軸檢視音條，明確標示無分數與錄音",progress.IsEnabled&&((PitchGuide)view.FindName("PerformanceOverview")).Position==25&&!((Button)view.FindName("RecordingPauseButton")).IsEnabled&&!((Button)view.FindName("RecordingDownloadButton")).IsEnabled&&((TextBlock)view.FindName("RecordingStatus")).Text=="本次沒有錄音"&&((TextBlock)view.FindName("ScoreStatus")).IsVisible);
        });
        await Inspect(new(){Title="只有錄音",Artist="歌手",Duration=performance.Duration,Complete=true,AudioFile=performance.AudioFile},"performance-review-audio-only",view=>
        {
            Check("只有錄音時仍可播放，不捏造音準與分數",((Button)view.FindName("RecordingPauseButton")).IsEnabled&&((TextBlock)view.FindName("NoPitchStatus")).IsVisible&&((TextBlock)view.FindName("PerformanceScore")).Text=="—");
        });
        await Inspect(new(){Title="錄音檔遺失",AudioFile=Path.Combine(folder,"missing.m4a")},"performance-review-missing",view=>Check("錄音遺失會顯示原因並停用播放與下載",!((Button)view.FindName("RecordingPauseButton")).IsEnabled&&!((Button)view.FindName("RecordingDownloadButton")).IsEnabled&&((TextBlock)view.FindName("RecordingError")).Text.Length>0));
        var rendering=new PerformanceResult{Title="錄音製作中",Duration=performance.Duration,Rendering=true,ReferenceNotes=performance.ReferenceNotes,Notes=performance.Notes,RecordingTimeline=performance.RecordingTimeline};
        var pending=new PerformanceDialog(this,rendering,mutedPlayback:true);
        pending.Loaded+=async(_,_)=>
        {
            try
            {
                Check("錄音製作中保留音準檢視並標示處理狀態",((TextBlock)pending.FindName("RecordingStatus")).Text.Contains("製作中")&&((Slider)pending.FindName("RecordingProgress")).IsEnabled);
                rendering.Rendering=false;rendering.AudioFile=performance.AudioFile;await Task.Delay(250);
                Check("錄音完成後不用重開視窗即可播放與同步音條",((Button)pending.FindName("RecordingPauseButton")).IsEnabled&&((Slider)pending.FindName("RecordingProgress")).Value>0);
            }
            catch(Exception error){failure=error;}finally{pending.Close();}
        };
        pending.ShowDialog();if(failure!=null)throw failure;
        string invalid=Path.Combine(folder,"invalid-recording.m4a");await File.WriteAllTextAsync(invalid,"invalid audio");
        await Inspect(new(){Title="錄音無法解碼",AudioFile=invalid,ReferenceNotes=[new(1,2,60)],Duration=5},"performance-review-invalid",view=>Check("錄音損壞仍可檢視音條與下載原檔，播放錯誤清楚顯示",!((Button)view.FindName("RecordingPauseButton")).IsEnabled&&((Button)view.FindName("RecordingDownloadButton")).IsEnabled&&((Slider)view.FindName("RecordingProgress")).IsEnabled&&((TextBlock)view.FindName("RecordingError")).Text.Contains("無法播放")));
        string reviewId=_performances.Single(p=>ReferenceEquals(p.Value,performance)).Key;
        var previousVoice=_voice;double previousDeadline=_resultsDeadline;
        bool phone=_phone.Mixer.SuppressMonitoring,local=_localInput.Mixer.SuppressMonitoring;
        string? audioFile=performance.AudioFile;performance.AudioFile=null;
        _voice=_voice with{AutoPlayNext=true};_resultsDeadline=MicrophoneCapture.Now+.15;
        _=Dispatcher.BeginInvoke(new Action(async()=>
        {
            var view=Application.Current.Windows.OfType<PerformanceDialog>().Single();
            try
            {
                await Task.Delay(400);
                Check("從待播回顧時暫停監聽及自動換歌，不會在結算誤執行下一首",_reviewOpen&&_phone.Mixer.SuppressMonitoring&&_localInput.Mixer.SuppressMonitoring&&_songFlow==SongFlow.Results);
            }
            catch(Exception error){failure=error;}finally{view.Close();}
        }));
        try
        {
            QueueReview_Click(new Button{Tag=reviewId},new RoutedEventArgs());
            if(failure!=null)throw failure;
            Check("關閉回顧還原原監聽狀態與剩餘自動換歌時間",!_reviewOpen&&_phone.Mixer.SuppressMonitoring==phone&&_localInput.Mixer.SuppressMonitoring==local&&_songFlow==SongFlow.Results&&_resultsDeadline-MicrophoneCapture.Now is >0 and <.2);
        }
        finally{performance.AudioFile=audioFile;_voice=previousVoice;_resultsDeadline=previousDeadline;}
    }
}
