using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using OpenKaraoke.Phone;
using OpenKaraoke.Scoring;

string root=Path.GetFullPath(args.Length>0?args[0]:".");string folder=Path.Combine(root,"artifacts","phone-tests-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(folder);var checks=new List<object>();
void Check(string name,bool passed,object? evidence=null){checks.Add(new{Name=name,Passed=passed,Evidence=evidence});Console.WriteLine($"{(passed?"PASS":"FAIL")} {name}");if(!passed)throw new InvalidOperationException(name);}
async Task Wait(Func<bool> condition){var watch=System.Diagnostics.Stopwatch.StartNew();while(!condition()){if(watch.Elapsed.TotalSeconds>12)throw new TimeoutException("手機麥克風驗收逾時。");await Task.Delay(20);}}
byte[] Tone(double frequency,int sample,int count,int rate){var bytes=new byte[count*2];for(int i=0;i<count;i++)System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i*2,2),(short)(Math.Sin(2*Math.PI*frequency*(sample+i)/rate)*10000));return bytes;}
byte[] Packet(uint sequence,double end,double frame,double uncertainty=2)
{var bytes=new byte[32+960];BinaryPrimitives.WriteUInt32LittleEndian(bytes,PhonePacketReader.Magic);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),sequence);BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(8),end);BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(16),frame);BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(24),uncertainty);Tone(440,0,480,48000).CopyTo(bytes,32);return bytes;}
try
{
    if(args.Contains("--published-assets"))
    {
        string published=Path.GetFullPath(args[Array.IndexOf(args,"--published-assets")+1]);
        var assembly=System.Reflection.Assembly.LoadFile(Path.Combine(published,"app","OpenKaraoke.Phone.dll"));
        foreach(string name in new[]{"index.html","style.css","app.js","audio-worklet.js","setup.html","setup.js","logo.svg"})
        {
            using var embedded=assembly.GetManifestResourceStream("OpenKaraoke.Phone.Web."+name)??throw new IOException("缺少正式手機頁面資源："+name);
            using var source=File.OpenRead(name=="logo.svg"?Path.Combine(root,"docs","assets",name):Path.Combine(root,"src","OpenKaraoke.Phone","Web",name));
            Check("正式套件手機資源與已驗證原始碼一致："+name,System.Security.Cryptography.SHA256.HashData(embedded).SequenceEqual(System.Security.Cryptography.SHA256.HashData(source)));
        }
        await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(folder);return;
    }
    if(args.Contains("--probe-echo"))
    {
        var effect=new VoiceEcho();var signal=new float[48000*3];signal[0]=.2f;effect.Process(signal,48000);
        double early=signal.Skip(4800).Take(14400).Sum(v=>(double)v*v),late=signal.Skip(19200).Sum(v=>(double)v*v);
        int last=Array.FindLastIndex(signal,v=>Math.Abs(v)>.00002f);
        Console.WriteLine(JsonSerializer.Serialize(new{Level=effect.Level,Dry=signal[0],FirstRepeatPeak=signal.Skip(4800).Take(14400).Max(v=>Math.Abs(v)),EarlyTailEnergy=early,LateTailEnergy=late,DecayToMinus80DbSeconds=last/48000d,Scope="合成脈衝，不是實體 KTV 聽感比對"}));return;
    }
    if(args.Contains("--probe-local"))
    {
        await using var local=new LocalMicrophone();long received=0;double energy=0;
        local.Audio.Received+=(samples,_,_)=>{Interlocked.Add(ref received,samples.Length);foreach(float value in samples)energy+=(double)value*value;};
        await local.StartAsync("default",0);await Task.Delay(300);long before=local.Audio.UnderrunSamples,drops=local.Audio.DiscardedSamples,receivedBefore=received;
        await Task.Delay(4000);
        Console.WriteLine(JsonSerializer.Serialize(new{Rate=local.Audio.InputRate,OutputRate=local.Mixer.WaveFormat.SampleRate,Received=received-receivedBefore,Underrun=local.Audio.UnderrunSamples-before,Discarded=local.Audio.DiscardedSamples-drops,Rms=Math.Sqrt(energy/Math.Max(1,received)),BufferMs=local.Audio.BufferedMilliseconds,Scope="本機四秒擷取與靜音 WASAPI 輸出，不儲存錄音"}));return;
    }
    if(args.Contains("--serve"))
    {
        string info=Path.GetFullPath(args[Array.IndexOf(args,"--serve")+1]);await using var serving=new PhoneHost(Path.Combine(folder,"browser"),0,0,true,false);await serving.ApplyAsync(new(PhoneEnabled:true,PhoneAddress:"127.0.0.1"));
        await File.WriteAllTextAsync(info,JsonSerializer.Serialize(new{serving.MicrophoneUrl,serving.SecureUrl,serving.SetupUrl}));using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));while(!File.Exists(info+".stop"))await Task.Delay(100,timeout.Token);return;
    }
    string settingsPath=Path.Combine(folder,"voice.json");
    new VoiceSettings(DeviceId:"phone",DelayMilliseconds:180,ScoringEnabled:true,PhoneAddress:"192.168.10.50").Save(settingsPath);
    var restored=VoiceSettings.Load(settingsPath);
    Check("電腦麥克風音量預設 100%",restored.LocalVolume==100);
    Check("手機共用麥克風音量預設 100%",restored.PhoneMonitorVolume==100);
    Check("迴聲預設使用正常效果",restored.EchoLevel==0);
    Check("設定保存裝置、延遲、開關與網路",restored.DeviceId=="phone"&&restored.DelayMilliseconds==180&&restored.ScoringEnabled&&restored.PhoneEnabled&&restored.PhoneAddress=="192.168.10.50");
    restored.Save(settingsPath);
    Check("語音設定可完整保存與還原",VoiceSettings.Load(settingsPath)==restored);
    File.WriteAllText(settingsPath,"""{"DeviceId":"default","PhoneEnabled":true}""");
    Check("電腦來源保持手機服務停用",!VoiceSettings.Load(settingsPath).PhoneEnabled);
    File.WriteAllText(settingsPath,"""{"DeviceId":"phone","PhoneEnabled":false}""");
    Check("手機來源保持手機服務啟用",VoiceSettings.Load(settingsPath).PhoneEnabled);
    new VoiceSettings(DeviceId:"default",PhoneEnabled:true).Save(settingsPath);
    using(var saved=JsonDocument.Parse(File.ReadAllText(settingsPath)))Check("儲存時手機服務狀態與唯一音訊來源一致",!saved.RootElement.GetProperty("PhoneEnabled").GetBoolean());
    new VoiceSettings(PhoneMonitorVolume:-5).Save(settingsPath);
    var boundedVolumes=VoiceSettings.Load(settingsPath);boundedVolumes.Save(settingsPath);
    Check("共用手機音量不可低於 0%",boundedVolumes.PhoneMonitorVolume==0&&VoiceSettings.Load(settingsPath)==boundedVolumes);
    new VoiceSettings(PhoneMonitorVolume:73,LocalVolume:42).Save(settingsPath);
    var sharedVolume=VoiceSettings.Load(settingsPath);sharedVolume.Save(settingsPath);
    Check("共用手機音量與電腦音量分別保存",sharedVolume.PhoneMonitorVolume==73&&sharedVolume.LocalVolume==42&&VoiceSettings.Load(settingsPath)==sharedVolume);
    new VoiceSettings(PhoneMonitorVolume:140).Save(settingsPath);
    Check("共用手機音量不可超過 100%",VoiceSettings.Load(settingsPath).PhoneMonitorVolume==100);
    using(var audio=new PhoneAudio())
    {
        Check("每路預設靜音且防止重複開啟",audio.Muted&&audio.Connect()&&!audio.Connect());
        Check("拒絕奇數或過大的 PCM",!audio.AcceptPcm([1],0)&&!audio.AcceptPcm(new byte[8194],0));
        int received=0;audio.Received+=(samples,rate,time)=>received+=samples.Length;audio.AcceptPcm([255,127,0,128],1);var output=new byte[8];audio.Read(output,0,output.Length);
        Check("靜音不阻斷原始評分輸入",received==2&&output.All(b=>b==0)&&audio.BufferedSamples==0);
        audio.Configure(false,100);audio.AcceptPcm([255,127,0,128],2);audio.Read(output,0,output.Length);Check("PCM 正負值與音量正確",Math.Abs(BitConverter.ToSingle(output,0)-.8)<.001&&Math.Abs(BitConverter.ToSingle(output,4)+.8)<.001);
        for(int i=0;i<10;i++)audio.AcceptPcm(new byte[8192],3);Check("16kHz 積壓後只保留最新 10ms",audio.BufferedSamples==160&&audio.DiscardedSamples>0);
        audio.SetFormat(48000);audio.AcceptPcm(new byte[8192],3);Check("48kHz 積壓後同樣只保留最新 10ms",audio.InputRate==48000&&audio.BufferedSamples==480&&audio.BufferedMilliseconds==10);
        audio.Configure(true,45);audio.Read(output,0,output.Length);Check("靜音立即清空音訊",audio.BufferedSamples==0&&output.All(b=>b==0));
        audio.SetFormat(16000);audio.Configure(false,100);audio.AcceptPcm([255,127,0,0],4);var impulse=new byte[2561*4];audio.Read(impulse,0,impulse.Length);Check("手機監聽保留原始脈衝且沒有殘響尾音",Math.Abs(BitConverter.ToSingle(impulse,0)-.8)<.001&&System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(impulse).ToArray().Skip(1).All(value=>value==0));
        audio.Disconnect();Check("中斷清空收音且恢復靜音",audio.Muted&&!audio.Connected&&audio.Level==0);
    }
    using(var audio=new PhoneAudio())
    {
        audio.Connect();audio.Configure(false,100);audio.SetFormat(48000);var reader=new PhonePacketReader(48000);var ends=new List<double>();audio.Received+=(_,_,end)=>ends.Add(end);
        Check("帶時間戳封包可進入原始評分與監聽",reader.Accept(Packet(1,1000,480),1.005,audio)&&ends.Count==1&&audio.BufferedSamples==480);
        Check("過期封包不進入評分或監聽",reader.Accept(Packet(2,1010,960),1.2,audio)&&ends.Count==1&&audio.StalePackets==1);
        Check("網路恢復後維持來源時間，略過部分不壓縮時間軸",reader.Accept(Packet(3,1021,1440),1.025,audio)&&ends.Count==2&&Math.Abs(ends[1]-ends[0]-.02)<.000001);
        Check("拒絕重複序號、無效時間與未知封包",!reader.Accept(Packet(3,1030,1920),1.03,audio)&&!reader.Accept(Packet(4,double.NaN,1920),1.03,audio)&&!reader.Accept(Packet(4,1030,1920,-1),1.03,audio)&&!reader.Accept(new byte[992],1.03,audio));
        audio.Configure(true,100);audio.Configure(false,100);var backlog=Enumerable.Repeat(new byte[]{255,127},480).SelectMany(b=>b).Concat(Enumerable.Repeat(new byte[]{0,128},480).SelectMany(b=>b)).ToArray();audio.AcceptPcm(backlog,2);var output=new byte[480*4];audio.Read(output,0,output.Length);
        Check("佇列淘汰實際保留新聲音而非舊聲音",System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(output).ToArray().All(v=>v<-.79));
    }
    using(var mixer=new PhoneMixer())
    {
        var input=mixer.TryConnect("pc")!.Audio;input.SetFormat(48000);input.Configure(false,50);float[]? scoring=null;input.Received+=(samples,_,_)=>scoring=samples;
        var samples=Enumerable.Repeat(.25f,480).ToArray();input.AcceptSamples(samples,1);var output=new byte[480*2*4];mixer.Read(output,0,output.Length);var values=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(output).ToArray();
        Check("PC 浮點收音經共用混音路徑播放，左右一致且遵守音量",values.Take(900).All(v=>Math.Abs(v-.1f)<.0001));
        Check("PC 監聽音量不修改傳給評分的原始訊號",ReferenceEquals(samples,scoring)&&scoring.All(v=>v==.25f));
        Check("拒絕無效 PC 浮點訊號",!input.AcceptSamples([float.NaN],2)&&!input.AcceptSamples([],2));
    }
    foreach (int rate in new[] { 44100, 48000 })
    {
        using var audio = new PhoneAudio();
        audio.SetFormat(rate);
        audio.Connect();
        audio.Configure(false, 100);
        int inputFrames = rate / 100, packet = 0;
        float[] Read()
        {
            var output = new float[480];
            audio.AddOutput(output, 48000);
            return output;
        }
        void Feed()
        {
            audio.AcceptPcm(Tone(440, packet * inputFrames, inputFrames, rate), ++packet * .01);
        }
        static double PeakStep(float previous, float[] output)
        {
            double peak = 0;
            foreach (float sample in output)
            {
                peak = Math.Max(peak, Math.Abs(sample - previous));
                previous = sample;
            }
            return peak;
        }

        Feed();
        var prior = Read();
        Check($"手機 {rate}Hz 第一包即開始監聽", prior[1] > .01f && audio.BufferedMilliseconds == 0);
        for (int i = 1; i < 18; i++) { Feed(); prior = Read(); }
        long underruns = audio.UnderrunSamples;
        var gap = Read();
        Feed();
        var resumed = Read();
        double gapStep = PeakStep(prior[^1], gap), resumeStep = PeakStep(gap[^1], resumed);
        Check($"手機 {rate}Hz 補零與恢復接點平滑，恢復時在同一輸出區塊播放",
            audio.UnderrunSamples - underruns == inputFrames && gap.Skip(100).All(v => v == 0)
            && resumed.Take(32).Any(v => Math.Abs(v) > .005f) && gapStep < .03 && resumeStep < .03,
            new { GapStep = gapStep, ResumeStep = resumeStep });

        long discarded = audio.DiscardedSamples;
        Feed();
        Feed();
        double buffer = audio.BufferedMilliseconds;
        var spliced = Read();
        double spliceStep = PeakStep(resumed[^1], spliced);
        Check($"手機 {rate}Hz 丟樣接點平滑並維持最新 10ms 緩衝",
            audio.DiscardedSamples - discarded == inputFrames && buffer == 10 && spliceStep < .03,
            new { PeakStep = spliceStep, BufferedMilliseconds = buffer });
    }
    double MonitorTone(double hertz)
    {
        using var audio=new PhoneAudio();audio.Connect();audio.Configure(false,100);audio.SetFormat(96000);double energy=0;int count=0;
        for(int block=0;block<60;block++)
        {
            var input=Enumerable.Range(0,960).Select(i=>(float)(.25*Math.Sin(2*Math.PI*hertz*(block*960+i)/96000))).ToArray();audio.AcceptSamples(input,block*.01);
            var output=new float[480];audio.AddOutput(output,48000);if(block>=10){energy+=output.Sum(v=>(double)v*v);count+=output.Length;}
        }
        return Math.Sqrt(energy/count);
    }
    double voice=MonitorTone(1000),ultrasonic=MonitorTone(36000);
    Check("96→48kHz 降取樣保留歌聲，抑制折回可聽頻段的 36kHz 訊號",voice>.139&&voice<.143&&ultrasonic/voice<.001,new{VoiceRms=voice,UltrasonicRms=ultrasonic,RejectionDb=20*Math.Log10(ultrasonic/voice)});
    float[] EchoImpulse(int level)
    {var effect=new VoiceEcho{Level=level};var signal=new float[48000*2];signal[0]=.2f;effect.Process(signal,48000);return signal;}
    var normalEcho=EchoImpulse(0);var largeEcho=EchoImpulse(3);var noEcho=EchoImpulse(-3);
    Check("正常迴聲保留即時原音並產生適量尾音",normalEcho[0]==.2f&&normalEcho.Skip(4800).Any(v=>Math.Abs(v)>.001));
    double firstRepeat=normalEcho.Skip(4800).Take(14400).Max(v=>Math.Abs(v)),lateTail=normalEcho.Skip(19200).Sum(v=>(double)v*v);
    double decay=Array.FindLastIndex(normalEcho,v=>Math.Abs(v)>.00002f)/48000d;
    Check("正常迴聲的反射強度與尾音達到設定門檻，原音不延後",normalEcho[0]==.2f&&firstRepeat>.0237974544&&lateTail>.000010664653547192858&&decay>1.3,new{FirstRepeat=firstRepeat,LateTailEnergy=lateTail,DecayToMinus80DbSeconds=decay,Scope="合成脈衝量測"});
    Check("增加迴聲會增強尾音，最小值恢復原音",largeEcho.Skip(4800).Sum(v=>(double)v*v)>normalEcho.Skip(4800).Sum(v=>(double)v*v)*3.9&&noEcho.Skip(1).All(v=>v==0));
    var echo=new VoiceEcho();var loud=new float[48000].Select(_=>3.4f).ToArray();echo.Process(loud,48000);
    Check("迴聲混音不超出播放上限且沒有非數值",loud.All(v=>float.IsFinite(v)&&Math.Abs(v)<=.950001));echo.Reset();var empty=new float[48000];echo.Process(empty,48000);
    Check("來源停止後清除迴聲，不帶入下一個來源",empty.All(v=>v==0));
    await using(var local=new LocalMicrophone())
    {
        int samples=0;local.Audio.Received+=(audio,_,_)=>Interlocked.Add(ref samples,audio.Length);
        await local.StartAsync("default",0);await Task.Delay(160);
        Check("實際 PC 收音送到共用 WASAPI 輸出，未啟用評分也可持續監聽",local.Running&&samples>1000&&local.Mixer.OutputActive&&local.Mixer.OutputPosition>0,new{Samples=samples,Rate=local.Audio.InputRate,OutputPosition=local.Mixer.OutputPosition,Volume=local.Audio.Volume,Scope="輸出音量 0 驗證裝置路徑，未量測實體喇叭延遲"});
        await local.StopAsync();Check("停止 PC 收音會釋放擷取、輸出與緩衝",!local.Running&&!local.Audio.Connected&&!local.Mixer.OutputActive&&local.Audio.BufferedSamples==0);
        bool rejected=false;try{await local.StartAsync("missing-openkaraoke-input",0);}catch{rejected=true;}
        Check("PC 裝置開啟失敗不殘留監聽輸出",rejected&&!local.Running&&!local.Mixer.OutputActive);
        await local.StartAsync("default",0);Check("PC 裝置失敗後可重新開啟正常輸入",local.Running);
    }
    for(int rate=44100;rate<=48000;rate+=3900)
    {
        var resampler=new AudioResampler();int samples=0;for(int at=0;at<rate*10;at+=441){int count=Math.Min(441,rate*10-at);samples+=resampler.Convert(new float[count],rate).Length;}
        Check($"電腦端 {rate}→16000 重取樣跨封包不漂移",Math.Abs(samples-160000)<=1,samples);
    }
    using(var mixer=new PhoneMixer())
    {
        var channels=Enumerable.Range(1,2).Select(i=>mixer.TryConnect("test-"+i)!).ToArray();Check("兩路分配固定編號，第三路被拒絕",channels.Select(c=>c.Slot).SequenceEqual(new[]{1,2})&&mixer.TryConnect("third")==null);
        Check("相同手機不能重複佔用一個位置",mixer.TryConnect("test-1")==null);
        foreach(var channel in channels){channel.Audio.Configure(false,100);channel.Audio.AcceptPcm(Enumerable.Repeat(new byte[]{255,127},200).SelectMany(b=>b).ToArray(),0);}
        float[]? monitored=null;int monitoredRate=0;
        mixer.MonitorSamples+=(samples,rate,_)=>{monitored=samples;monitoredRate=rate;};
        var output=new byte[480*2*4];mixer.Read(output,0,output.Length);var values=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(output).ToArray();
        Check("兩路混音不超出輸出上限且左右一致",values.All(v=>Math.Abs(v)<=.950001)&&Enumerable.Range(0,values.Length/2).All(i=>values[i*2]==values[i*2+1]));
        Check("演唱錄音取得與實際監聽相同的混音與迴聲，不增加音訊緩衝",monitored!=null&&monitoredRate==mixer.WaveFormat.SampleRate&&monitored.SequenceEqual(Enumerable.Range(0,values.Length/2).Select(i=>values[i*2])));
        mixer.SuppressMonitoring=true;mixer.Read(output,0,output.Length);
        Check("回放錄音時可暫停現場監聽，原始收音來源仍連線",System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(output).ToArray().All(v=>v==0)&&channels.All(c=>c.Audio.Connected));
        mixer.SuppressMonitoring=false;
        mixer.Disconnect(channels[1]);var rejoined=mixer.TryConnect("test-2");Check("同一手機重連維持編號且只讓該路靜音",rejoined?.Slot==2&&rejoined.Audio.Muted&&channels[0].Audio.Muted==false);
        foreach(var channel in channels)channel.Audio.Configure(true,45);mixer.StartOutput();await Task.Delay(250);Check("兩路共用的實際 WASAPI 輸出正常",mixer.OutputActive&&mixer.OutputPosition>0,new{Position=mixer.OutputPosition,RequestedMs=mixer.RequestedOutputMilliseconds,ActualDriverLatency="未量測"});
        Check("WASAPI 使用低延遲請求並保留相容退回",mixer.RequestedOutputMilliseconds is 10 or 20,mixer.RequestedOutputMilliseconds);
    }
    await using(var preparing=new PhoneHost(Path.Combine(folder,"preparing"),0,0,true))
    {
        preparing.Mixer.SuppressMonitoring=true;
        var draft=new VoiceSettings(DeviceId:"phone",PhoneEnabled:true,PhoneAddress:"127.0.0.1");
        await preparing.ApplyAsync(draft,startOutput:false);
        string pairing=preparing.MicrophoneUrl;
        Check("只準備連線可取得 QR 網址，不啟動音訊輸出",preparing.Running&&pairing.Length>0&&!preparing.Mixer.OutputActive);
        await preparing.ApplyAsync(draft);
        Check("同一連線開始測試後才啟動音訊輸出且保留配對",preparing.Mixer.OutputActive&&preparing.MicrophoneUrl==pairing&&preparing.Mixer.SuppressMonitoring);
    }
    await using var host=new PhoneHost(Path.Combine(folder,"host"),0,0,true,false);var settings=new VoiceSettings(DeviceId:"phone",PhoneEnabled:true,PhoneAddress:"127.0.0.1");Check("未啟動不開放網路服務",!host.Running);await host.ApplyAsync(settings);string token=new Uri(host.MicrophoneUrl).Fragment[6..];
    using var ca=X509CertificateLoader.LoadCertificate(host.PublicCertificate!);Check("只下載公鑰憑證且指紋一致",!ca.HasPrivateKey&&host.Fingerprint.Replace(":","")==Convert.ToHexString(SHA256.HashData(ca.RawData)));
    string moved=Path.Combine(folder,"moved-certificates");Directory.CreateDirectory(moved);File.WriteAllBytes(Path.Combine(moved,"local-ca.cer"),ca.RawData);
    using(var regenerated=new PhoneCertificates(moved,IPAddress.Loopback))Check("資料移轉後自動建立對應新使用者的憑證",regenerated.Fingerprint!=host.Fingerprint&&regenerated.Server.HasPrivateKey);
    bool Validate(object _,X509Certificate? certificate,X509Chain? chain,SslPolicyErrors errors)
    {if(certificate==null||(errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0)return false;using var leaf=X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());using var trust=new X509Chain();trust.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;trust.ChainPolicy.CustomTrustStore.Add(ca);trust.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;return trust.Build(leaf);}
    var handler=new HttpClientHandler{ServerCertificateCustomValidationCallback=(request,cert,chain,errors)=>Validate(request,cert,chain,errors)};using var client=new HttpClient(handler){BaseAddress=new Uri(host.SecureUrl)};
    Check("HTTPS 驗證實際信任鏈與位址",(await client.GetAsync("/")).IsSuccessStatusCode);Check("未配對禁止查詢狀態",(await client.GetAsync("/api/state")).StatusCode==HttpStatusCode.Unauthorized);
    async Task Pair(string value){using var request=new HttpRequestMessage(HttpMethod.Post,"/pair");request.Headers.Authorization=new("Bearer",value);var response=await client.SendAsync(request);Check("正確配對取得安全的裝置工作階段",response.StatusCode==HttpStatusCode.NoContent&&response.Headers.GetValues("Set-Cookie").Any(v=>v.Contains("httponly",StringComparison.OrdinalIgnoreCase)&&v.Contains("secure",StringComparison.OrdinalIgnoreCase)));}
    using(var bad=new HttpRequestMessage(HttpMethod.Post,"/pair")){bad.Headers.Authorization=new("Bearer","bad");Check("錯誤配對被拒絕",(await client.SendAsync(bad)).StatusCode==HttpStatusCode.Unauthorized);}await Pair(token);
    using(var boot=new HttpClient())
    {
        using var setupResponse=await boot.GetAsync(host.SetupUrl);
        string page=await setupResponse.Content.ReadAsStringAsync();
        Check("首次設定包含指紋但不暴露配對碼",page.Contains(host.Fingerprint)&&!page.Contains(token));
        string logo=await boot.GetStringAsync(host.SetupUrl+"logo.svg");
        Check("首次設定與收音頁使用同一份專案標誌",logo==File.ReadAllText(Path.Combine(root,"docs","assets","logo.svg"))&&await client.GetStringAsync("/logo.svg")==logo&&page.Contains("src=\"/logo.svg\""));
        var script=await boot.GetAsync(host.SetupUrl+"setup.js");
        Check("HTTP 允許分步引導程式且保留同源腳本限制",script.IsSuccessStatusCode&&script.Content.Headers.ContentType?.MediaType=="text/javascript"&&page.Contains("src=\"/setup.js\"")&&setupResponse.Headers.GetValues("Content-Security-Policy").Single().Contains("script-src 'self';"));
        Check("HTTP 不提供收音與狀態 API",(await boot.GetAsync(host.SetupUrl+"api/state")).StatusCode==HttpStatusCode.NotFound&&(await boot.GetAsync(host.SetupUrl+"app.js")).StatusCode==HttpStatusCode.NotFound&&(await boot.GetAsync(host.SetupUrl+"audio-worklet.js")).StatusCode==HttpStatusCode.NotFound);
    }
    async Task<ClientWebSocket> Connect(string id,string origin)
    {
        var ws=new ClientWebSocket();ws.Options.RemoteCertificateValidationCallback=Validate;ws.Options.SetRequestHeader("Origin",origin);ws.Options.SetRequestHeader("Authorization","Bearer "+token);ws.Options.Cookies=new();ws.Options.Cookies.Add(new Uri(host.SecureUrl),new Cookie("openkaraoke-device",id));
        try{await ws.ConnectAsync(new Uri(host.SecureUrl.Replace("https:","wss:")+"/ws"),CancellationToken.None);byte[] hello=new byte[1024];await ws.ReceiveAsync(hello,CancellationToken.None);return ws;}catch{ws.Dispose();throw;}
    }
    bool originRejected=false;try{using var wrong=await Connect(Guid.NewGuid().ToString("N"),"https://example.com");}catch(WebSocketException){originRejected=true;}Check("拒絕不同來源的收音連線",originRejected&&host.Mixer.ConnectedCount==0);
    using(var timed=await Connect(Guid.NewGuid().ToString("N"),host.SecureUrl))
    {
        var own=host.Mixer.Channels.Single(c=>c.Audio.Connected);int received=0;own.Audio.Received+=(_,_,_)=>Interlocked.Increment(ref received);
        await timed.SendAsync(Encoding.UTF8.GetBytes("{\"sampleRate\":48000,\"protocol\":2}"),WebSocketMessageType.Text,true,CancellationToken.None);
        await timed.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"id\":12}"),WebSocketMessageType.Text,true,CancellationToken.None);var response=new byte[2048];var pong=await timed.ReceiveAsync(response,CancellationToken.None);
        using(var value=JsonDocument.Parse(response.AsMemory(0,pong.Count)))Check("加密連線提供往返同步，不回傳已移除的診斷資訊",value.RootElement.GetProperty("id").GetInt32()==12&&value.RootElement.GetProperty("serverTime").GetDouble()>0&&!value.RootElement.TryGetProperty("bufferMs",out _));
        await timed.SendAsync(Packet(1,MicrophoneCapture.Now*1000-250,480),WebSocketMessageType.Binary,true,CancellationToken.None);await Wait(()=>own.Audio.StalePackets==1);
        Check("實際 WSS 接收會淘汰過期音訊",received==0&&own.Audio.BufferedSamples==0);
        await timed.SendAsync(Packet(2,MicrophoneCapture.Now*1000,960),WebSocketMessageType.Binary,true,CancellationToken.None);await Wait(()=>received==1);
        Check("過期後新封包仍可正常收音",own.Audio.Level>.01&&own.Audio.HasCaptureTimestamps);
        await timed.CloseAsync(WebSocketCloseStatus.NormalClosure,"test",CancellationToken.None);await Wait(()=>host.Mixer.ConnectedCount==0);
    }
    using(var audio=new PhoneAudio())
    {
        audio.Connect();audio.Configure(false,100);
        await using var capture=new PhoneCapture(audio);int failures=0,receivedFrames=0;
        capture.Failed+=_=>failures++;capture.Frame+=(_,_)=>receivedFrames++;
        await capture.StartAsync("phone",Path.Combine(root,"tools","scoring","swift-f0-0.3.0.onnx"));
        audio.AcceptPcm(Tone(440,0,800,16000),MicrophoneCapture.Now);audio.Disconnect();await capture.DisposeAsync();
        Check("正常斷線仍處理尾端音訊，且不被當成分析故障",capture.Error==null&&failures==0&&receivedFrames>0);
    }
    using(var audio=new PhoneAudio())
    {
        audio.Connect();await using var capture=new PhoneCapture(audio);
        capture.Frame+=(_,_)=>throw new IOException("驗收尾端分析故障");
        await capture.StartAsync("phone",Path.Combine(root,"tools","scoring","swift-f0-0.3.0.onnx"));
        audio.AcceptPcm(Tone(440,0,800,16000),MicrophoneCapture.Now);await capture.DisposeAsync();
        Check("停止時尾端分析發生錯誤仍保留失敗狀態",capture.Error is IOException);
    }
    using(var audio=new PhoneAudio())
    {
        audio.Connect();audio.SetFormat(48000);var reader=new PhonePacketReader(48000);var times=new System.Collections.Concurrent.ConcurrentBag<double>();await using var capture=new PhoneCapture(audio);
        capture.Frame+=(_,time)=>times.Add(time);await capture.StartAsync("phone",Path.Combine(root,"tools","scoring","swift-f0-0.3.0.onnx"));
        for(int i=0;i<40;i++){double end=100+(i+1+(i>=20?4:0))*.01;reader.Accept(Packet((uint)i+1,end*1000,(i+1+(i>=20?4:0))*480),end+.001,audio);await Task.Delay(10);}
        await capture.DisposeAsync();Check("音訊缺口前的尾端仍分析，缺口後評分不被提前",capture.Error==null&&times.Count==24&&times.Any(t=>t>=100.4)&&!times.Any(t=>t>100.201&&t<100.239),new{Frames=times.Count,ExpectedFrames=24,First=times.Min(),Last=times.Max()});
    }
    using(var page=await client.GetAsync("/"))
    {
        string html=await page.Content.ReadAsStringAsync();
        Check("收音頁只保留麥克風與常亮提示，沒有增益或診斷介面",html.Contains("screen-status")&&!html.Contains("id=\"enhance\"")&&!html.Contains("id=\"automatic\"")&&!html.Contains("diagnostics"));
        Check("HTTPS 權限政策允許本頁申請螢幕常亮",page.Headers.TryGetValues("Permissions-Policy",out var policy)&&policy.Any(value=>value.Contains("screen-wake-lock=(self)")));
    }
    var ids=Enumerable.Range(0,2).Select(_=>Guid.NewGuid().ToString("N")).ToArray();var sockets=new List<ClientWebSocket>();var captures=new List<PhoneCapture>();
    try
    {
        for(int i=0;i<2;i++)sockets.Add(await Connect(ids[i],host.SecureUrl));Check("兩支可以同時連線",host.Mixer.ConnectedCount==2);
        int[] rates=[48000,44100];for(int i=0;i<2;i++)await sockets[i].SendAsync(Encoding.UTF8.GetBytes("{\"sampleRate\":"+rates[i]+"}"),WebSocketMessageType.Text,true,CancellationToken.None);
        bool thirdRejected=false;try{using var third=await Connect(Guid.NewGuid().ToString("N"),host.SecureUrl);}catch(WebSocketException){thirdRejected=true;}Check("第三支被拒絕且兩路不受影響",thirdRejected&&host.Mixer.ConnectedCount==2);
        var latency=new List<double>();TaskCompletionSource? packetSeen=null;host.Mixer.Channels[0].Audio.Received+=(_,_,_)=>packetSeen?.TrySetResult();
        for(int i=0;i<50;i++){packetSeen=new(TaskCreationOptions.RunContinuationsAsynchronously);var timer=System.Diagnostics.Stopwatch.StartNew();await sockets[0].SendAsync(new byte[960],WebSocketMessageType.Binary,true,CancellationToken.None);await packetSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));latency.Add(timer.Elapsed.TotalMilliseconds);await Task.Delay(10);}packetSeen=null;
        var sorted=latency.Order().ToArray();Check("加密串流的本機傳送到接收耗時已量測",sorted[^1]<100,new{MedianMs=sorted[25],P95Ms=sorted[47],Scope="Windows loopback，非實體手機端到端"});
        string model=Path.Combine(root,"tools","scoring","swift-f0-0.3.0.onnx");var frames=Enumerable.Range(0,2).Select(_=>new System.Collections.Concurrent.ConcurrentBag<PitchFrame>()).ToArray();double[] frequencies=[440,466.1637615];
        var captureErrors=new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        for(int i=0;i<2;i++){int index=i;var capture=new PhoneCapture(host.Mixer.Channels[i].Audio);capture.Frame+=(frame,_)=>frames[index].Add(frame);capture.Failed+=e=>captureErrors.Enqueue(e);await capture.StartAsync("phone",model);captures.Add(capture);}
        host.SetMonitorVolume(0);
        Check("共用手機監聽音量同步歸零且兩支不斷線",host.Mixer.ConnectedCount==2&&host.Mixer.Channels.All(c=>c.Audio.Volume==0));
        var sending=Enumerable.Range(0,2).Select(async i=>{int rate=rates[i],size=rate/100;for(int packet=0;packet<100;packet++){await sockets[i].SendAsync(Tone(frequencies[i],packet*size,size,rate),WebSocketMessageType.Binary,true,CancellationToken.None);await Task.Delay(10);}});await Task.WhenAll(sending);if(!captureErrors.IsEmpty)throw new AggregateException(captureErrors);await Wait(()=>frames.All(f=>f.Count>25));
        Check("兩路取樣率分別處理，音高資料沒有混在一起",frames[0].Where(f=>f.Voiced).All(f=>Math.Abs(f.Midi-69)<.4)&&frames[1].Where(f=>f.Voiced).All(f=>Math.Abs(f.Midi-70)<.4));
        Check("共用監聽音量為零仍保留兩支原始收音供評分",frames.All(f=>f.Count(frame=>frame.Voiced)>20)&&host.Mixer.Channels.All(c=>c.Audio.Level>.01));
        Check("手機開啟後直接收音",host.Mixer.Channels.All(c=>!c.Audio.Muted)&&frames.All(f=>f.Count>25));
        host.SetMonitorVolume(25);await host.ApplyAsync(host.Configuration);
        Check("重新套用服務設定保留共用音量與兩支連線",host.Mixer.ConnectedCount==2&&host.Mixer.Channels.All(c=>c.Audio.Volume==25));
        await sockets[1].CloseAsync(WebSocketCloseStatus.NormalClosure,"test",CancellationToken.None);await Wait(()=>host.Mixer.ConnectedCount==1);await Task.Delay(40);Check("其中一支停止不影響另一支",host.Mixer.Channels[0].Audio.Connected&&!host.Mixer.Channels[1].Audio.Connected);
        using(var rejoined=await Connect(ids[1],host.SecureUrl)){Check("相同裝置重連回原位置",host.Mixer.Channels.Single(c=>c.Id==ids[1]).Slot==2&&host.Mixer.ConnectedCount==2);Check("重連自動使用目前共用音量",host.Mixer.Channels.All(c=>c.Audio.Volume==25));await rejoined.CloseAsync(WebSocketCloseStatus.NormalClosure,"test",CancellationToken.None);}
    }
    finally{foreach(var capture in captures)await capture.DisposeAsync();foreach(var socket in sockets){socket.Abort();socket.Dispose();}}
    await Wait(()=>host.Mixer.ConnectedCount==0);string fingerprint=host.Fingerprint;await host.ResetPairingAsync();Check("重新配對使舊工作階段失效",(await client.GetAsync("/api/state")).StatusCode==HttpStatusCode.Unauthorized);
    await host.ApplyAsync(settings with{PhoneEnabled=false});Check("停用釋放全部連線與輸出",!host.Running&&host.Mixer.ConnectedCount==0&&!host.Mixer.OutputActive);await host.ApplyAsync(settings);Check("重新啟用保留原憑證且更換配對碼",host.Fingerprint==fingerprint&&new Uri(host.MicrophoneUrl).Fragment[6..]!=token);
    await using(var previewHost=new PhoneHost(Path.Combine(folder,"preview-pairing"),0,0,true,false))
    {
        var previewSettings=new VoiceSettings(DeviceId:"phone",PhoneEnabled:true,PhoneAddress:"127.0.0.1");
        await previewHost.ApplyAsync(previewSettings);string originalPair=new Uri(previewHost.MicrophoneUrl).Fragment;
        try{await previewHost.ApplyAsync(previewSettings with{PhoneAddress="203.0.113.1"},preservePairing:true);}catch(IOException){}
        await previewHost.ApplyAsync(previewSettings,preservePairing:true);
        Check("測試網路失敗後可還原原本配對，不必重新掃碼",new Uri(previewHost.MicrophoneUrl).Fragment==originalPair);
        await previewHost.ApplyAsync(previewSettings with{PhoneEnabled=false});await previewHost.ApplyAsync(previewSettings);
        Check("正式停用後重新啟用仍會使舊配對失效",new Uri(previewHost.MicrophoneUrl).Fragment!=originalPair);
    }
    await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(folder);
}
catch(Exception error){await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));Console.Error.WriteLine(error);Environment.ExitCode=1;}
