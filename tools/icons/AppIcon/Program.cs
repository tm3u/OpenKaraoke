using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string root=Path.GetFullPath(args[0]);
        var source=XElement.Load(Path.Combine(root,"src/OpenKaraoke.Prototype/Assets/Icons/audio-lines.svg"));
        var geometry=new GeometryGroup();
        foreach(var path in source.Elements())
        {
            if(path.Name.LocalName!="path")throw new InvalidDataException("LOGO 只接受原始路徑。");
            geometry.Children.Add(Geometry.Parse((string?)path.Attribute("d")??""));
        }
        var pen=new Pen(new SolidColorBrush(Color.FromRgb(0x64,0xDD,0xC4)),double.Parse(source.Attribute("stroke-width")!.Value,CultureInfo.InvariantCulture))
        {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
        int[] sizes=[16,20,24,32,40,48,64,128,256];var frames=new List<byte[]>();
        foreach(int size in sizes)
        {
            var drawing=new DrawingVisual();
            using(var dc=drawing.RenderOpen()){dc.PushTransform(new ScaleTransform(size/24d,size/24d));dc.DrawGeometry(null,pen,geometry);dc.Pop();}
            var bitmap=new RenderTargetBitmap(size,size,96,96,PixelFormats.Pbgra32);bitmap.Render(drawing);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var png=new MemoryStream();encoder.Save(png);frames.Add(png.ToArray());
        }
        string destination=Path.Combine(root,"src/OpenKaraoke.Prototype/Assets/openkaraoke.ico");
        using var output=new BinaryWriter(File.Create(destination));output.Write((ushort)0);output.Write((ushort)1);output.Write((ushort)sizes.Length);
        int offset=6+16*sizes.Length;
        for(int i=0;i<sizes.Length;i++)
        {
            output.Write((byte)(sizes[i]==256?0:sizes[i]));output.Write((byte)(sizes[i]==256?0:sizes[i]));output.Write((byte)0);output.Write((byte)0);
            output.Write((ushort)1);output.Write((ushort)32);output.Write(frames[i].Length);output.Write(offset);offset+=frames[i].Length;
        }
        foreach(var frame in frames)output.Write(frame);
        Directory.CreateDirectory(Path.Combine(root,"artifacts"));File.WriteAllBytes(Path.Combine(root,"artifacts/openkaraoke-app-icon.png"),frames[^1]);
        Console.WriteLine(destination);
    }
}
