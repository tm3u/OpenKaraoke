using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace OpenKaraoke.Prototype;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        int index=Array.IndexOf(e.Args,"--data-dir");
        string folder=index>=0&&index+1<e.Args.Length?Path.GetFullPath(e.Args[index+1]):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenKaraoke");
        string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())))[..24];
        _instance=new Mutex(true,"Local\\OpenKaraoke-"+key,out bool created);
        if(!created){ MessageBox.Show("OpenKaraoke 已在執行中。","OpenKaraoke");Shutdown();return; }
        DispatcherUnhandledException+=(_,args)=>
        {
            try { Directory.CreateDirectory(folder);File.AppendAllText(Path.Combine(folder,"error.log"),DateTimeOffset.Now+" "+args.Exception+Environment.NewLine); }catch(IOException){ }
            MessageBox.Show("程式發生錯誤："+args.Exception.Message,"OpenKaraoke"); args.Handled=true;Shutdown(1);
        };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose();base.OnExit(e); }
}
