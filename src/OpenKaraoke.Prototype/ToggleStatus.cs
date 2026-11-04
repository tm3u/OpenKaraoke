using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace OpenKaraoke.Prototype;

public enum TogglePhase { Disabled, Changing, Enabled }

public static class ToggleStatus
{
    public static readonly DependencyProperty PhaseProperty=DependencyProperty.RegisterAttached("Phase",typeof(TogglePhase),typeof(ToggleStatus),new PropertyMetadata(TogglePhase.Disabled));
    public static readonly DependencyProperty TargetEnabledProperty=DependencyProperty.RegisterAttached("TargetEnabled",typeof(bool),typeof(ToggleStatus),new PropertyMetadata(false));
    public static TogglePhase GetPhase(DependencyObject value)=>(TogglePhase)value.GetValue(PhaseProperty);
    public static void SetPhase(DependencyObject value,TogglePhase phase)=>value.SetValue(PhaseProperty,phase);
    public static bool GetTargetEnabled(DependencyObject value)=>(bool)value.GetValue(TargetEnabledProperty);
    public static void SetTargetEnabled(DependencyObject value,bool enabled)=>value.SetValue(TargetEnabledProperty,enabled);
    public static bool IsChanging(DependencyObject value)=>GetPhase(value)==TogglePhase.Changing;
    public static void Begin(Control control,bool enabled)
    {
        SetTargetEnabled(control,enabled);AutomationProperties.SetItemStatus(control,"處理中");SetPhase(control,TogglePhase.Changing);
    }
    public static void Complete(Control control,bool enabled)
    {
        SetTargetEnabled(control,enabled);AutomationProperties.SetItemStatus(control,"");SetPhase(control,enabled?TogglePhase.Enabled:TogglePhase.Disabled);
    }
}
