using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenKaraoke.Library;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyCreationAsync(string report)
    {
        var checks = new List<object>(); string folder = Path.GetDirectoryName(report)!;
        void Check(string name, bool passed, object? evidence = null)
        { checks.Add(new { Name = name, Passed = passed, Evidence = evidence }); if (!passed) throw new InvalidOperationException(name); }
        async Task Wait(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.Elapsed.TotalSeconds > 35) throw new TimeoutException("加入歌曲驗收逾時。"); await Task.Delay(30); }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        ScrollViewer? FindScroll(DependencyObject node)
        {
            if(node is ScrollViewer scroll)return scroll;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)if(FindScroll(VisualTreeHelper.GetChild(node,i)) is {} found)return found;
            return null;
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int failOnce=1;
        IProgress<CreationProgress>? currentProgress=null;
        SongProcessingQueue? processing = null; AddSongDialog? dialog = null;
        try
        {
            string package = Argument(Environment.GetCommandLineArgs(), "--verification-package") ?? throw new ArgumentException("缺少驗收歌曲檔。");
            await using var silent = new OpenKaraoke.Player.MpvConnection(_player.PipeName); await silent.ConnectAsync(); await silent.SetAsync("mute", true);
            await ModeAsync("original");await _player.ChangeKeyAsync(2);string? currentId=_queue.CurrentId;int currentTrack=_player.State.TrackId;
            int active = 0, peak = 0; var started = new List<string>();
            processing = new SongProcessingQueue(Path.Combine(folder, "processing"), async (item, work, progress, token) =>
            {
                int count = Interlocked.Increment(ref active); peak = Math.Max(peak, count); lock (started) started.Add(item.Title);
                try
                {
                    currentProgress=progress;
                    progress.Report(new("download", item.Kind == "create" ? "下載影片" : "匯入歌曲", 40));
                    await release.Task.WaitAsync(token);
                    if(item.Title.StartsWith("取消"))await cancelRelease.Task.WaitAsync(token);
                    if(item.Title=="失敗工作"&&Interlocked.Exchange(ref failOnce,0)==1)throw new IOException("驗收用錯誤訊息");
                    return await _packages.ImportAsync(package, item.Title, item.Artist, token, item.Id, item.Lyrics);
                }
                finally { Interlocked.Decrement(ref active); }
            },_store);
            dialog = new AddSongDialog(this, _packages, _creator, processing); dialog.Show();
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var title = (TextBox)dialog.FindName("TitleInput"); var artist = (TextBox)dialog.FindName("ArtistInput");
            var submit = (Button)dialog.FindName("SubmitButton");
            var createSubmit = submit.TranslatePoint(new(),dialog); double createSubmitHeight=submit.ActualHeight, createHeight=dialog.ActualHeight;
            var createLabel=UiElements<TextBlock>((StackPanel)dialog.FindName("CreateSource")).First();
            Check("加入歌曲為 720px 左右並排", dialog.ActualWidth == 720 && ((ListBox)dialog.FindName("ProcessingList")).TranslatePoint(new(), dialog).X > title.TranslatePoint(new(), dialog).X + 200);
            Check("製作欄位預設空白且不可送出", title.Text == "" && artist.Text == "" && !submit.IsEnabled);
            title.Text="　 歌 名  ";artist.Text="\t 歌 手　";
            title.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));artist.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Check("歌名歌手離開欄位時清除前後空白，保留中間空格",title.Text=="歌 名"&&artist.Text=="歌 手");
            title.Text=artist.Text="";
            dialog.SetVerifiedVideo(new("https://www.youtube.com/watch?v=LbiWaeLdqeA", "測試影片標題不會填進欄位", 12));
            Check("讀取後按鈕為圖示加已讀取，歌名歌手仍空白", ((TextBlock)dialog.FindName("ReadVideoLabel")).Text == "已讀取" && ((Icon)dialog.FindName("ReadVideoIcon")).Kind == "circle-check" && title.Text == "" && artist.Text == "");
            ((TextBox)dialog.FindName("UrlInput")).Text += "&list=test";
            Check("修改網址恢復讀取影片並禁止送出", ((TextBlock)dialog.FindName("ReadVideoLabel")).Text == "讀取影片" && !submit.IsEnabled);
            dialog.SetVerifiedVideo(new("https://www.youtube.com/watch?v=LbiWaeLdqeA", "測試影片", 12));
            title.Text = "製作工作"; artist.Text = " "; Check("製作兩欄必填，純空白無效", !submit.IsEnabled);
            artist.Text = "驗收歌手";
            var lyricsSection = (StackPanel)dialog.FindName("LyricsSection");
            var scrollChoice = (RadioButton)dialog.FindName("ScrollLyricsButton");
            var karaokeChoice = (RadioButton)dialog.FindName("KaraokeLyricsButton");
            Check("歌詞選填欄位位於歌名歌手下方，未匯入仍可加入", lyricsSection.TranslatePoint(new(), dialog).Y > title.TranslatePoint(new(), dialog).Y + title.ActualHeight && submit.IsEnabled
                && scrollChoice.IsChecked == true && karaokeChoice.IsChecked == false && scrollChoice.IsEnabled && karaokeChoice.IsEnabled && dialog.FindName("LyricsNote") == null);
            string lineLyrics = Path.Combine(folder, "逐行歌詞.lrc"), wordLyrics = Path.Combine(folder, "逐字歌詞.lrc"), invalidLyrics = Path.Combine(folder, "錯誤歌詞.lrc");
            await File.WriteAllTextAsync(lineLyrics, "[00:01.00]今天一起唱歌\n[00:05.00]跟著節奏向前\n[00:09.00]");
            await File.WriteAllTextAsync(wordLyrics, VerificationLyrics);
            await File.WriteAllTextAsync(invalidLyrics, "沒有時間的文字");
            dialog.SelectLyrics(lineLyrics);
            Check("歌詞呈現方式二擇一，左側預設滾動且匯入後可直接送出", submit.IsEnabled && scrollChoice.IsChecked == true && karaokeChoice.IsChecked == false
                && scrollChoice.GroupName == karaokeChoice.GroupName && scrollChoice.TranslatePoint(new(), dialog).X < karaokeChoice.TranslatePoint(new(), dialog).X);
            karaokeChoice.IsChecked = true;
            Check("逐行歌詞不能以 KTV 逐字模式送出", !submit.IsEnabled && ((TextBlock)dialog.FindName("LyricsError")).Text.Contains("缺少完整的逐字時間"));
            scrollChoice.IsChecked = true;
            Check("改選滾動模式後取消 KTV 選取並清除歌詞錯誤", submit.IsEnabled && scrollChoice.IsChecked == true && karaokeChoice.IsChecked == false
                && ((TextBlock)dialog.FindName("LyricsError")).Visibility == Visibility.Collapsed);
            dialog.SelectMode("import"); dialog.SelectMode("create");
            Check("製作與匯入頁籤各自保存歌詞草稿", ((TextBlock)dialog.FindName("LyricsFilename")).Text == Path.GetFileName(lineLyrics)
                && scrollChoice.IsChecked == true);
            dialog.SelectLyrics(invalidLyrics);
            Check("無效歌詞阻擋送出且可在加入前移除", !submit.IsEnabled && ((Button)dialog.FindName("ClearLyricsButton")).Visibility == Visibility.Visible);
            ((Button)dialog.FindName("ClearLyricsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("加入前移除歌詞即可恢復選填狀態", submit.IsEnabled && scrollChoice.IsChecked == true && karaokeChoice.IsEnabled);
            dialog.SelectLyrics(wordLyrics);
            karaokeChoice.IsChecked = true;
            Check("逐字歌詞可選 KTV 模式加入，且與滾動互斥", submit.IsEnabled && karaokeChoice.IsChecked == true && scrollChoice.IsChecked == false);
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            CaptureDialog(dialog, Path.Combine(folder, "add-songs-lyrics-karaoke.png"));
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            File.Delete(wordLyrics);
            await Wait(() => processing.Items.Any(i => i.Status == "active"));
            Check("提交後清空欄位並保持可繼續加入", title.Text == "" && artist.Text == "" && title.IsEnabled);
            Check("提交時固定歌詞內容與模式，表單清除上一首歌詞", processing.Items.Single(i => i.Kind == "create").Lyrics is { Mode: "karaoke", Content: VerificationLyrics }
                && ((Button)dialog.FindName("ClearLyricsButton")).Visibility == Visibility.Collapsed && scrollChoice.IsChecked == true && karaokeChoice.IsChecked == false);
            await Wait(()=>currentProgress!=null&&processing.Items.Any(i=>i.Percent==40));
            currentProgress!.Report(new("setup","準備 AI 功能"));
            await Wait(()=>UiElements<ProgressBar>(dialog).Any(p=>p.IsVisible&&p.IsIndeterminate));
            var busyBar=UiElements<ProgressBar>(dialog).First(p=>p.IsVisible&&p.IsIndeterminate);
            var busyIndicator=(Border)busyBar.Template.FindName("BusyIndicator",busyBar);var busyBounds=new Rect(busyBar.RenderSize);
            Check("未知進度使用獨立短條，不顯示滿格完成狀態",busyIndicator.ActualWidth is >0 and <=40.5&&busyIndicator.ActualWidth<busyBar.ActualWidth&&((Border)busyBar.Template.FindName("PART_Indicator",busyBar)).Visibility==Visibility.Collapsed);
            if(UiMotion.GetAllowAnimation(dialog))
            {
                var opacity=new List<double>{busyIndicator.Opacity};await Task.Delay(130);opacity.Add(busyIndicator.Opacity);await Task.Delay(170);opacity.Add(busyIndicator.Opacity);
                Check("未知進度以透明度提示持續工作，不捏造百分比或改變版面",opacity.Max()-opacity.Min()>.08&&processing.Items.Single(i=>i.Status=="active").Percent==null&&new Rect(busyBar.RenderSize)==busyBounds,opacity);
            }
            CaptureDialog(dialog,Path.Combine(folder,"add-songs-busy.png"));UiMotion.SetAllowAnimation(dialog,false);await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            double stillOpacity=busyIndicator.Opacity;await Task.Delay(150);
            Check("減少動畫時未知進度保持靜態，仍保留處理狀態",!busyIndicator.HasAnimatedProperties&&busyIndicator.Opacity==stillOpacity&&processing.Items.Single(i=>i.Status=="active").Stage=="準備 AI 功能");
            dialog.ClearValue(UiMotion.AllowAnimationProperty);currentProgress.Report(new("download","下載影片",40));
            await Wait(()=>UiElements<ProgressBar>(dialog).Any(p=>p.IsVisible&&!p.IsIndeterminate&&p.Value==40));
            dialog.SelectMode("import"); await dialog.SelectPackageAsync(package);
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var importLabel=(TextBlock)dialog.FindName("PackageLabel");var chooser=(Button)dialog.FindName("ChoosePackageButton");
            Check("匯入選檔標題與 YouTube 網址標題使用相同層級",importLabel.Text=="選擇歌曲檔"&&importLabel.FontSize==createLabel.FontSize&&Equals(importLabel.Foreground,createLabel.Foreground)&&importLabel.Parent==chooser.Parent);
            Check("選檔框只保留右側的檔案圖示",UiElements<Icon>(chooser).Count()==1&&UiElements<Icon>(chooser).Single().Kind=="folder-open"&&UiElements<Icon>(chooser).Single().TranslatePoint(new(),chooser).X>chooser.ActualWidth-40);
            Check("製作與匯入按鈕尺寸及水平位置相同",Math.Abs(createSubmit.Y-submit.TranslatePoint(new(),dialog).Y)<1&&Math.Abs(createSubmitHeight-submit.ActualHeight)<1&&Math.Abs(createHeight-dialog.ActualHeight)<1,new{CreateY=createSubmit.Y,ImportY=submit.TranslatePoint(new(),dialog).Y,Height=submit.ActualHeight});
            var cornerBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);cornerBitmap.Render(dialog);byte[] cornerPixels=new byte[4*(int)dialog.ActualWidth*(int)dialog.ActualHeight];cornerBitmap.CopyPixels(cornerPixels,4*(int)dialog.ActualWidth,0);
            Check("加入歌曲四角為不透明直角",new[]{0,(int)dialog.ActualWidth-1,((int)dialog.ActualHeight-1)*(int)dialog.ActualWidth,(int)dialog.ActualWidth*(int)dialog.ActualHeight-1}.All(p=>cornerPixels[p*4+3]==255)&&((Border)dialog.FindName("DialogSurface")).CornerRadius==new CornerRadius(0)&&SquareWindowPreference(dialog));
            var parsed = SongPackageService.ParseFilename(package);
            Check("只有匯入依檔名帶入歌名歌手", title.Text == parsed.Title && artist.Text == parsed.Artist);
            title.Text = "匯入工作"; artist.Text = "驗收歌手";
            dialog.SelectLyrics(lineLyrics);
            scrollChoice.IsChecked = true;
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            CaptureDialog(dialog, Path.Combine(folder, "add-songs-lyrics-scroll.png"));
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 8; i++) processing.AddImport(package, "捲動工作 " + i, "驗收歌手");
            await Wait(() => ((ListBox)dialog.FindName("ProcessingList")).Items.Count == 10);
            var list = (ListBox)dialog.FindName("ProcessingList");
            Check("製作與匯入進入同一清單且同時只執行一首", peak == 1 && processing.Items.Count(i => i.Status == "active") == 1 && processing.Items.Any(i => i.Kind == "import"));
            Check("待處理列不顯示處理中或等待文字", list.Items.Cast<ProcessingRow>().Where(row => row.Id != processing.Items.Single(i => i.Status == "active").Id).All(row => row.Stage == ""));
            await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            var scroll=FindScroll(list)!;double offset=scroll.VerticalOffset;
            var alignment=ButtonAlignmentIssues(dialog);Check("處理清單及表單按鈕的內容置中且未裁切",alignment.Length==0,alignment);
            var scrollBar=(System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar",scroll);
            Check("處理清單捲軸維持細版寬度",scrollBar.ActualWidth<=7.1,scrollBar.ActualWidth);
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.MouseWheelEvent});
            await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            Check("處理清單實際回應滑鼠滾輪",scroll.VerticalOffset>offset);
            var last = list.Items[^1]; list.ScrollIntoView(last); await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var lastRow=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(last);
            Check("處理清單可捲動到最後一筆並完整顯示",lastRow.TranslatePoint(new(),list).Y>=-1&&lastRow.TranslatePoint(new Point(0,lastRow.ActualHeight),list).Y<=list.ActualHeight+1);
            var close=(Button)dialog.FindName("CloseDialogButton");var header=(Border)dialog.FindName("DialogHeader");var entry=(Grid)dialog.FindName("EntryArea");
            Check("只有右上關閉叉叉，沒有底部關閉按鈕或提示",UiElements<Icon>(close).Single().Kind=="x"&&close.ToolTip==null&&close.TranslatePoint(new(),dialog).X>dialog.ActualWidth-60&&close.TranslatePoint(new Point(0,close.ActualHeight),dialog).Y<=header.ActualHeight+1&&dialog.FindName("FooterNote")==null&&!UiElements<Button>(dialog).Any(b=>Equals(b.Content,"關閉")));
            Check("移除整條底欄且不留下空白保留區",((Grid)dialog.FindName("DialogLayout")).RowDefinitions.Count==2&&Math.Abs(dialog.ActualHeight-header.ActualHeight-entry.ActualHeight-2)<1);
            Check("歌曲處理清單位於加入歌曲視窗",dialog.FindName("ProcessingList")!=null);
            CaptureDialog(dialog, Path.Combine(folder, "add-songs-processing.png"));
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(() => !dialog.IsVisible); double position = _player.State.Position; await Wait(() => _player.State.Position > position + .2);
            Check("右上叉叉關閉後仍持續處理且不中斷播放", processing.Items.Any(i => i.Status == "active") && !_player.State.Paused);
            dialog = new AddSongDialog(this, _packages, _creator, processing); dialog.Show(); await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
            title=(TextBox)dialog.FindName("TitleInput");artist=(TextBox)dialog.FindName("ArtistInput");submit=(Button)dialog.FindName("SubmitButton");
            Check("重新開啟看到同一處理清單", ((ListBox)dialog.FindName("ProcessingList")).Items.Count == 10);
            dialog.SelectMode("create"); await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle); CaptureDialog(dialog, Path.Combine(folder, "add-songs-create.png"));
            dialog.SelectMode("import"); await dialog.SelectPackageAsync(package); await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle); CaptureDialog(dialog, Path.Combine(folder, "add-songs-import.png"));
            release.TrySetResult(); await Wait(() => processing.Items.Length == 0&&((ListBox)dialog.FindName("ProcessingList")).Items.Count==0);
            Check("完成自動加入歌曲庫並從處理清單移除", _store.Search("驗收歌手").Total == 10 && ((ListBox)dialog.FindName("ProcessingList")).Items.Count == 0);
            Check("兩個加入流程均實際保存當次選定的歌詞模式", (await _packages.InspectAsync(_store.Search("製作工作").Songs.Single().MediaPath)).LyricsMode == "karaoke"
                && (await _packages.InspectAsync(_store.Search("匯入工作").Songs.Single().MediaPath)).LyricsMode == "scroll");
            Check("製作與匯入都保存同名 opok 與依模式命名的獨立歌詞", new[] { ("製作工作", ".ass"), ("匯入工作", ".lrc") }.All(pair =>
            {
                string media = _store.Search(pair.Item1).Songs.Single().MediaPath;
                return Path.GetExtension(media) == ".opok" && File.Exists(Path.ChangeExtension(media, pair.Item2))
                    && Directory.GetFiles(Path.GetDirectoryName(media)!).Length == 2;
            }));
            Check("實際處理順序與加入順序一致", started.Take(2).SequenceEqual(new[] { "製作工作", "匯入工作" }) && peak == 1);
            Check("背景處理保留目前歌曲、原唱音軌及升降調",_queue.CurrentId==currentId&&_player.State.TrackId==currentTrack&&_player.State.Key==2&&!_player.State.Paused);
            await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle); CaptureDialog(dialog, Path.Combine(folder, "add-songs-completed.png"));
            title.Text="匯入工作";artist.Text="驗收歌手";submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("已入庫歌曲在送出前阻擋重複且保留表單",processing.Items.Length==0&&((TextBlock)dialog.FindName("FormError")).Text=="歌曲已加入或正在處理。"&&title.Text=="匯入工作");
            title.Text="修正後名稱";Check("修改欄位立即清除舊錯誤，選取頁籤保留底色",((TextBlock)dialog.FindName("FormError")).Visibility==Visibility.Collapsed&&((Button)dialog.FindName("ImportTab")).Style==FindResource("SelectedButton"));
            string failed=processing.AddImport(package,"失敗工作","驗收歌手");processing.AddImport(package,"失敗之後","驗收歌手");
            await Wait(()=>processing.Items.Length==1&&processing.Items[0].Status=="error"&&((ListBox)dialog.FindName("ProcessingList")).Items.Count==1&&((ListBox)dialog.FindName("ProcessingList")).Items.Cast<ProcessingRow>().Any(r=>r.Id==failed&&r.Item.Status=="error"));
            list=(ListBox)dialog.FindName("ProcessingList");var errorRow=list.Items.Cast<ProcessingRow>().Single();list.ScrollIntoView(errorRow);await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
            var errorContainer=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(errorRow);var retry=UiElements<Button>(errorContainer).Single();
            Check("失敗不阻塞下一首，顯示重試圖示與指定懸停文字",_store.ContainsSong("失敗之後","驗收歌手")&&errorRow.ActionIcon=="rotate-ccw"&&Equals(retry.ToolTip,"重試"));
            Check("處理失敗列沒有額外錯誤懸停文字",UiElements<FrameworkElement>(errorContainer).Where(e=>e.ToolTip!=null).All(e=>Equals(e.ToolTip,"重試")||Equals(e.ToolTip,errorRow.Title)));
            CaptureDialog(dialog,Path.Combine(folder,"add-songs-error.png"));retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>processing.Items.Length==0&&list.Items.Count==0);Check("實際按下重試後成功入庫並移除",_store.ContainsSong("失敗工作","驗收歌手"));
            string cancelActive=processing.AddImport(package,"取消當前","驗收歌手"),cancelPending=processing.AddImport(package,"取消待處理","驗收歌手");
            await Wait(()=>processing.Items.Any(i=>i.Id==cancelActive&&i.Status=="active")&&list.Items.Count==2);
            bool duplicateBlocked=false;try{processing.AddImport(package,"取消當前","驗收歌手");}catch(InvalidOperationException){duplicateBlocked=true;}
            Check("尚在處理的歌曲亦不能重複送出",duplicateBlocked&&processing.Items.Length==2);
            async Task<Button> ActionFor(string id)
            {
                var row=list.Items.Cast<ProcessingRow>().Single(r=>r.Id==id);list.ScrollIntoView(row);await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);
                return UiElements<Button>((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(row)).Single();
            }
            var cancelButton=await ActionFor(cancelPending);Check("取消處理按鈕只有叉叉且沒有懸停文字",cancelButton.ToolTip==null&&UiElements<Icon>(cancelButton).Single().Kind=="x");
            cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>processing.Items.Single(i=>i.Id==cancelPending).Status=="cancelled");
            (await ActionFor(cancelActive)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Wait(()=>processing.Items.All(i=>i.Status=="cancelled")&&list.Items.Cast<ProcessingRow>().All(r=>r.Item.Status=="cancelled"));
            Check("目前與尚未執行工作可透過實際按鈕取消，不產生入庫半成品",!_store.ContainsSong("取消當前","驗收歌手")&&!_store.ContainsSong("取消待處理","驗收歌手"));
            CaptureDialog(dialog,Path.Combine(folder,"add-songs-cancelled.png"));cancelRelease.TrySetResult();
            (await ActionFor(cancelActive)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));(await ActionFor(cancelPending)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(()=>processing.Items.Length==0&&list.Items.Count==0);Check("已取消工作實際重試後依序完成並移除",_store.ContainsSong("取消當前","驗收歌手")&&_store.ContainsSong("取消待處理","驗收歌手"));
            string nonstandard=Path.Combine(folder,"不符合命名格式.opok");File.Copy(package,nonstandard,true);await dialog.SelectPackageAsync(nonstandard);
            Check("不符合命名格式時全填歌名、歌手空白且禁止送出",title.Text=="不符合命名格式"&&artist.Text==""&&!submit.IsEnabled);
            await dialog.SelectPackageAsync(FixtureMediaPath);
            Check("匯入拒絕其他副檔名且不可送出",!submit.IsEnabled&&((TextBlock)dialog.FindName("FormError")).Text.Contains("只能匯入 .opok"));
            await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);close=(Button)dialog.FindName("CloseDialogButton");
            Check("表單錯誤不破壞左右並排與右上叉叉位置",list.TranslatePoint(new(),dialog).X>title.TranslatePoint(new(),dialog).X+200&&close.TranslatePoint(new Point(0,close.ActualHeight),dialog).Y<=((Border)dialog.FindName("DialogHeader")).ActualHeight+1);
            ((TextBlock)dialog.FindName("FormError")).Text=string.Concat(Enumerable.Repeat("下載工具回報的長錯誤內容。",100));
            await Dispatcher.InvokeAsync(dialog.UpdateLayout,DispatcherPriority.ApplicationIdle);var errorViewport=(ScrollViewer)dialog.FindName("FormErrorViewport");
            Check("長錯誤內容可捲動且右上叉叉維持可見",errorViewport.ActualHeight<=72&&errorViewport.ScrollableHeight>0&&close.IsVisible&&close.TranslatePoint(new Point(0,close.ActualHeight),dialog).Y<=((Border)dialog.FindName("DialogHeader")).ActualHeight+1);
            dialog.Close();await Wait(()=>!dialog.IsVisible);
            await VerifyAiPreparationAsync(folder,package,Check);
            await VerifyLyricsPlaybackAsync(folder, package, Check);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = true, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true })); }
        finally { dialog?.Close(); if (processing != null) await processing.DisposeAsync(); Close(); }
    }
}
