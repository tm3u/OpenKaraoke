using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenKaraoke.Prototype;

public sealed class NativeVideoHost : HwndHost
{
    public nint VideoHandle { get; private set; }
    public double CornerRadius {get;set;}=11;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        VideoHandle = CreateWindowEx(0, "STATIC", "OpenKaraoke Video Host", 0x50000000 | 0x04000000 | 0x02000000 | 0x00000004,
            0, 0, 1, 1, hwndParent.Handle, 0, 0, 0);
        if (VideoHandle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new HandleRef(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) { DestroyWindow(hwnd.Handle); VideoHandle = 0; }

    protected override void OnWindowPositionChanged(Rect bounds)
    {
        base.OnWindowPositionChanged(bounds);
        if(VideoHandle==0||!GetClientRect(VideoHandle,out var client))return;
        int radius=(int)(2*CornerRadius*GetDpiForWindow(VideoHandle)/96d);
        nint region=CreateRoundRectRgn(0,0,client.Right+1,client.Bottom+1,radius,radius);
        if(region!=0&&SetWindowRgn(VideoHandle,region,true)==0)DeleteObject(region);
    }

    [StructLayout(LayoutKind.Sequential)] private struct ClientRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")]private static extern bool GetClientRect(nint window,out ClientRect rect);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint window);
    [DllImport("gdi32.dll")]private static extern nint CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
    [DllImport("user32.dll")]private static extern int SetWindowRgn(nint window,nint region,bool redraw);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int exStyle, string className, string title, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);
}
