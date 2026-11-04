using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenKaraoke.Prototype;

public partial class IdleLandscape : UserControl
{
    private const double CycleSeconds=90;
    private readonly Storyboard _colors=new(){RepeatBehavior=RepeatBehavior.Forever},_movement=new(){Duration=TimeSpan.FromSeconds(CycleSeconds),RepeatBehavior=RepeatBehavior.Forever};
    private Window? _window;
    private bool _started,_preview;
    private readonly List<MatrixTransform> _noteMotion=[];
    private TimeSpan _resumePosition;
    internal bool IsAnimating {get;private set;}
    internal double CyclePositionSeconds=>_started?_movement.GetCurrentTime(this)?.TotalSeconds??0:_resumePosition.TotalSeconds;
    internal bool HasAnimationClock=>_started;
    internal int MotionChannelCount=>_movement.Children.Count;
    internal int? DesiredMotionFrameRate=>Timeline.GetDesiredFrameRate(_movement);
    public IdleLandscape()
    {
        InitializeComponent();
        AddOpacity("DaySky",[(0,1),(.18,1),(.3,0),(.7,0),(.82,1),(1,1)]);
        AddOpacity("DuskSky",[(0,0),(.17,0),(.25,1),(.33,0),(.67,0),(.75,1),(.83,0),(1,0)]);
        AddOpacity("Stars",[(0,0),(.28,0),(.38,1),(.62,1),(.72,0),(1,0)]);
        AddOpacity("Sun",[(0,1),(.23,1),(.27,0),(.73,0),(.77,1),(1,1)]);
        var orbit=new DoubleAnimation(0,360,TimeSpan.FromSeconds(CycleSeconds));
        Storyboard.SetTargetName(orbit,"Orbit");Storyboard.SetTargetProperty(orbit,new PropertyPath(RotateTransform.AngleProperty));_movement.Children.Add(orbit);
        AddNotes(BackNotes,14,new(360,164),326,128,-8,1,.08,16,.42);
        AddNotes(BackNotes,8,new(360,146),230,83,12,-2,.23,14,.36);
        AddNotes(FrontNotes,10,new(360,237),294,82,-12,3,.04,22,.58);
        Timeline.SetDesiredFrameRate(_colors,60);Timeline.SetDesiredFrameRate(_movement,60);
        Loaded+=(_,_)=>{_window=Window.GetWindow(this);if(_window!=null)_window.StateChanged+=WindowChanged;SystemParameters.StaticPropertyChanged+=SystemChanged;RefreshMotion();};
        Unloaded+=(_,_)=>{if(_window!=null)_window.StateChanged-=WindowChanged;SystemParameters.StaticPropertyChanged-=SystemChanged;Suspend();};
        IsVisibleChanged+=(_,_)=>RefreshMotion();
        SizeChanged+=(_,_)=>Clip=new RectangleGeometry(new Rect(0,0,ActualWidth,ActualHeight),11,11);
    }
    private void AddNotes(Canvas layer,int count,Point center,double rx,double ry,double tilt,int turns,double offset,double size,double opacity)
    {
        Brush[] colors=[new SolidColorBrush(Color.FromRgb(182,225,215)),new SolidColorBrush(Color.FromRgb(245,223,171)),new SolidColorBrush(Color.FromRgb(206,224,221))];
        foreach(var brush in colors)brush.Freeze();
        double rotation=tilt*Math.PI/180;
        Point PointAt(double angle)
        {
            double x=rx*Math.Cos(angle),y=ry*Math.Sin(angle);
            return new(center.X+x*Math.Cos(rotation)-y*Math.Sin(rotation),center.Y+x*Math.Sin(rotation)+y*Math.Cos(rotation));
        }
        for(int i=0;i<count;i++)
        {
            double angle=2*Math.PI*(i/(double)count+offset),width=size+(i%3)*3;
            var start=PointAt(angle);var translate=new MatrixTransform(new Matrix(1,0,0,1,start.X,start.Y));
            string target="NoteMotion"+_noteMotion.Count;RegisterName(target,translate);_noteMotion.Add(translate);
            var transform=new TransformGroup();transform.Children.Add(new RotateTransform((i%5-2)*9,width/2,width/2));transform.Children.Add(translate);
            var note=new Icon{Kind="music",Width=width,Height=width,StrokeThickness=1.6,Foreground=colors[i%colors.Length],Opacity=opacity+(i%3)*.05,RenderTransform=transform};
            Canvas.SetLeft(note,-width/2);Canvas.SetTop(note,-width/2);layer.Children.Add(note);
            var direction=turns>0?SweepDirection.Clockwise:SweepDirection.Counterclockwise;
            var figure=new PathFigure{StartPoint=PointAt(angle),IsClosed=true};
            figure.Segments.Add(new ArcSegment(PointAt(angle+Math.PI),new Size(rx,ry),tilt,false,direction,true));
            figure.Segments.Add(new ArcSegment(figure.StartPoint,new Size(rx,ry),tilt,false,direction,true));
            var path=new PathGeometry([figure]);path.Freeze();
            var animation=new MatrixAnimationUsingPath{PathGeometry=path,DoesRotateWithTangent=false,Duration=TimeSpan.FromSeconds(CycleSeconds/Math.Abs(turns)),RepeatBehavior=RepeatBehavior.Forever};
            Storyboard.SetTargetName(animation,target);Storyboard.SetTargetProperty(animation,new PropertyPath(MatrixTransform.MatrixProperty));_movement.Children.Add(animation);
        }
    }
    private void AddOpacity(string target,(double Time,double Value)[] keys)
    {
        var animation=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromSeconds(CycleSeconds)};
        foreach(var key in keys)animation.KeyFrames.Add(new LinearDoubleKeyFrame(key.Value,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(key.Time*CycleSeconds))));
        Storyboard.SetTargetName(animation,target);Storyboard.SetTargetProperty(animation,new PropertyPath(OpacityProperty));_colors.Children.Add(animation);
    }
    private void WindowChanged(object? sender,EventArgs e)=>RefreshMotion();
    private void SystemChanged(object? sender,PropertyChangedEventArgs e){if(e.PropertyName==nameof(SystemParameters.ClientAreaAnimation))RefreshMotion();}
    internal void RefreshMotion()
    {
        if(_preview)return;
        if(!UiMotion.GetAllowAnimation(this)){Suspend();return;}
        if(!IsLoaded||!IsVisible||_window?.WindowState==WindowState.Minimized)
        {
            Suspend();return;
        }
        if(!_started)
        {
            _colors.Begin(this,true);_movement.Begin(this,true);_started=true;
            _colors.Seek(this,_resumePosition,TimeSeekOrigin.BeginTime);_movement.Seek(this,_resumePosition,TimeSeekOrigin.BeginTime);
        }
        _colors.Resume(this);
        if(SystemParameters.ClientAreaAnimation)_movement.Resume(this);else _movement.Pause(this);
        IsAnimating=true;
    }
    private void Suspend()
    {
        if(_started)
        {
            _resumePosition=_movement.GetCurrentTime(this)??TimeSpan.Zero;
            foreach(var element in new UIElement[]{DaySky,DuskSky,Stars,Sun})
            {
                double opacity=element.Opacity;element.BeginAnimation(OpacityProperty,null);element.SetCurrentValue(OpacityProperty,opacity);
            }
            double angle=Orbit.Angle;Orbit.BeginAnimation(RotateTransform.AngleProperty,null);Orbit.SetCurrentValue(RotateTransform.AngleProperty,angle);
            foreach(var transform in _noteMotion)
            {
                var matrix=transform.Matrix;
                transform.BeginAnimation(MatrixTransform.MatrixProperty,null);transform.SetCurrentValue(MatrixTransform.MatrixProperty,matrix);
            }
            _colors.Remove(this);_movement.Remove(this);_started=false;
        }
        IsAnimating=false;
    }
    internal void PreviewPhase(double phase)
    {
        _preview=true;_colors.Begin(this,true);_movement.Begin(this,true);_started=true;
        var position=TimeSpan.FromSeconds(Math.Max(phase,0)*CycleSeconds);
        _colors.SeekAlignedToLastTick(this,position,TimeSeekOrigin.BeginTime);_movement.SeekAlignedToLastTick(this,position,TimeSeekOrigin.BeginTime);
        _colors.Pause(this);_movement.Pause(this);IsAnimating=false;
    }
    internal void ResumeAnimation(){_preview=false;Suspend();RefreshMotion();}
}
