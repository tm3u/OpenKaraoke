using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenKaraoke.Prototype;

public sealed class MarqueeText : Decorator
{
    public static readonly DependencyProperty TextProperty=DependencyProperty.Register(nameof(Text),typeof(string),typeof(MarqueeText),new FrameworkPropertyMetadata("",FrameworkPropertyMetadataOptions.AffectsMeasure,TextChanged));
    public static readonly DependencyProperty ForegroundProperty=TextElement.ForegroundProperty.AddOwner(typeof(MarqueeText),new FrameworkPropertyMetadata(Brushes.White,FrameworkPropertyMetadataOptions.Inherits));
    public string Text{get=>(string)GetValue(TextProperty);set=>SetValue(TextProperty,value);}
    public Brush Foreground{get=>(Brush)GetValue(ForegroundProperty);set=>SetValue(ForegroundProperty,value);}
    private readonly TextBlock _label=new(){TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.None};
    private readonly TranslateTransform _movement=new();
    private double _distance=-1;
    public bool Scrolling{get;private set;}
    internal double ScrollOffset=>_movement.X;
    internal double CycleSeconds=>Math.Max(0,_distance)/24+2.4;
    public MarqueeText()
    {
        ClipToBounds=true;Child=_label;_label.RenderTransform=_movement;
        _label.SetBinding(TextBlock.TextProperty,new Binding(nameof(Text)){Source=this});
        _label.SetBinding(TextBlock.ForegroundProperty,new Binding(nameof(Foreground)){Source=this});
        Loaded+=(_,_)=>Animate();Unloaded+=(_,_)=>Stop();IsVisibleChanged+=(_,_)=>{if(IsVisible)Animate();else Stop();};
    }
    private static void TextChanged(DependencyObject sender,DependencyPropertyChangedEventArgs e)
    {var element=(MarqueeText)sender;element._distance=-1;System.Windows.Automation.AutomationProperties.SetName(element,(string?)e.NewValue??"");}
    protected override Size MeasureOverride(Size available)
    {_label.Measure(new(double.PositiveInfinity,double.PositiveInfinity));return new(double.IsFinite(available.Width)?Math.Min(available.Width,_label.DesiredSize.Width):_label.DesiredSize.Width,_label.DesiredSize.Height);}
    protected override Size ArrangeOverride(Size size)
    {
        bool motion=UiMotion.GetAllowAnimation(this);_label.TextTrimming=motion?TextTrimming.None:TextTrimming.CharacterEllipsis;
        _label.Arrange(new Rect(0,0,motion?Math.Max(size.Width,_label.DesiredSize.Width):size.Width,size.Height));Animate(size.Width);return size;
    }
    private void Stop(){_movement.BeginAnimation(TranslateTransform.XProperty,null);_movement.X=0;Scrolling=false;_distance=-1;}
    internal void RefreshMotion(){Stop();InvalidateArrange();}
    private void Animate(double? width=null)
    {
        if(!IsLoaded||!IsVisible)return;
        if(!UiMotion.GetAllowAnimation(this)){Stop();return;}
        double distance=Math.Max(0,_label.DesiredSize.Width-(width??ActualWidth));
        if(Math.Abs(distance-_distance)<.5)return;Stop();_distance=distance;if(distance<=1)return;
        double travel=distance/24;var animation=new DoubleAnimationUsingKeyFrames{RepeatBehavior=RepeatBehavior.Forever,Duration=TimeSpan.FromSeconds(travel+2.4)};
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-distance,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2+travel))));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(-distance,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(travel+2.4))));
        Timeline.SetDesiredFrameRate(animation,30);_movement.BeginAnimation(TranslateTransform.XProperty,animation);Scrolling=true;
    }
}
