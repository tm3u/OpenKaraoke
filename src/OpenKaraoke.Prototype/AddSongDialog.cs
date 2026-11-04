using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OpenKaraoke.Prototype;

public partial class AddSongDialog : Window
{
    private sealed class Draft
    {
        public string Title = "", Artist = "", Url = "", File = "";
        public YouTubeVideo? Video;
        public PackageInfo? Package;
        public SongLyrics? Lyrics;
        public string LyricsFilename = "", LyricsError = "", LyricsMode = "scroll";
    }
    private readonly SongPackageService _packages;
    private readonly SongCreationService _creator;
    private readonly SongProcessingQueue _queue;
    private readonly Draft _create = new(), _import = new();
    private readonly ObservableCollection<ProcessingRow> _rows = [];
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _readCancellation;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private Task? _readTask, _fileTask;
    private string _mode = "create";
    private bool _updating, _reading, _selecting, _closing, _closed;
    private int _dirty;
    private Draft Current => _mode == "create" ? _create : _import;

    public AddSongDialog(Window owner, SongPackageService packages, SongCreationService creator, SongProcessingQueue queue)
    {
        InitializeComponent(); Owner = owner; _packages = packages; _creator = creator; _queue = queue;
        WindowAppearance.UseSquareCorners(this);
        ProcessingList.ItemsSource=_rows;
        MaxWidth = SystemParameters.WorkArea.Width - 40; MaxHeight = SystemParameters.WorkArea.Height - 40;
        _refresh.Tick += (_, _) => { if (Interlocked.Exchange(ref _dirty, 0) != 0) RenderQueue(); };
        _queue.Changed += QueueChanged;
        Closing += ClosingDialog; Loaded += (_, _) => _refresh.Start();
        RenderForm(); RenderQueue();
    }
    private static Brush Color(string key) => (Brush)Application.Current.FindResource(key);
    private void CreateTab_Click(object sender, RoutedEventArgs e) => SelectMode("create");
    private void ImportTab_Click(object sender, RoutedEventArgs e) => SelectMode("import");
    internal void SelectMode(string mode) { _mode = mode == "import" ? "import" : "create"; FormError.Text = ""; RenderForm(); }
    private void RenderForm()
    {
        _updating = true;
        UrlInput.Text = _create.Url; TitleInput.Text = Current.Title; ArtistInput.Text = Current.Artist;
        CreateSource.Visibility = _mode == "create" ? Visibility.Visible : Visibility.Collapsed;
        ImportSource.Visibility = _mode == "import" ? Visibility.Visible : Visibility.Collapsed;
        PackageFilename.Text = _import.File.Length > 0 ? Path.GetFileName(_import.File) : SongPackageService.Extension;
        foreach (var (button, mode) in new[] { (CreateTab, "create"), (ImportTab, "import") })
        {
            button.Style=(Style)FindResource(_mode==mode?"SelectedButton":typeof(Button));
            button.Background = Color(_mode == mode ? "Tint" : "Raised"); button.Foreground = Color(_mode == mode ? "Accent" : "Sub");
        }
        _updating = false; RenderLyrics(); UpdateValidity();
    }
    private void UpdateValidity()
    {
        if (SubmitButton == null) return;
        bool urlValid;
        try { SongCreationService.NormalizeYouTubeUrl(_create.Url); urlValid = true; } catch (ArgumentException) { urlValid = false; }
        bool ready = _create.Video != null;
        ReadVideoLabel.Text = _reading ? "讀取中…" : ready ? "已讀取" : "讀取影片";
        ReadVideoIcon.Kind = ready ? "circle-check" : "search";
        AutomationProperties.SetName(ReadVideoButton, ready ? "已讀取" : "讀取影片");
        ReadVideoButton.IsEnabled = urlValid && !_reading;
        ChoosePackageButton.IsEnabled = !_selecting;
        SubmitButton.IsEnabled = !string.IsNullOrWhiteSpace(Current.Title) && !string.IsNullOrWhiteSpace(Current.Artist)
            && (_mode == "create" ? ready && !_reading : _import.Package != null && !_selecting)
            && Current.LyricsError.Length == 0;
        UrlHint.Visibility = UrlInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TitleHint.Visibility = TitleInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArtistHint.Visibility = ArtistInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FormError.Visibility = FormError.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Url_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _queue == null) return;
        _readCancellation?.Cancel(); _create.Url = UrlInput.Text; _create.Video = null; _reading = false;
        FormError.Text = ""; UpdateValidity();
    }
    private void Names_Changed(object sender, TextChangedEventArgs e)
    {
        if (_updating || _queue == null) return;
        Current.Title = TitleInput.Text; Current.Artist = ArtistInput.Text; FormError.Text=""; UpdateValidity();
    }
    private async void ReadVideo_Click(object sender, RoutedEventArgs e)
    {
        _readTask = ReadVideoAsync(); await _readTask;
    }
    private void Name_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox input && input.Text != input.Text.Trim()) input.Text = input.Text.Trim();
    }
    internal async Task ReadVideoAsync()
    {
        _readCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _readCancellation = cancellation;
        string url = _create.Url; _create.Video=null; _reading = true; FormError.Text = ""; UpdateValidity();
        try
        {
            var video = await _creator.ReadVideoAsync(url, null, cancellation.Token);
            if (!cancellation.IsCancellationRequested && url == _create.Url) { _create.Video = video; if (_mode == "create") RenderLyrics(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closing && _readCancellation == cancellation) FormError.Text = error.Message; }
        finally
        {
            if (_readCancellation == cancellation) { _readCancellation=null;_reading = false; if (!_closing) UpdateValidity(); }
            cancellation.Dispose();
        }
    }
    internal void SetVerifiedVideo(YouTubeVideo video) { _create.Url = video.Url; _create.Video = video; RenderForm(); }
    private void ChooseLyrics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "選擇歌詞檔", Filter = "歌詞檔案|*.lrc;*.ass|LRC 歌詞|*.lrc|ASS 歌詞|*.ass", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) SelectLyrics(picker.FileName);
    }
    internal void SelectLyrics(string file)
    {
        if (Current.Package?.LyricsMode.Length > 0) return;
        Current.Lyrics = null; Current.LyricsFilename = Path.GetFileName(file); Current.LyricsError = "";
        try { Current.Lyrics = SongLyrics.Read(file) with { Mode = Current.LyricsMode }; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { Current.LyricsError = error.Message; }
        FormError.Text = ""; RenderLyrics(); UpdateValidity();
    }
    private void ClearLyrics_Click(object sender, RoutedEventArgs e)
    {
        Current.Lyrics = null; Current.LyricsFilename = Current.LyricsError = "";
        FormError.Text = ""; RenderLyrics(); UpdateValidity();
    }
    private void LyricsMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _queue == null || Current.Package?.LyricsMode.Length > 0 || sender is not RadioButton { Tag: string mode, IsChecked: true }) return;
        Current.LyricsMode = mode;
        if (Current.Lyrics != null) Current.Lyrics = Current.Lyrics with { Mode = mode };
        FormError.Text = ""; RenderLyrics(); UpdateValidity();
    }
    private void RenderLyrics()
    {
        bool embedded = Current.Package?.LyricsMode.Length > 0;
        string mode = embedded ? Current.Package!.LyricsMode : Current.LyricsMode;
        if (Current.Lyrics != null)
        {
            Current.LyricsError = "";
            try { Current.Lyrics.Validate(_mode == "create" ? Current.Video?.Duration : Current.Package?.Duration); }
            catch (InvalidDataException error) { Current.LyricsError = error.Message; }
        }
        ChooseLyricsButton.IsEnabled = !embedded;
        LyricsFilename.Text = embedded ? "歌曲檔內含歌詞" : Current.LyricsFilename.Length > 0 ? Current.LyricsFilename : "選擇 .lrc 或 .ass 歌詞檔";
        LyricsFilename.ToolTip = Current.LyricsFilename.Length > 0 ? Current.LyricsFilename : null;
        LyricsFilename.Foreground = Color(embedded || Current.LyricsFilename.Length > 0 ? "Text" : "Sub");
        ClearLyricsButton.Visibility = !embedded && Current.LyricsFilename.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool updating = _updating; _updating = true;
        foreach (var (choice, value) in new[] { (ScrollLyricsButton, "scroll"), (KaraokeLyricsButton, "karaoke") })
        {
            choice.IsEnabled = !embedded;
            choice.IsChecked = mode == value;
        }
        _updating = updating;
        LyricsError.Text = Current.LyricsError;
        LyricsError.Visibility = Current.LyricsError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ChoosePackage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "選擇歌曲檔", Filter = "OpenKaraoke 歌曲檔|*.opok;*.openkaraoke", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        _fileTask = SelectPackageAsync(picker.FileName); await _fileTask;
    }
    internal async Task SelectPackageAsync(string file)
    {
        _selecting = true; _import.File = file; _import.Package = null;
        _import.Lyrics = null; _import.LyricsFilename = _import.LyricsError = "";
        _import.LyricsMode = "scroll";
        (_import.Title, _import.Artist) = SongPackageService.ParseFilename(file); FormError.Text = ""; RenderForm();
        try { _import.Package = await _packages.InspectAsync(file, _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { FormError.Text = error.Message; }
        finally { _selecting = false; if (!_closing) { RenderForm(); } }
    }
    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (!SubmitButton.IsEnabled) return;
        try
        {
            if (_mode == "create") _queue.AddCreate(_create.Video!, Current.Title, Current.Artist, Current.Lyrics);
            else _queue.AddImport(_import.File, Current.Title, Current.Artist, Current.Lyrics);
            Current.Title = Current.Artist = Current.Url = Current.File = ""; Current.Video = null; Current.Package = null;
            Current.Lyrics = null; Current.LyricsFilename = Current.LyricsError = "";
            Current.LyricsMode = "scroll";
            RenderForm(); RenderQueue();
        }
        catch (Exception error) { FormError.Text = error.Message; UpdateValidity(); }
    }
    private void QueueChanged() => Interlocked.Exchange(ref _dirty, 1);
    private void RenderQueue()
    {
        var items=_queue.Items;
        for(int i=0;i<items.Length;i++)
        {
            int old=-1;for(int j=i;j<_rows.Count;j++)if(_rows[j].Id==items[i].Id){old=j;break;}
            if(old<0)_rows.Insert(i,new ProcessingRow(items[i]));
            else {if(old!=i)_rows.Move(old,i);if(_rows[i].Item!=items[i])_rows[i]=new ProcessingRow(items[i]);}
        }
        while(_rows.Count>items.Length)_rows.RemoveAt(_rows.Count-1);
        ProcessingEmpty.Visibility = ProcessingList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void JobAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        var item = _queue.Items.FirstOrDefault(i => i.Id == id); if (item == null) return;
        try { if (item.Status is "error" or "cancelled") _queue.Retry(id); else _queue.Cancel(id); RenderQueue(); }
        catch (Exception error) { FormError.Text=error.Message; UpdateValidity(); }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
    private void Heading_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left)return;
        for(var element=e.OriginalSource as DependencyObject;element!=null;element=VisualTreeHelper.GetParent(element))
            if(element is Button)return;
        DragMove();
    }
    private async void ClosingDialog(object? sender, CancelEventArgs e)
    {
        if (_closed) return; e.Cancel = true; if (_closing) return; _closing = true;
        _refresh.Stop(); _queue.Changed -= QueueChanged; _lifetime.Cancel();
        try { if (_readTask != null) await _readTask; if (_fileTask != null) await _fileTask; }
        finally { _closed = true; _lifetime.Dispose(); _ = Dispatcher.BeginInvoke(Close); }
    }
}

