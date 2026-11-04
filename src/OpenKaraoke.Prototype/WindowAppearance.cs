using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenKaraoke.Prototype;

internal static class WindowAppearance
{
    internal static void UseSquareCorners(Window window)
    {
        window.SourceInitialized+=(_,_)=>
        {
            if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))return;
            int preference=1;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle,33,ref preference,sizeof(int));
        };
    }
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(nint window,int attribute,ref int value,int size);
}
