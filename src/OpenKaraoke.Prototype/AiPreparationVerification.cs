using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenKaraoke.Library;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyAiPreparationAsync(string folder,string package,Action<string,bool,object?> check)
    {
        var reports=new List<CreationProgress>();var parser=new AiInstallProgress(new InlineCreationProgress(reports.Add));
        parser.Report("Collecting torch==2.8.0");parser.Report("Downloading torch-2.8.0-cp312-cp312-win_amd64.whl (100 MB)");
        parser.Report("Progress 37 of 100");
        check("pip 真實位元組進度轉為目前套件百分比",reports[^1].Percent==37&&reports[^1].Detail=="下載套件 · torch",null);
        parser.Report("Downloading torchaudio-2.8.0-cp312-cp312-win_amd64.whl (10 MB)");parser.Report("Progress 50 of 200");
        check("pip 下一個下載套件獨立更新名稱與進度",reports[^1].Percent==25&&reports[^1].Detail=="下載套件 · torchaudio",null);
        parser.Report("Progress 1024 of 0");
        check("無下載總大小時保持不確定進度，不顯示假百分比",reports[^1].Percent==null,null);
        int count=reports.Count;parser.Report("Progress invalid of 500");parser.Report("Requirement already satisfied: torch");
        check("不完整或無關 pip 輸出不覆蓋下載進度",reports.Count==count,null);
        parser.Report("Installing collected packages: torch, torchaudio");
        check("下載完成後安裝階段移除舊百分比",reports[^1].Percent==null&&reports[^1].Detail=="安裝 AI 套件",null);
        var firstRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<CreationProgress>? current=null;bool ready=false;int attempt=0,processed=0;
        await using var queue=new SongProcessingQueue(Path.Combine(folder,"ai-ui"),async(item,_,_,token)=>
        {
            var song=await _packages.ImportAsync(package,item.Title,item.Artist,token,item.Id);Interlocked.Increment(ref processed);return song;
        },aiReady:()=>ready,prepareAi:async(progress,token)=>
        {
            current=progress;
            if(Interlocked.Increment(ref attempt)==1){await firstRelease.Task.WaitAsync(token);throw new IOException("模擬下載失敗");}
            await secondRelease.Task.WaitAsync(token);ready=true;
        });
        async Task Wait(Func<bool> condition)
        {
            var watch=Stopwatch.StartNew();while(!condition())
            {if(watch.Elapsed.TotalSeconds>12)throw new TimeoutException("AI 元件介面驗收逾時。");await Task.Delay(20);}
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        }
        AddSongDialog? dialog=null;
        try
        {
            dialog=new AddSongDialog(this,_packages,_creator,queue);dialog.Show();
            var video=new YouTubeVideo("https://www.youtube.com/watch?v=LbiWaeLdqeA","驗收影片",120);
            foreach(string title in new[]{"等待元件甲","等待元件乙"})
            {
                dialog.SetVerifiedVideo(video);((TextBox)dialog.FindName("TitleInput")).Text=title;
                ((TextBox)dialog.FindName("ArtistInput")).Text="驗收歌手";
                ((Button)dialog.FindName("SubmitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            var list=(ListBox)dialog.FindName("ProcessingList");
            await Wait(()=>current!=null&&list.Items.Count==3);
            current!.Report(new("setup","下載套件 · torch",37));
            await Wait(()=>list.Items.Cast<ProcessingRow>().Any(r=>r.Item.Percent==37));
            async Task<ListBoxItem> Container(string id)
            {
                var row=list.Items.Cast<ProcessingRow>().Single(r=>r.Id==id);list.ScrollIntoView(row);
                await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
                return (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row);
            }
            string setupId=queue.Items[0].Id;var setupRow=list.Items.Cast<ProcessingRow>().First();
            var container=await Container(setupId);var action=UiElements<Button>(container).Single();var bar=UiElements<ProgressBar>(container).Single();
            check("按加入後首列為獨立必要 AI 元件，兩首歌曲列在後面",setupRow.Title=="必要 AI 元件"&&list.Items.Count==3&&list.Items.Cast<ProcessingRow>().Skip(1).All(r=>r.Item.Status=="queued"),null);
            check("AI 項目顯示下載圖示及實際 37% 進度",setupRow.Icon=="download"&&bar.IsVisible&&!bar.IsIndeterminate&&bar.Value==37&&setupRow.Percent=="37%",null);
            check("AI 列不顯示取消叉叉或空白歌手列",action.Visibility==Visibility.Collapsed&&setupRow.ArtistVisible==Visibility.Collapsed,null);
            var songRow=list.Items.Cast<ProcessingRow>().ElementAt(1);var songContainer=await Container(songRow.Id);
            check("等待歌曲明示等待元件，取消歌曲仍可操作",songRow.Stage=="等待必要 AI 元件"&&UiElements<Button>(songContainer).Single().IsVisible&&songRow.ActionIcon=="x"&&processed==0,null);
            list.ScrollIntoView(list.Items[0]);await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            CaptureDialog(dialog,Path.Combine(folder,"ai-components-progress.png"));
            ((Button)dialog.FindName("CloseDialogButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>!dialog.IsVisible);
            check("關閉加入歌曲視窗不會取消 AI 準備",queue.Items[0].Status=="active"&&attempt==1&&processed==0,null);
            dialog=new AddSongDialog(this,_packages,_creator,queue);dialog.Show();list=(ListBox)dialog.FindName("ProcessingList");
            await Wait(()=>list.Items.Count==3);
            check("重新開啟保留同一元件項目及下載進度",list.Items.Cast<ProcessingRow>().First().Id==setupId&&list.Items.Cast<ProcessingRow>().First().Percent=="37%",null);
            firstRelease.TrySetResult();await Wait(()=>list.Items.Cast<ProcessingRow>().Any(r=>r.Item.Status=="error"));
            container=await Container(setupId);action=UiElements<Button>(container).Single();setupRow=list.Items.Cast<ProcessingRow>().First();
            check("AI 準備失敗只提供重試按鈕，歌曲繼續等待",action.IsVisible&&setupRow.ActionIcon=="rotate-ccw"&&Equals(action.ToolTip,"重試")&&queue.Items.Skip(1).All(i=>i.Status=="queued")&&processed==0,null);
            CaptureDialog(dialog,Path.Combine(folder,"ai-components-error.png"));
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>attempt==2&&list.Items.Cast<ProcessingRow>().First().Item.Status=="active");
            container=await Container(setupId);
            check("按重試後恢復準備且再次隱藏取消操作",UiElements<Button>(container).Single().Visibility==Visibility.Collapsed&&queue.Items[0].Id==setupId,null);
            secondRelease.TrySetResult();await Wait(()=>queue.Items.Length==0&&list.Items.Count==0&&processed==2);
            check("AI 驗證就緒後才完成歌曲製作及實際入庫",_store.ContainsSong("等待元件甲","驗收歌手")&&_store.ContainsSong("等待元件乙","驗收歌手"),null);
        }
        finally{dialog?.Close();firstRelease.TrySetResult();secondRelease.TrySetResult();}
    }
    private sealed class InlineCreationProgress(Action<CreationProgress> report):IProgress<CreationProgress>
    {public void Report(CreationProgress value)=>report(value);}
}
