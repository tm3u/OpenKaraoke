using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenKaraoke.Library;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyQueueFeedbackAsync(string folder,Action<string,bool,object?> check)
    {
        var saved=_queue.Entries.ToArray();string query=SearchBox.Text;
        var fixture=Enumerable.Range(0,65).Select(i=>_songs[0].Song with{Id="feedback-"+i,Title=$"回饋驗收 {i:00}",Artist="介面驗收"}).ToArray();
        async Task Layout()=>await Dispatcher.InvokeAsync(SongList.UpdateLayout,DispatcherPriority.ApplicationIdle);
        async Task Wait(Func<bool> ready)
        {var clock=Stopwatch.StartNew();while(!ready()){if(clock.Elapsed.TotalSeconds>5)throw new TimeoutException("歌曲列回饋驗收逾時。");await Task.Delay(10);}await Layout();}
        QueueAddedFeedback? Feedback(Song song)
        {var row=_songs.FirstOrDefault(r=>r.Song.Id==song.Id);return row==null?null:FindQueueFeedback(SongList.ItemContainerGenerator.ContainerFromItem(row));}
        Button AddButton(QueueAddedFeedback row)=>UiElements<Button>(row).Single(b=>Equals(b.ToolTip,"加入待播清單"));
        try
        {
            foreach(var song in fixture)_store.Add(song);
            SearchBox.Text="回饋驗收";_searchTimer.Stop();await SearchAsync();await SearchAsync(true);
            var row=_songs.Single(r=>r.Song.Id==fixture[^1].Id);SongList.ScrollIntoView(row);await Layout();
            var scroll=UiElements<ScrollViewer>(SongList).First();double offset=scroll.VerticalOffset;
            int before=_queue.Entries.Count;Toast.Visibility=Visibility.Collapsed;_toastTimer.Stop();
            UiMotion.SetAllowAnimation(this,true);
            AddButton(Feedback(fixture[^1])!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>_queue.Entries.Count==before+1&&Feedback(fixture[^1])?.IsConfirming==true);
            var feedback=Feedback(fixture[^1])!;Size size=feedback.RenderSize;
            check("加入成功僅顯示單純勾選與可讀取狀態，不顯示下方提示",UiElements<Icon>(AddButton(feedback)).Single().Kind=="check"&&AutomationProperties.GetItemStatus(feedback)=="已加入待播清單"&&!Toast.IsVisible,null);
            var button=AddButton(feedback);var tick=UiElements<Icon>(button).Single();var tickBounds=tick.TransformToAncestor(button).TransformBounds(new Rect(tick.RenderSize));
            check("歌曲列指定的 15 像素圖示實際生效並置中",tick.ActualWidth==15&&tick.ActualHeight==15&&Math.Abs(tickBounds.Left+tickBounds.Width/2-button.ActualWidth/2)<=.5&&Math.Abs(tickBounds.Top+tickBounds.Height/2-button.ActualHeight/2)<=.5,new{tick.ActualWidth,tick.ActualHeight,Bounds=tickBounds});
            check("加入後保留分批載入的 65 首搜尋結果與捲動位置",_songs.Count==65&&offset>0&&Math.Abs(scroll.VerticalOffset-offset)<.1,new{Rows=_songs.Count,Before=offset,After=scroll.VerticalOffset});
            feedback.ShowAdded(TimeSpan.FromMilliseconds(900));await Task.Delay(180);
            check("歌曲列沒有額外底色或動畫，尺寸及文字位置不變",feedback.Children.Count==1&&feedback.Background==null&&!feedback.HasAnimatedProperties&&feedback.RenderSize==size&&feedback.RenderTransform.Value.IsIdentity,null);
            await CaptureAsync(Path.Combine(folder,"queue-added-feedback.png"));
            before=_queue.Entries.Count;
            for(int i=0;i<3;i++)AddButton(feedback).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>_queue.Entries.Count==before+3&&Feedback(fixture[^1])!.IsConfirming);
            check("連續重複加入仍建立三筆紀錄且按鈕不被動畫鎖住",AddButton(Feedback(fixture[^1])!).IsEnabled&&!Toast.IsVisible,null);
            before=_queue.Entries.Count;
            AddButton(Feedback(fixture[^1])!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AddButton(Feedback(fixture[^2])!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>_queue.Entries.Count==before+2&&Feedback(fixture[^1])!.IsConfirming&&Feedback(fixture[^2])!.IsConfirming);
            check("快速加入不同歌曲，各列回饋不互相覆蓋",Feedback(fixture[^1])!=Feedback(fixture[^2]),null);
            await Task.Delay(1150);
            check("確認結束後還原加號，不遺留底色",!feedback.IsConfirming&&feedback.Background==null&&UiElements<Icon>(AddButton(feedback)).Single().Kind=="plus",null);
            UiMotion.SetAllowAnimation(this,false);before=_queue.Entries.Count;
            AddButton(feedback).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>_queue.Entries.Count==before+1&&feedback.IsConfirming);
            check("減少動畫時仍僅顯示勾選，不加入底色",feedback.Background==null&&!feedback.HasAnimatedProperties,null);
            UiMotion.SetAllowAnimation(this,true);feedback.ShowAdded(TimeSpan.FromMilliseconds(900));UiMotion.SetAllowAnimation(this,false);
            check("系統動畫設定切換不影響勾選與置中",feedback.IsConfirming&&feedback.Background==null&&feedback.RenderSize==size,null);
            UiMotion.SetAllowAnimation(this,true);feedback.ShowAdded(TimeSpan.FromMilliseconds(900));
            check("勾選確認一律沒有動態效果",feedback.IsConfirming&&!feedback.HasAnimatedProperties,null);
            SearchBox.Text="不存在的回饋歌曲";_searchTimer.Stop();await SearchAsync();await Layout();
            check("歌曲離開畫面後清除回饋，避免虛擬化重用到別首歌",!feedback.IsConfirming&&!feedback.HasAnimatedProperties,null);
            before=_queue.Entries.Count;Toast.Visibility=Visibility.Collapsed;
            Enqueue_Click(new Button{Tag=fixture[0] with{Id="missing-feedback-song"}},new RoutedEventArgs(Button.ClickEvent));await Layout();
            check("加入失敗保留錯誤提示，不新增紀錄或假報成功",_queue.Entries.Count==before&&Toast.IsVisible&&ToastText.Text.Contains("歌曲已從歌曲庫移除")&&!_queueFeedback.ContainsKey("missing-feedback-song"),null);
            SearchBox.Clear();_searchTimer.Stop();await SearchAsync();await Layout();
            var popular=_songs[0].Song;before=_queue.Entries.Count;
            AddButton(Feedback(popular)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AddButton(Feedback(popular)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>_queue.Entries.Count==before+2&&Feedback(popular)?.IsConfirming==true);
            check("熱門歌曲重新排序後仍回饋正確歌曲",Feedback(popular)!.DataContext is SongRow shown&&shown.Song.Id==popular.Id,null);
        }
        finally
        {
            _queue.Entries.Clear();_queue.Entries.AddRange(saved);_queue.Save();_queueFeedback.Clear();
            foreach(var song in fixture)_store.Remove(song.Id,song.Title);
            ClearValue(UiMotion.AllowAnimationProperty);SearchBox.Text=query;_searchTimer.Stop();await SearchAsync();RefreshQueue();
            _toastTimer.Stop();Toast.Visibility=Visibility.Collapsed;await Layout();
        }
    }
}
