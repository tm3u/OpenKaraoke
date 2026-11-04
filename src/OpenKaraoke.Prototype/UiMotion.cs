using System.Windows;

namespace OpenKaraoke.Prototype;

public static class UiMotion
{
    public static readonly DependencyProperty AllowAnimationProperty = DependencyProperty.RegisterAttached(
        "AllowAnimation", typeof(bool), typeof(UiMotion), new FrameworkPropertyMetadata(true,
            FrameworkPropertyMetadataOptions.Inherits, Changed));
    public static bool GetAllowAnimation(DependencyObject element) => (bool)element.GetValue(AllowAnimationProperty);
    public static void SetAllowAnimation(DependencyObject element, bool value) => element.SetValue(AllowAnimationProperty, value);
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is MarqueeText text) text.RefreshMotion();
        if (element is IdleLandscape landscape) landscape.RefreshMotion();
    }
}
