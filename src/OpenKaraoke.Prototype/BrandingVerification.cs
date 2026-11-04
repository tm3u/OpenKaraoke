using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private static bool SquareWindowPreference(Window window)
    {
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))return true;
        return DwmGetWindowAttribute(new WindowInteropHelper(window).Handle,33,out int preference,sizeof(int))==0&&preference==1;
    }
    private bool VerifyApplicationLogo(string folder)
    {
        var decoder=new IconBitmapDecoder(new Uri("pack://application:,,,/Assets/openkaraoke.ico"),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        bool Matches(BitmapSource image)
        {
            var reference=decoder.Frames.FirstOrDefault(f=>f.PixelWidth==image.PixelWidth&&f.PixelHeight==image.PixelHeight);
            if(reference==null)return false;
            byte[] Pixels(BitmapSource frame)
            {
                var converted=new FormatConvertedBitmap(frame,PixelFormats.Pbgra32,null,0);
                var bytes=new byte[converted.PixelWidth*converted.PixelHeight*4];converted.CopyPixels(bytes,converted.PixelWidth*4,0);return bytes;
            }
            var actual=Pixels(image);var expected=Pixels(reference);
            return actual.Zip(expected).All(p=>Math.Abs(p.First-p.Second)<=2);
        }
        bool valid=Icon!=null&&decoder.Frames.Count==9;
        nint window=new WindowInteropHelper(this).Handle;
        for(int kind=0;kind<=1;kind++)
        {
            nint icon=SendSizingMessage(window,0x007F,kind,0);
            if(icon==0){valid=false;continue;}
            var bitmap=Imaging.CreateBitmapSourceFromHIcon(icon,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            valid&=Matches(bitmap);Save(bitmap,Path.Combine(folder,kind==0?"app-icon-small.png":"app-icon-large.png"));
        }
        nint large=0,small=0;
        try
        {
            string executable=Environment.ProcessPath??Path.ChangeExtension(typeof(MainWindow).Assembly.Location,".exe");
            if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
                executable=Path.ChangeExtension(typeof(MainWindow).Assembly.Location,".exe");
            valid&=ExtractIconEx(executable,0,out large,out small,1)>0&&large!=0&&small!=0;
            if(large!=0)valid&=Matches(Imaging.CreateBitmapSourceFromHIcon(large,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()));
            if(small!=0)valid&=Matches(Imaging.CreateBitmapSourceFromHIcon(small,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions()));
        }
        finally {if(large!=0)DestroyIcon(large);if(small!=0)DestroyIcon(small);}
        return valid;
    }
    private static void Save(BitmapSource image,string path)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(path);encoder.Save(output);
    }
    [DllImport("shell32.dll",EntryPoint="ExtractIconExW",CharSet=CharSet.Unicode)]private static extern uint ExtractIconEx(string path,int index,out nint large,out nint small,uint count);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(nint icon);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint window,int attribute,out int value,int size);
}
