using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OpenKaraoke.Prototype;

public sealed class QueueAddedFeedback : Grid
{
    private static readonly DependencyPropertyKey IsConfirmingKey=DependencyProperty.RegisterReadOnly(
        nameof(IsConfirming),typeof(bool),typeof(QueueAddedFeedback),new PropertyMetadata(false));
    public static readonly DependencyProperty IsConfirmingProperty=IsConfirmingKey.DependencyProperty;
    public bool IsConfirming=>(bool)GetValue(IsConfirmingProperty);
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(900)};

    public QueueAddedFeedback()
    {
        _timer.Tick+=(_,_)=>Reset();
        Unloaded+=(_,_)=>Reset();DataContextChanged+=(_,_)=>Reset();
    }
    public void ShowAdded(TimeSpan remaining)
    {
        SetValue(IsConfirmingKey,true);_timer.Stop();_timer.Interval=remaining;_timer.Start();
        UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void Reset(){_timer.Stop();SetValue(IsConfirmingKey,false);}
    protected override AutomationPeer OnCreateAutomationPeer()=>new FrameworkElementAutomationPeer(this);
}