public sealed class ProcessingRow(SongWorkItem item)
{
    public SongWorkItem Item=>item;
    public string Id => item.Id;
    public string Title => item.Title;
    public string Artist => item.Artist;
    public string Icon => item.Kind == "setup" ? "download" : item.Kind == "create" ? "sparkles" : "package";
    public Brush IconBrush => (Brush)Application.Current.FindResource(item.Status == "active" ? "Accent" : "Sub");
    public string Stage => item.Stage;
    public Visibility ArtistVisible=>item.Artist.Length>0?Visibility.Visible:Visibility.Collapsed;
    public Visibility ActionVisible=>item.Kind=="setup"&&item.Status!="error"?Visibility.Collapsed:Visibility.Visible;
    public string Percent => item.Status == "active" && item.Percent.HasValue ? $"{item.Percent:0}%" : "";
    public double Progress => item.Percent ?? 0;
    public bool Indeterminate => item.Status == "active" && !item.Percent.HasValue;
    public Visibility StageVisible => Stage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisible => item.Status == "active" ? Visibility.Visible : Visibility.Collapsed;
    public Brush StageBrush => (Brush)Application.Current.FindResource(item.Status == "active" ? "Accent" : item.Status == "error" ? "Danger" : "Sub");
    public string ActionIcon => item.Status is "error" or "cancelled" ? "rotate-ccw" : "x";
    public string ActionName => (item.Status is "error" or "cancelled" ? "重新處理：" : "取消處理：") + item.Title;
    public string? ActionTooltip => item.Status is "error" or "cancelled" ? "重試" : null;
}
