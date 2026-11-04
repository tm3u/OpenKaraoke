using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace OpenKaraoke.Prototype;

public sealed class Icon : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata("music", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.7, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    private static readonly Dictionary<string, Geometry> Cache = new();
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["list"]="list-music",["next"]="skip-forward",["repeat"]="rotate-ccw",["close"]="x",["trash"]="trash-2",
        ["up"]="arrow-up",["down"]="arrow-down",["expand"]="maximize",["collapse"]="minimize",
        ["volume"]="volume-2",["mute"]="volume-x"
    };
    static Icon()
    {
        WidthProperty.OverrideMetadata(typeof(Icon),new FrameworkPropertyMetadata(18d));
        HeightProperty.OverrideMetadata(typeof(Icon),new FrameworkPropertyMetadata(18d));
        HorizontalAlignmentProperty.OverrideMetadata(typeof(Icon),new FrameworkPropertyMetadata(HorizontalAlignment.Center));
        VerticalAlignmentProperty.OverrideMetadata(typeof(Icon),new FrameworkPropertyMetadata(VerticalAlignment.Center));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Icon),new FrameworkPropertyMetadata(false));
        FocusableProperty.OverrideMetadata(typeof(Icon),new FrameworkPropertyMetadata(false));
    }
    internal static Geometry ReadGeometry(string kind)
    {
        string name = Aliases.GetValueOrDefault(kind, kind);
        if (Cache.TryGetValue(name, out var cached)) return cached;
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/OpenKaraoke;component/Assets/Icons/{name}.svg"))
            ?? throw new InvalidDataException("缺少圖示：" + name);
        using var reader = XmlReader.Create(resource.Stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true });
        var svg = XElement.Load(reader); var geometry = new GeometryGroup();
        foreach (var node in svg.Elements())
        {
            double Number(string attribute, double fallback = 0) => node.Attribute(attribute) is {} value ? double.Parse(value.Value, CultureInfo.InvariantCulture) : fallback;
            Geometry shape = node.Name.LocalName switch
            {
                "path" => Geometry.Parse((string?)node.Attribute("d") ?? ""),
                "circle" => new EllipseGeometry(new Point(Number("cx"), Number("cy")), Number("r"), Number("r")),
                "ellipse" => new EllipseGeometry(new Point(Number("cx"), Number("cy")), Number("rx"), Number("ry")),
                "rect" => new RectangleGeometry(new Rect(Number("x"), Number("y"), Number("width"), Number("height")), Number("rx"), Number("ry", Number("rx"))),
                "line" => new LineGeometry(new Point(Number("x1"), Number("y1")), new Point(Number("x2"), Number("y2"))),
                "polyline" or "polygon" => Geometry.Parse("M" + (string?)node.Attribute("points") + (node.Name.LocalName == "polygon" ? "Z" : "")),
                _ => throw new InvalidDataException("不支援的圖示元素：" + node.Name.LocalName)
            };
            geometry.Children.Add(shape);
        }
        geometry.Freeze(); Cache[name] = geometry; return geometry;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (string.IsNullOrEmpty(Kind)) return;
        double scale=Math.Min(ActualWidth,ActualHeight)/24;
        dc.PushTransform(new MatrixTransform(scale,0,0,scale,(ActualWidth-24*scale)/2,(ActualHeight-24*scale)/2));
        dc.DrawGeometry(null, new Pen(Foreground, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, ReadGeometry(Kind));
        dc.Pop();
    }
}

public sealed class VideoFrame : Decorator
{
    public bool Fullscreen { get; set; }
    protected override Size MeasureOverride(Size constraint)
    {
        double width = double.IsFinite(constraint.Width) ? constraint.Width : 600;
        double height = Fullscreen && double.IsFinite(constraint.Height) ? constraint.Height : width * 9 / 16;
        if (double.IsFinite(constraint.Height)) height = Math.Min(height, constraint.Height);
        Child?.Measure(new Size(width, height)); return new Size(width, height);
    }
    protected override Size ArrangeOverride(Size size) { Child?.Arrange(new Rect(size)); return size; }
}
