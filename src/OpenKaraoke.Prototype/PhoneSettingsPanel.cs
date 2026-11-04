using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenKaraoke.Phone;
using OpenKaraoke.Scoring;
using QRCoder;

namespace OpenKaraoke.Prototype;

public sealed class PhoneSettingsPanel : StackPanel
{
    private sealed class ChannelRow
    {
        public required int Slot;
        public TextBlock Name=new(){FontSize=13},State=new(){FontSize=12,TextAlignment=TextAlignment.Right};
    }
    private readonly PhoneHost _host;
    private readonly ComboBox _network=new(){SelectedValuePath="Address",Name="PhoneNetwork"};
    private readonly TextBlock _error=new(){FontSize=12,TextWrapping=TextWrapping.Wrap};
    private readonly StackPanel _details=new(),_connections=new(){Margin=new(0,16,0,0)};
    private readonly StackPanel _setup=new(){Margin=new(0,16,0,0)};
    private readonly Grid _codes=new(){Margin=new(0,17,0,0)};
    private readonly Image _setupQr=new(){Width=168,Height=168},_micQr=new(){Width=168,Height=168};
    private readonly TextBlock _fingerprint=new(){FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)};
    private readonly TextBlock _previewHint=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,0)};
    private readonly Button _retry=new(){Content="重新準備連線",FontSize=12,Margin=new(0,10,0,0)};
    private readonly List<ChannelRow> _rows=[];
    private string _lastSetup="",_lastMic="";
    private bool _closed,_sourceChanging;
    public event Action? DraftChanged;
    public event Action? RetryRequested;
    public PhoneSettingsPanel(PhoneHost host,VoiceSettings settings)
    {
        _host=host;_error.Foreground=Color("Danger");_fingerprint.Foreground=Color("Sub");
        for(int slot=1;slot<=PhoneMixer.MaximumMicrophones;slot++)AddConnection(slot);
        var addresses=PhoneHost.Addresses().ToList();string selected=host.Address.Length>0?host.Address:settings.PhoneAddress;
        if(selected.Length>0&&addresses.All(a=>a.Address!=selected))addresses.Insert(0,new(selected,"已選擇網路"));
        _network.ItemsSource=addresses;_network.SelectedValue=selected.Length>0?selected:addresses.FirstOrDefault()?.Address;
        _setup.Children.Add(new TextBlock{Text="網路",FontSize=12,Foreground=Color("Sub"),Margin=new(0,0,0,7)});_setup.Children.Add(_network);_previewHint.Foreground=Color("Sub");_setup.Children.Add(_previewHint);_details.Children.Add(_setup);
        var codes=_codes;codes.ColumnDefinitions.Add(new());codes.ColumnDefinitions.Add(new());
        var first=CodeCard("首次設定",_setupQr);var second=CodeCard("連接麥克風",_micQr);codes.Children.Add(first);Grid.SetColumn(second,1);codes.Children.Add(second);_setup.Children.Add(codes);
        _setup.Children.Add(new Expander{Header="憑證指紋",Style=(Style)FindResource("PhoneExpander"),Margin=new(0,10,0,0),Content=_fingerprint});
        _details.Children.Add(_connections);Children.Add(_details);_error.Margin=new(0,12,0,0);Children.Add(_error);Children.Add(_retry);_retry.Click+=(_,_)=>RetryRequested?.Invoke();
        _network.SelectionChanged+=(_,_)=>{Render();DraftChanged?.Invoke();};_host.Changed+=HostChanged;Render();
    }
    private static Brush Color(string name)=>(Brush)Application.Current.FindResource(name);
    private void AddConnection(int slot)
    {
        var row=new ChannelRow{Slot=slot};row.Name.Text="手機 "+slot;
        var panel=new DockPanel();DockPanel.SetDock(row.State,Dock.Right);panel.Children.Add(row.State);panel.Children.Add(row.Name);
        _connections.Children.Add(new Border{Child=panel,Padding=new(0,10,0,10),BorderBrush=Color("Line"),BorderThickness=new(0,0,0,1)});_rows.Add(row);
    }
    private static StackPanel CodeCard(string title,Image image)
    {
        var card=new StackPanel();card.Children.Add(new TextBlock{Text=title,FontSize=12,HorizontalAlignment=HorizontalAlignment.Center,Margin=new(0,0,0,9)});
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.NearestNeighbor);image.Stretch=Stretch.Uniform;card.Children.Add(new Border{Width=168,Height=168,Background=Color("Raised"),Child=image});
        return card;
    }
    private static BitmapImage Qr(string url)
    {byte[] png=PngByteQRCodeHelper.GetQRCode(url,QRCodeGenerator.ECCLevel.M,4);using var input=new MemoryStream(png);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=input;image.EndInit();image.Freeze();return image;}
    public VoiceSettings Read(VoiceSettings settings)=>settings.DeviceId!="phone"?settings with{PhoneEnabled=false}:settings with
    {
        PhoneEnabled=true,PhoneAddress=(string?)_network.SelectedValue??""
    };
    public void BeginSourceChange(){_sourceChanging=true;_error.Text="";Render();}
    public void CompleteSourceChange(){_sourceChanging=false;Render();}
    private void HostChanged()=>Dispatcher.BeginInvoke(()=>{if(!_closed)Render();});
    private void Render()
    {
        bool changing=_sourceChanging||_host.IsChanging;
        bool ready=!changing&&_host.Running&&_host.SetupUrl.Length>0&&_host.SecureUrl.Length>0;
        bool pendingNetwork=ready&&!string.IsNullOrEmpty((string?)_network.SelectedValue)&&(string?)_network.SelectedValue!=_host.Address;
        _previewHint.Text=changing?"正在準備連線…":pendingNetwork?$"目前連線使用 {_host.Address}；按「測試收音」預覽新網路。":"連線尚未就緒，請重新準備。";
        _previewHint.Visibility=ready&&!pendingNetwork?Visibility.Collapsed:Visibility.Visible;
        _codes.Visibility=ready?Visibility.Visible:Visibility.Collapsed;_retry.Visibility=!changing&&!ready?Visibility.Visible:Visibility.Collapsed;
        _network.IsEnabled=!changing;_codes.IsEnabled=ready;_connections.IsEnabled=ready;
        foreach(var row in _rows){bool online=_host.Mixer.Channels.Any(c=>c.Slot==row.Slot&&c.Audio.Connected);row.Name.Foreground=Color(online?"Accent":"Text");row.State.Text=online?"已連線":"未連線";row.State.Foreground=Color(online?"Accent":"Sub");}
        string setup=ready?_host.SetupUrl:"",microphone=ready?_host.MicrophoneUrl:"";
        _fingerprint.Text=ready?_host.Fingerprint:"";if(_lastSetup!=setup){_lastSetup=setup;_setupQr.Source=setup.Length>0?Qr(setup):null;}if(_lastMic!=microphone){_lastMic=microphone;_micQr.Source=microphone.Length>0?Qr(microphone):null;}
        if(_host.Error.Length>0)_error.Text=_host.Error;_error.Visibility=_error.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
    }
    public void Finish(){_closed=true;_host.Changed-=HostChanged;}
}
