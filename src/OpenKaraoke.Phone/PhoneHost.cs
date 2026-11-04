using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Phone;

public sealed record LanAddress(string Address,string Name){public override string ToString()=>Name+" · "+Address;}

public sealed class PhoneHost(string dataDirectory,int httpPort=8080,int httpsPort=8443,bool allowLoopback=false,bool playOutput=true) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly object _channelSettingsGate=new();
    private WebApplication? _server;
    private PhoneCertificates? _certificates;
    private readonly ConcurrentDictionary<string,WebSocket> _sockets=new();
    private string _token="";
    private VoiceSettings _settings=new();
    private bool _disposed;
    private bool _audioEventsWired;
    public PhoneMixer Mixer {get;}=new();
    public event Action? Changed;
    public bool Running=>_server!=null;
    public bool IsChanging {get;private set;}
    public bool TargetEnabled {get;private set;}
    public string SetupUrl {get;private set;}="";
    public string MicrophoneUrl=>Running?SecureUrl+"/#pair="+_token:"";
    public string SecureUrl {get;private set;}="";
    public string Fingerprint=>_certificates?.Fingerprint??"";
    public string Error {get;private set;}="";
    public string Address {get;private set;}="";
    public VoiceSettings Configuration=>_settings;
    public byte[]? PublicCertificate=>_certificates?.PublicCertificate;

    public static LanAddress[] Addresses()=>NetworkInterface.GetAllNetworkInterfaces()
        .Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
        .OrderBy(n=>n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211?0:1)
        .SelectMany(n=>n.GetIPProperties().UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork&&IsPrivate(a.Address)).Select(a=>new LanAddress(a.Address.ToString(),n.Name)))
        .DistinctBy(a=>a.Address).ToArray();
    private static bool IsPrivate(IPAddress address)
    {
        byte[] b=address.GetAddressBytes();return b.Length==4&&(b[0]==10||b[0]==192&&b[1]==168||b[0]==172&&b[1] is >=16 and <=31||b[0]==169&&b[1]==254);
    }
    public async Task ApplyAsync(VoiceSettings settings,bool preservePairing=false,bool startOutput=true)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);Error="";
            Mixer.EchoLevel=settings.EchoLevel;
            if(!_audioEventsWired){Mixer.OutputFailed+=_=>{Error="手機收音播放已停止，請檢查電腦音訊輸出裝置。";Changed?.Invoke();};Mixer.Changed+=()=>Changed?.Invoke();_audioEventsWired=true;}
            if(!settings.PhoneEnabled){lock(_channelSettingsGate)_settings=settings;if(Running){IsChanging=true;TargetEnabled=false;Changed?.Invoke();}await StopLockedAsync(preservePairing);return;}
            string address=settings.PhoneAddress;
            if(address.Length==0)address=Addresses().FirstOrDefault()?.Address??throw new IOException("請先連接 Wi-Fi 或區域網路。");
            if(!IPAddress.TryParse(address,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork||(!IsPrivate(ip)&&!(allowLoopback&&IPAddress.IsLoopback(ip))))throw new IOException("請選擇電腦的區域網路位址。");
            if(!(allowLoopback&&IPAddress.IsLoopback(ip))&&!Addresses().Any(a=>a.Address==address))throw new IOException("此網路位址目前無法使用，請重新選擇。");
            lock(_channelSettingsGate)_settings=settings with{PhoneAddress=address};
            if(Running&&Address==address){if(startOutput)StartOutput();ApplyChannels();Changed?.Invoke();return;}
            IsChanging=true;TargetEnabled=true;Changed?.Invoke();
            await StopLockedAsync(preservePairing);Address=address;
            _certificates=await Task.Run(()=>new PhoneCertificates(Path.Combine(dataDirectory,"certificates"),ip));if(_token.Length==0)_token=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[],ApplicationName=typeof(PhoneHost).Assembly.FullName});
            builder.Logging.ClearProviders();builder.Configuration.Sources.Clear();
            builder.WebHost.ConfigureKestrel(server=>
            {
                server.AddServerHeader=false;server.Limits.MaxRequestBodySize=2048;server.Limits.MaxConcurrentConnections=12;
                server.Limits.KeepAliveTimeout=TimeSpan.FromSeconds(25);
                server.Listen(ip,httpPort);server.Listen(ip,httpsPort,endpoint=>endpoint.UseHttps(_certificates.Server));
            });
            var app=builder.Build();app.UseWebSockets(new WebSocketOptions{KeepAliveInterval=TimeSpan.FromSeconds(10)});
            app.Use(async(context,next)=>
            {
                context.Response.Headers.CacheControl="no-store";context.Response.Headers["Referrer-Policy"]="no-referrer";
                context.Response.Headers.XContentTypeOptions="nosniff";
                context.Response.Headers["Content-Security-Policy"]="default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' wss://"+context.Request.Host+"; worker-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
                context.Response.Headers["Permissions-Policy"]="microphone=(self), camera=(), screen-wake-lock=(self)";
                if(context.Request.Host.Host!=address){context.Response.StatusCode=400;return;}
                if(!context.Request.IsHttps&&context.Request.Path!="/"&&context.Request.Path!="/ca.cer"&&context.Request.Path!="/style.css"&&context.Request.Path!="/setup.js"&&context.Request.Path!="/logo.svg"){context.Response.StatusCode=404;return;}
                await next(context);
            });
            app.MapGet("/",context=>WriteText(context,context.Request.IsHttps?Asset("index.html"):SetupPage(),"text/html; charset=utf-8"));
            app.MapGet("/style.css",context=>WriteText(context,Asset("style.css"),"text/css; charset=utf-8"));
            app.MapGet("/logo.svg",context=>WriteText(context,Asset("logo.svg"),"image/svg+xml"));
            foreach(string file in new[]{"app.js","audio-worklet.js","setup.js"})
            {string name=file;app.MapGet("/"+name,context=>WriteText(context,Asset(name),"text/javascript; charset=utf-8"));}
            app.MapGet("/ca.cer",async context=>
            {
                context.Response.ContentType="application/pkix-cert";context.Response.Headers.ContentDisposition="attachment; filename=OpenKaraoke-Local-CA.cer";
                await context.Response.Body.WriteAsync(_certificates.PublicCertificate,context.RequestAborted);
            });
            app.MapPost("/pair",context=>
            {
                if(!BearerAuthorized(context)){context.Response.StatusCode=401;return Task.CompletedTask;}
                if(DeviceId(context)==null)context.Response.Cookies.Append("openkaraoke-device",Guid.NewGuid().ToString("N"),new CookieOptions{HttpOnly=true,Secure=true,SameSite=SameSiteMode.Strict,Path="/",IsEssential=true});
                context.Response.Cookies.Append("openkaraoke-mic",_token,new CookieOptions{HttpOnly=true,Secure=true,SameSite=SameSiteMode.Strict,Path="/",IsEssential=true});context.Response.StatusCode=204;return Task.CompletedTask;
            });
            app.MapGet("/api/state",async context=>
            {
                if(!Authorized(context)){context.Response.StatusCode=401;return;}
                var own=Mixer.Channels.FirstOrDefault(c=>c.Id==DeviceId(context));
                await context.Response.WriteAsJsonAsync(new{connected=own?.Audio.Connected??false,muted=own?.Audio.Muted??true,level=own?.Audio.Level??0,slot=own?.Slot,count=Mixer.ConnectedCount,limit=PhoneMixer.MaximumMicrophones},context.RequestAborted);
            });
            app.MapMethods("/ws",["GET","CONNECT"],HandleSocketAsync);
            try{await app.StartAsync();}
            catch{await app.DisposeAsync();throw;}
            _server=app;
            var urls=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            SetupUrl=urls.Single(u=>u.StartsWith("http:",StringComparison.Ordinal))+"/";SecureUrl=urls.Single(u=>u.StartsWith("https:",StringComparison.Ordinal));
            if(startOutput)StartOutput();
            ApplyChannels();
        }
        catch(Exception error)
        {
            await StopLockedAsync(preservePairing);Error=error is IOException&&error.Message.StartsWith("請",StringComparison.Ordinal)?error.Message:"手機麥克風無法啟動，請確認網路與 8080／8443 連接埠。";throw new IOException(Error,error);
        }
        finally{IsChanging=false;_gate.Release();Changed?.Invoke();}
    }
    private void StartOutput()
    {
        if(!playOutput||Mixer.OutputActive)return;
        try{Mixer.StartOutput();}catch(Exception){Error="無法播放手機收音，請檢查電腦音訊輸出裝置。";}
    }
    public async Task ResetPairingAsync()
    {
        await _gate.WaitAsync();try{if(!Running)return;_token=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));foreach(var socket in _sockets.Values)socket.Abort();Mixer.DisconnectAll();}finally{_gate.Release();Changed?.Invoke();}
    }
    private static string? DeviceId(HttpContext context)=>Guid.TryParseExact(context.Request.Cookies["openkaraoke-device"],"N",out var id)?id.ToString("N"):null;
    public void SetMonitorVolume(int volume)
    {
        lock(_channelSettingsGate)
        {
            _settings=_settings with{PhoneMonitorVolume=Math.Clamp(volume,0,100)};
            ApplyChannels();
        }
    }
    private void ApplyChannels()
    {lock(_channelSettingsGate)foreach(var channel in Mixer.Channels)channel.Audio.Configure(false,_settings.PhoneMonitorVolume);}
    private bool Matches(string? candidate)=>candidate!=null&&candidate.Length==_token.Length&&_token.Length>0&&CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate),Encoding.UTF8.GetBytes(_token));
    private bool BearerAuthorized(HttpContext c)=>c.Request.IsHttps&&Matches(c.Request.Headers.Authorization.ToString() is {} h&&h.StartsWith("Bearer ",StringComparison.Ordinal)?h[7..]:null);
    private bool Authorized(HttpContext c)=>c.Request.IsHttps&&(Matches(c.Request.Cookies["openkaraoke-mic"])||BearerAuthorized(c));
    private async Task HandleSocketAsync(HttpContext context)
    {
        if(!Authorized(context)){context.Response.StatusCode=401;return;}
        if(context.Request.Headers.Origin.ToString()!="https://"+context.Request.Host){context.Response.StatusCode=403;return;}
        if(!context.WebSockets.IsWebSocketRequest){context.Response.StatusCode=400;return;}
        if(!Running){context.Response.StatusCode=503;return;}
        string id=DeviceId(context)??Guid.NewGuid().ToString("N");var channel=Mixer.TryConnect(id);
        if(channel==null){context.Response.StatusCode=409;return;}
        int version=channel.Version;WebSocket? socket=null;
        try
        {
            socket=await context.WebSockets.AcceptWebSocketAsync();_sockets[id]=socket;
            ApplyChannels();
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{type="hello",slot=channel.Slot}),WebSocketMessageType.Text,true,context.RequestAborted);
            Changed?.Invoke();var bytes=new byte[8193+PhonePacketReader.HeaderSize];int count=0;bool formatReceived=false,dataReceived=false;PhonePacketReader? packets=null;
            while(socket.State==WebSocketState.Open)
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var result=await socket.ReceiveAsync(bytes.AsMemory(count),timeout.Token);
                if(result.MessageType==WebSocketMessageType.Close){await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"Stopped",CancellationToken.None);break;}
                count+=result.Count;
                if(count>8192+(packets==null?0:PhonePacketReader.HeaderSize)){await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData,"Packet too large",CancellationToken.None);break;}
                if(!result.EndOfMessage)continue;
                if(result.MessageType==WebSocketMessageType.Text)
                {
                    try
                    {
                        using var document=JsonDocument.Parse(bytes.AsMemory(0,count));var value=document.RootElement;
                        if(value.TryGetProperty("type",out var type)&&type.GetString()=="ping")
                        {
                            int pingId=value.GetProperty("id").GetInt32();
                            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{type="pong",id=pingId,serverTime=MicrophoneCapture.Now*1000}),WebSocketMessageType.Text,true,timeout.Token);
                        }
                        else
                        {
                            if(formatReceived||dataReceived)throw new InvalidOperationException("Format already set");
                            int rate=value.GetProperty("sampleRate").GetInt32();channel.Audio.SetFormat(rate);
                            if(value.TryGetProperty("protocol",out var protocol)){if(protocol.GetInt32()!=2)throw new InvalidOperationException("Unknown protocol");packets=new(rate);}
                            formatReceived=true;
                        }
                    }
                    catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException or FormatException or OverflowException){await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData,"Invalid format",CancellationToken.None);break;}
                }
                else if(!(packets?.Accept(bytes.AsSpan(0,count),MicrophoneCapture.Now,channel.Audio)??channel.Audio.AcceptPcm(bytes.AsSpan(0,count),MicrophoneCapture.Now))){await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData,"Invalid PCM16",CancellationToken.None);break;}
                else dataReceived=true;
                count=0;
            }
        }
        catch(Exception error) when(error is WebSocketException or OperationCanceledException or IOException){ }
        finally
        {
            if(socket!=null)_sockets.TryRemove(new KeyValuePair<string,WebSocket>(id,socket));
            Mixer.Disconnect(channel,version);socket?.Dispose();Changed?.Invoke();
        }
    }
    private static string Asset(string name)
    {using var stream=typeof(PhoneHost).Assembly.GetManifestResourceStream("OpenKaraoke.Phone.Web."+name)??throw new FileNotFoundException(name);using var reader=new StreamReader(stream);return reader.ReadToEnd();}
    private static Task WriteText(HttpContext c,string text,string type){c.Response.ContentType=type;return c.Response.WriteAsync(text,c.RequestAborted);}
    private string SetupPage()=>Asset("setup.html").Replace("{{fingerprint}}",Fingerprint,StringComparison.Ordinal);
    private async Task StopLockedAsync(bool preservePairing=false)
    {
        foreach(var socket in _sockets.Values)socket.Abort();var server=_server;_server=null;
        if(server!=null){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));try{await server.StopAsync(timeout.Token);}catch(OperationCanceledException){}await server.DisposeAsync();}
        Mixer.DisconnectAll();_certificates?.Dispose();_certificates=null;if(!preservePairing)_token="";SetupUrl=SecureUrl=Address="";
    }
    public async ValueTask DisposeAsync()
    {await _gate.WaitAsync();try{if(_disposed)return;_disposed=true;await StopLockedAsync();Mixer.Dispose();}finally{_gate.Release();}}
}
