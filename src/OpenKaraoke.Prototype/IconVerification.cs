using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyIconAssetsAsync(string folder)
    {
        var manifest=Application.GetResourceStream(new Uri("pack://application:,,,/OpenKaraoke;component/Assets/Icons/manifest.json"))!;
        using var document=await JsonDocument.ParseAsync(manifest.Stream);manifest.Stream.Dispose();
        if(document.RootElement.GetProperty("version").GetString()!="1.17.0")throw new InvalidDataException("圖示版本與指定素材版本不一致。");
        var nonsquare=new Icon{Kind="plus",Width=30,Height=18,Foreground=Brush("Accent")};
        nonsquare.Measure(new Size(30,18));nonsquare.Arrange(new Rect(0,0,30,18));nonsquare.UpdateLayout();
        var bitmap=new RenderTargetBitmap(30,18,96,96,PixelFormats.Pbgra32);bitmap.Render(nonsquare);
        var pixels=new byte[30*18*4];bitmap.CopyPixels(pixels,30*4,0);int left=30,right=-1,top=18,bottom=-1;
        for(int y=0;y<18;y++)for(int x=0;x<30;x++)if(pixels[(y*30+x)*4+3]>0){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
        if(right<left||Math.Abs((right-left)-(bottom-top))>1||Math.Abs((left+right+1)/2d-15)>.5||Math.Abs((top+bottom+1)/2d-9)>.5)throw new InvalidDataException("非正方形圖示區沒有等比例置中。");
        var grid=new UniformGrid{Columns=6,Margin=new(12)};var names=new List<string>();
        foreach(var entry in document.RootElement.GetProperty("icons").EnumerateArray())
        {
            string name=entry.GetProperty("name").GetString()!;names.Add(name);
            var resource=Application.GetResourceStream(new Uri($"pack://application:,,,/OpenKaraoke;component/Assets/Icons/{name}.svg"))!;
            using(var stream=resource.Stream)
                if(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream))!=entry.GetProperty("sha256").GetString())throw new InvalidDataException("圖示素材內容不同："+name);
            if(OpenKaraoke.Prototype.Icon.ReadGeometry(name).Bounds.IsEmpty)throw new InvalidDataException("無法繪製圖示："+name);
            var cell=new StackPanel{Margin=new(4,10,4,10)};
            cell.Children.Add(new Icon{Kind=name,Width=24,Height=24,StrokeThickness=2,HorizontalAlignment=HorizontalAlignment.Center,Foreground=Brush("Text")});
            cell.Children.Add(new TextBlock{Text=name,FontSize=11,Foreground=Brush("Sub"),HorizontalAlignment=HorizontalAlignment.Center,Margin=new(0,7,0,0)});grid.Children.Add(cell);
        }
        var window=new Window{Owner=this,Width=700,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false,Background=Brush("PanelBrush"),Content=grid};
        window.Show();await Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);
        CaptureDialog(window,Path.Combine(folder,"lucide-icons.png"));window.Close();
        await File.WriteAllTextAsync(Path.Combine(folder,"icons-report.json"),JsonSerializer.Serialize(new{Passed=true,Version="1.17.0",Source=document.RootElement.GetProperty("source").GetString(),Count=names.Count,Icons=names},new JsonSerializerOptions{WriteIndented=true}));
    }
}
