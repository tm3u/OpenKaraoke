using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyInterfaceAsync(string report)
    {
        var checks=new List<object>();
        void Check(string name,bool passed,object? evidence=null)
        {
            checks.Add(new {Name=name,Passed=passed,Evidence=evidence});
            if(!passed)throw new InvalidOperationException(name);
        }
        async Task Wait(Func<bool> condition,int timeout=12000)
        {
            var clock=Stopwatch.StartNew();
            while(!condition()) { if(clock.Elapsed.TotalMilliseconds>timeout)throw new TimeoutException("介面操作未完成。");await Task.Delay(40); }
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        }
        async Task Click(Button button,Func<bool> completed)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(completed);
        }
        void CheckCurrentQueueProtected(string state)
        {
            QueueTab.IsSelected=true;UpdateLayout();
            string id=_queue.CurrentId!;
            var row=QueueList.Items.Cast<QueueRow>().Single(row=>row.Id==id);
            QueueList.ScrollIntoView(row);QueueList.UpdateLayout();
            var container=(ListBoxItem)QueueList.ItemContainerGenerator.ContainerFromItem(row);
            Button? FindRemove(DependencyObject node)
            {
                if(node is Button button&&System.Windows.Automation.AutomationProperties.GetName(button)=="從待播清單移除")return button;
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
                    if(FindRemove(VisualTreeHelper.GetChild(node,i)) is {} found)return found;
                return null;
            }
            Check(state+"的歌曲不顯示移除按鈕",FindRemove(container)?.Visibility==Visibility.Collapsed);
            int count=_queue.Entries.Count;
            QueueRemove_Click(new Button { Tag=id },new RoutedEventArgs(Button.ClickEvent));
            Check(state+"的歌曲無法透過移除事件刪除",_queue.CurrentId==id&&_queue.Entries.Count==count&&_queue.Entries.Any(entry=>entry.Id==id&&entry.Status=="playing"));
            SongLibraryTab.IsSelected=true;UpdateLayout();
        }
        try
        {
            await using var silent=new OpenKaraoke.Player.MpvConnection(_player.PipeName);await silent.ConnectAsync();await silent.SetAsync("mute",true);
            await VerifyUiContractAsync(Path.GetDirectoryName(report)!);Check("完整圖示、按鈕、懸停及清單狀態契約驗收",true);
            await VerifyIconAssetsAsync(Path.GetDirectoryName(report)!);Check("Lucide 1.17.0 圖示素材完整且可繪製",true);
            Check("歌曲庫與影片在同一視窗",Catalog.IsVisible&&VideoHost.VideoHandle!=0&&_player.State.Loaded);
            Check("音量預設 100、範圍 0 到 100",VolumeSlider.Value==100&&VolumeSlider.Minimum==0&&VolumeSlider.Maximum==100);
            Check("已移除的懸停文字維持空白",MuteButton.ToolTip==null&&ClearSearchButton.ToolTip==null&&QueueCount.ToolTip==null);
            CheckCurrentQueueProtected("播放中");
            var remove=new RemoveSongDialog(this,_queue.CurrentSong!);remove.Show();
            await Dispatcher.InvokeAsync(remove.UpdateLayout,DispatcherPriority.ApplicationIdle);
            var confirmation=(TextBox)remove.FindName("ConfirmationInput");var delete=(Button)remove.FindName("ConfirmButton");
            Check("刪除彈窗初始停用確認",!delete.IsEnabled);
            confirmation.Text="不相符的歌名";Check("刪除彈窗拒絕錯誤歌名",!delete.IsEnabled);
            confirmation.Text=_queue.CurrentSong!.Title;Check("完整歌名立即啟用單次刪除確認",delete.IsEnabled);
            await Dispatcher.InvokeAsync(remove.UpdateLayout,DispatcherPriority.ApplicationIdle);
            CaptureDialog(remove,Path.Combine(Path.GetDirectoryName(report)!,"single-machine-delete.png"));remove.Close();
            var add=CreateSongDialog();add.Show();await Dispatcher.InvokeAsync(add.UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("加入歌曲視窗左右並排且完整呈現",add.ActualHeight<SystemParameters.WorkArea.Height&&add.ActualWidth==720);
            var buttonIssues=ButtonAlignmentIssues(add);Check("製作歌曲視窗按鈕內容置中且未裁切",buttonIssues.Length==0,buttonIssues);
            CaptureDialog(add,Path.Combine(Path.GetDirectoryName(report)!,"single-machine-add.png"));
            Check("製作歌曲歌名歌手預設空白且無法直接開始",((TextBox)add.FindName("TitleInput")).Text==""&&((TextBox)add.FindName("ArtistInput")).Text==""&&!((Button)add.FindName("SubmitButton")).IsEnabled);
            await Dispatcher.InvokeAsync(add.UpdateLayout,DispatcherPriority.ApplicationIdle);
            CaptureDialog(add,Path.Combine(Path.GetDirectoryName(report)!,"single-machine-create.png"));
            add.SelectMode("import");
            string importFixture=Path.Combine(Path.GetDirectoryName(report)!,"測試歌曲_測試歌手.opok");
            await _packages.ExportAsync(_queue.CurrentSong!.MediaPath,1,2,"測試歌曲","測試歌手",importFixture);
            await add.SelectPackageAsync(importFixture);
            await Dispatcher.InvokeAsync(add.UpdateLayout,DispatcherPriority.ApplicationIdle);
            buttonIssues=ButtonAlignmentIssues(add);Check("匯入歌曲視窗按鈕內容置中且未裁切",buttonIssues.Length==0,buttonIssues);
            Check("openkaraoke 匯入依檔名自動帶入歌名歌手",((TextBox)add.FindName("TitleInput")).Text=="測試歌曲"&&((TextBox)add.FindName("ArtistInput")).Text=="測試歌手"&&((Button)add.FindName("SubmitButton")).IsEnabled);
            ((TextBox)add.FindName("ArtistInput")).Text=" ";bool missingArtist=!((Button)add.FindName("SubmitButton")).IsEnabled;
            ((TextBox)add.FindName("ArtistInput")).Text="測試歌手";((TextBox)add.FindName("TitleInput")).Text=" ";
            Check("匯入歌名與歌手都必填且純空白無效",missingArtist&&!((Button)add.FindName("SubmitButton")).IsEnabled);
            ((TextBox)add.FindName("TitleInput")).Text="測試歌曲";
            await Dispatcher.InvokeAsync(add.UpdateLayout,DispatcherPriority.ApplicationIdle);
            CaptureDialog(add,Path.Combine(Path.GetDirectoryName(report)!,"single-machine-import.png"));
            Check("加入歌曲以處理清單顯示工作",add.FindName("ProcessingList")!=null);add.Close();
            await Click(PauseButton,()=>_player.State.Paused);Check("播放按鈕可暫停",_player.State.Paused);
            CheckCurrentQueueProtected("暫停中");
            await Click(KeyUpButton,()=>_player.State.Key==1);await Click(KeyResetButton,()=>_player.State.Key==0);
            await Click(KeyDownButton,()=>_player.State.Key==-1);await Click(KeyResetButton,()=>_player.State.Key==0);
            Check("升降調與正常調重設事件正常",KeyResetButton.Content.ToString()=="正常調");
            await Click(OriginalButton,()=>_player.State.TrackId==_queue.CurrentSong!.OriginalTrack&&_queue.Mode=="original");
            await Click(InstrumentalButton,()=>_player.State.TrackId==_queue.CurrentSong!.InstrumentalTrack&&_queue.Mode=="instrumental");
            Check("原唱與伴奏實際音軌切換",_player.State.TrackId==2);
            MuteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_player.State.Volume==0);
            VolumeSlider.Value=100;await Wait(()=>_player.State.Volume==100);Check("靜音與音量控制可達 0 及 100",true);
            foreach(var size in new[]{new Size(1280,720),new Size(1600,900)})
            {
                Width=size.Width;Height=size.Height;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
                var transport=BoundsOnWindow(RestartButton);var mode=BoundsOnWindow(OriginalButton);
                var pitch=BoundsOnWindow(KeyDownButton);var volume=BoundsOnWindow(MuteButton);
                Check($"{size.Width}×{size.Height} 控制列無重疊",transport.Right<=mode.Left&&BoundsOnWindow(InstrumentalButton).Right<=pitch.Left&&BoundsOnWindow(KeyUpButton).Right<=volume.Left,
                    new{Transport=transport,Mode=mode,Pitch=pitch,Volume=volume});
                bool songsHaveSpace=SongList.ActualHeight>200;QueueTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
                Check($"{size.Width}×{size.Height} 歌曲庫與清單有捲動空間",songsHaveSpace&&QueueList.ActualHeight>300);SongLibraryTab.IsSelected=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            }
            Rect before=RestoreBounds;
            FullscreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("全螢幕保留原生影片與控制列",_fullscreen&&!Catalog.IsVisible&&!QueueArea.IsVisible&&FullscreenButton.IsVisible&&VideoHost.ActualWidth>1000);
            Check("全螢幕提示為視窗化",(string)FullscreenButton.ToolTip=="視窗化");
            await CaptureAsync(Path.Combine(Path.GetDirectoryName(report)!,"single-machine-fullscreen.png"));
            SetFullscreen(false);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("視窗化還原尺寸與內容",!_fullscreen&&Catalog.IsVisible&&Math.Abs(Width-before.Width)<2&&Math.Abs(Height-before.Height)<2);
            Check("控制列已移除停止按鈕",FindName("StopButton")==null&&!UiElements<Button>(this).Any(b=>Equals(b.ToolTip,"停止")));
            await Click(RestartButton,()=>!_busy&&_songFlow==SongFlow.Playing&&!_player.State.Paused);
            var song=_queue.CurrentSong!;
            _queue.Add(song);_queue.Add(song);RefreshQueue();Render(_player.State);
            string? previous=_queue.CurrentId;
            await Click(CutButton,()=>_songFlow==SongFlow.Results&&!_busy);
            Check("切歌進入結算，不直接播放下一首且不保留分數錄音",_finishedPerformance?.Skipped==true&&_finishedPerformance.Score==null&&_finishedPerformance.AudioFile==null&&PlayIcon.Kind=="next");
            await Click(PauseButton,()=>_queue.CurrentId!=null&&_queue.CurrentId!=previous&&!_busy&&_songFlow==SongFlow.Playing);
            Check("下一首保留已播放紀錄",_queue.Entries.Any(e=>e.Id==previous&&e.Status=="played"));
            _queue.Entries.RemoveAll(e=>e.Status=="pending");_queue.Add(song);RefreshQueue();
            previous=_queue.CurrentId;await _player.SeekAsync(_player.State.Duration-0.35);await _player.SetPausedAsync(false);
            AutoPlayNextToggle.IsChecked=true;await Wait(()=>_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            await Wait(()=>_queue.CurrentId!=null&&_queue.CurrentId!=previous&&!_busy&&_songFlow==SongFlow.Playing,18000);
            Check("實際唱完自動接播下一首",_queue.Entries.Any(e=>e.Id==previous&&e.Status=="played")&&_queue.CurrentId!=null);
            await _player.SeekAsync(_player.State.Duration-0.35);await Wait(()=>_queue.CurrentId==null&&_player.State.Ended);
            AutoPlayNextToggle.IsChecked=false;await Wait(()=>!_voice.AutoPlayNext&&!ToggleStatus.IsChanging(AutoPlayNextToggle));
            int history=_queue.Entries.Count(e=>e.Status=="played");Check("最後一首唱完保留全部歷史",history>=3);
            ShowPlayedToggle.IsChecked=false;Check("隱藏已播放只隱藏列不刪資料",QueueList.Items.Count==0&&_queue.Entries.Count==history);
            ShowPlayedToggle.IsChecked=true;Check("顯示已播放恢復全部歷史",QueueList.Items.Count==history);
            await Click(RestartButton,()=>!_player.State.Paused&&_queue.CurrentId!=null);
            Check("重播保留舊歷史並建立新的播放紀錄",_queue.Entries.Count==history+1);
            SearchBox.Text="不存在的歌曲 xyz";await Wait(()=>_searchTotal==0&&_songs.Count==0&&!_fetching);
            Check("搜尋無結果不顯示熱門歌曲標籤",PopularLabel.Visibility==Visibility.Collapsed&&LibraryEmpty.IsVisible);
            SearchBox.Clear();await Wait(()=>_songs.Count>0&&!_fetching);
            Check("清除搜尋只有熱門前十名",_songs.Count<=10&&PopularLabel.IsVisible);
            await _player.SetVolumeAsync(100);VolumeSlider.Value=100;
            await CaptureAsync(Path.Combine(Path.GetDirectoryName(report)!,"single-machine-window.png"));
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new {Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error)
        {
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new {Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));
            ShowError(error);
        }
        finally { Close(); }
    }
    private static void CaptureDialog(Window dialog,string path)
    {
        var bitmap=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(dialog);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }
}
