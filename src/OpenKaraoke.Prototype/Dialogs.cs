using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using OpenKaraoke.Library;
using OpenKaraoke.Player;

namespace OpenKaraoke.Prototype;

public class KaraokeDialog : Window
{
    protected readonly StackPanel Body = new();
    protected readonly StackPanel Actions = new() { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right, Margin=new(0,20,0,0) };
    protected readonly TextBlock ErrorText = new() { TextWrapping=TextWrapping.Wrap, FontSize=12, Margin=new(0,12,0,0) };
    protected readonly DockPanel Heading = new() { Margin=new(0,0,0,18) };
    protected readonly TextBlock HeadingText = new() { FontSize=17 };
    public KaraokeDialog(Window owner, string title, double width=400)
    {
        WindowAppearance.UseSquareCorners(this);
        NameScope.SetNameScope(this,new NameScope());
        Owner=owner; Title=title; Width=width; SizeToContent=SizeToContent.Height; ResizeMode=ResizeMode.NoResize;
        Style=(Style)FindResource(typeof(Window));
        WindowStyle=WindowStyle.None; WindowStartupLocation=WindowStartupLocation.CenterOwner; ShowInTaskbar=false;
        Background=(Brush)FindResource("PanelBrush"); ErrorText.Foreground=(Brush)FindResource("Danger");
        var errorStyle=new Style(typeof(TextBlock));
        var emptyError=new Trigger { Property=TextBlock.TextProperty, Value="" };
        emptyError.Setters.Add(new Setter(VisibilityProperty,Visibility.Collapsed));errorStyle.Triggers.Add(emptyError);ErrorText.Style=errorStyle;
        var content = new StackPanel();
        HeadingText.Text=title; Heading.Children.Add(HeadingText);
        Heading.MouseLeftButtonDown+=(_,e)=>{ if(e.ChangedButton==MouseButton.Left && e.OriginalSource is TextBlock) DragMove(); };
        var errors=new ScrollViewer { Content=ErrorText,MaxHeight=96,Margin=new(0,12,0,0),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        ErrorText.Margin=new(0);errors.SetBinding(VisibilityProperty,new Binding(nameof(Visibility)){Source=ErrorText});
        RegisterName("DialogError",ErrorText);RegisterName("DialogErrorViewport",errors);
        content.Children.Add(Heading); content.Children.Add(Body); content.Children.Add(errors); content.Children.Add(Actions);
        Content=new Border { Background=Background, BorderBrush=(Brush)FindResource("Line"), BorderThickness=new(1), CornerRadius=new(0), Padding=new(20), Child=content };
        PreviewKeyDown+=(_,e)=>{ if(e.Key==Key.Escape) { Close(); e.Handled=true; } };
    }
    protected static void Field(StackPanel body, string label, UIElement input)
    {
        body.Children.Add(new TextBlock { Text=label, TextWrapping=TextWrapping.Wrap, FontSize=12, Foreground=(Brush)Application.Current.FindResource("Sub"), Margin=new(0,12,0,6) });
        body.Children.Add(input);
    }
    protected Button Action(string title, bool primary=false)
    {
        var button = new Button { Content=title, Margin=new(8,0,0,0), Style=(Style)FindResource(primary ? "PrimaryButton" : typeof(Button)) };
        Actions.Children.Add(button); return button;
    }
}

public sealed class RemoveSongDialog : KaraokeDialog
{
    public string Confirmation { get; private set; } = "";
    public RemoveSongDialog(Window owner, Song song) : base(owner,"刪除歌曲")
    {
        var target=new DockPanel();
        var cover=new Border { Width=42, Height=42, CornerRadius=new(7), Background=Covers.For(song.Id), Margin=new(0,0,12,0),
            Child=new TextBlock { Text=song.Initial, FontSize=19, HorizontalAlignment=HorizontalAlignment.Center } };
        DockPanel.SetDock(cover,Dock.Left);target.Children.Add(cover);
        var copy=new StackPanel { VerticalAlignment=VerticalAlignment.Center };
        copy.Children.Add(new TextBlock { Text=song.Title, TextWrapping=TextWrapping.Wrap });
        copy.Children.Add(new TextBlock { Text=song.Artist, Foreground=(Brush)FindResource("Sub"), FontSize=12, Margin=new(0,4,0,0), TextWrapping=TextWrapping.Wrap });
        target.Children.Add(copy);
        Body.Children.Add(new Border { Background=(Brush)FindResource("Raised"), CornerRadius=new(8), Padding=new(14), Child=target });
        var input=new TextBox { Name="ConfirmationInput" }; RegisterName(input.Name,input);System.Windows.Automation.AutomationProperties.SetName(input,"輸入歌曲名稱確認刪除");
        var field=new Grid();field.Children.Add(input);
        var hint=new TextBlock { Text="歌曲名稱", Foreground=(Brush)FindResource("Sub"), Margin=new(11,0,0,0), IsHitTestVisible=false };field.Children.Add(hint);
        Field(Body,$"請輸入「{song.Title}」確認刪除",field);
        Action("取消").Click+=(_,_)=>DialogResult=false;
        var confirm=Action("確認刪除",true); confirm.IsEnabled=false;confirm.IsDefault=true;RegisterName("ConfirmButton",confirm);
        confirm.Background=(Brush)FindResource("Danger");
        input.TextChanged+=(_,_)=>{confirm.IsEnabled=input.Text==song.Title;hint.Visibility=input.Text.Length==0?Visibility.Visible:Visibility.Collapsed;};
        confirm.Click+=(_,_)=>{ Confirmation=input.Text; DialogResult=true; };
        Loaded+=(_,_)=>input.Focus();
    }
}

