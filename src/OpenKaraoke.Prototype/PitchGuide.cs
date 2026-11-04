using System.Windows;
using System.Windows.Media;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public sealed class PitchGuide : FrameworkElement
{
    private static readonly Brush ReferenceBrush = new SolidColorBrush(Color.FromRgb(143, 153, 167));
    private static readonly Brush CorrectBrush = new SolidColorBrush(Color.FromRgb(100, 221, 142));
    private static readonly Brush IncorrectBrush = new SolidColorBrush(Color.FromRgb(240, 120, 131));
    private ReferenceTrack? _reference;
    private double _low, _high;
    public ReferenceTrack? Reference
    {
        get => _reference;
        set
        {
            if (ReferenceEquals(_reference, value)) return;
            _reference = value;
            _low = value == null ? 48 : value.Notes.Min(n => n.Midi) - 6;
            _high = value == null ? 72 : value.Notes.Max(n => n.Midi) + 6;
            InvalidateVisual();
        }
    }
    public SungNote[] Records { get; set; } = [];
    public MelodyNote[] OriginalNotes { get; set; } = [];
    public bool Overview { get; set; }
    public bool FollowPlayback { get; set; }
    public double Duration { get; set; }
    public double Position { get; set; }
    public int Key { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if ((!Overview && Reference == null) || width <= 0 || height <= 0) return;
        var background = (Brush)FindResource("PanelBrush");
        dc.DrawRoundedRectangle(background, null, new Rect(0, 0, width, height), 8, 8);
        bool follow=Overview&&FollowPlayback;
        double span=follow?Math.Max(.1,width/80):Overview?Math.Max(.1,Duration):10;
        double from=follow?Math.Clamp(Position-span*.3,0,Math.Max(0,Duration-span)):Overview?0:Position-3,to=from+span;
        double plotHeight=follow?Math.Max(18,height-25):height;
        var notes = Reference?.Notes.Where(n => n.End >= from && n.Start <= to).ToArray() ?? [];
        var pitches=OriginalNotes.Select(n=>n.Midi).Concat(Records.Select(n=>n.Midi)).ToArray();
        double low = Overview ? (pitches.Length > 0 ? pitches.Min() - 6 : 48) : _low + Key;
        double high = Overview ? (pitches.Length > 0 ? pitches.Max() + 6 : 72) : _high + Key;
        double X(double time) => (time - from) / span * width;
        double Y(double midi) => plotHeight - 9 - (midi - low) / (high - low) * (plotHeight - 18);
        void Bar(double start, double end, double midi, Brush brush)
        {
            double length = Math.Max(Overview ? .8 : 2, X(end) - X(start) - (Overview ? 0 : 1));
            dc.DrawRoundedRectangle(brush, null, new Rect(X(start), Y(midi) - 3, length, 6), 3, 3);
        }
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), 8, 8));
        if (Overview)
        {
            if(follow)
            {
                var line=new Pen((Brush)FindResource("Line"),1);
                for(int row=1;row<=5;row++){double y=9+(plotHeight-18)*row/6;dc.DrawLine(line,new(0,y),new(width,y));}
            }
            foreach (var note in OriginalNotes.Where(n=>n.End>=from&&n.Start<=to)) Bar(note.Start, note.End, note.Midi, ReferenceBrush);
            foreach (var note in Records.Where(n=>n.End>=from&&n.Start<=to)) Bar(note.Start, note.End, note.Midi, note.Correct ? CorrectBrush : IncorrectBrush);
            if(follow)
            {
                double playhead=X(Position);dc.DrawLine(new Pen((Brush)FindResource("Accent"),1),new(playhead,0),new(playhead,plotHeight));
                for(double second=Math.Ceiling(from);second<=to;second++)
                {
                    double x=X(second);dc.DrawLine(new Pen((Brush)FindResource("Line"),1),new(x,plotHeight),new(x,plotHeight+4));
                    var time=TimeSpan.FromSeconds(second);
                    var label=new FormattedText(time.ToString(second>=3600?@"h\:mm\:ss":@"m\:ss"),System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),10,(Brush)FindResource("Sub"),VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(label,new(x+3,plotHeight+7));
                }
            }
            dc.Pop(); return;
        }
        var grid=new Pen((Brush)FindResource("Line"),1);
        for(int row=1;row<=5;row++)
        {
            double y=9+(height-18)*row/6;
            dc.DrawLine(grid,new(0,y),new(width,y));
        }
        foreach (var note in notes)
        {
            double target = note.Midi + Key;
            Bar(note.Start, note.End, target, ReferenceBrush);
        }
        foreach(var note in Records.Where(n=>n.End<=Position&&n.End>=from&&n.Start<=to))
            Bar(note.Start,note.End,note.Midi+Key,note.Correct?CorrectBrush:IncorrectBrush);
        double now = X(Position);
        dc.DrawLine(new Pen((Brush)FindResource("Sub"), 1), new(now, 5), new(now, height - 5));
        dc.Pop();
    }
}
