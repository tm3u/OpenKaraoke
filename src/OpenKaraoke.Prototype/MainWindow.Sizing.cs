using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private Rect _videoBounds;
    private Rect BoundsOnWindow(FrameworkElement element)=>element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
    private void InitializeSizing()
    {
        WindowAppearance.UseSquareCorners(this);
        SourceInitialized+=(_,_)=>HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SizingMessage);
        Surface.SizeChanged+=(_,_)=>ResizeCanvas();
        Surface.LayoutUpdated+=(_,_)=>PositionVideo();
    }
    private void ResizeCanvas()
    {
        if(Surface.ActualWidth<=0||Surface.ActualHeight<=0)return;
        double scale=Math.Min(Surface.ActualWidth/1600,Surface.ActualHeight/900);
        AppCanvas.Width=Surface.ActualWidth/scale;AppCanvas.Height=Surface.ActualHeight/scale;
    }
    private void PositionVideo()
    {
        if(!IsLoaded||VideoViewport.ActualWidth<=0||VideoViewport.ActualHeight<=0)return;
        var bounds=VideoViewport.TransformToAncestor(Surface).TransformBounds(new Rect(VideoViewport.RenderSize));
        if(bounds==_videoBounds)return;_videoBounds=bounds;
        VideoHost.CornerRadius=11*bounds.Width/VideoViewport.ActualWidth;
        VideoHost.Margin=new(bounds.X,bounds.Y,0,0);VideoHost.Width=bounds.Width;VideoHost.Height=bounds.Height;
    }
    private nint SizingMessage(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message==0x0024)
        {
            var monitor=new MonitorInformation{Size=Marshal.SizeOf<MonitorInformation>()};
            if(!GetMonitorInfo(MonitorFromWindow(hwnd,2),ref monitor))return 0;
            var area=monitor.Work;int availableWidth=area.Right-area.Left,availableHeight=area.Bottom-area.Top;
            var limits=Marshal.PtrToStructure<WindowLimits>(lParam);
            limits.MaxPosition=new(area.Left-monitor.Monitor.Left,area.Top-monitor.Monitor.Top);
            double dpi=System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            limits.MinTrackSize=new((int)Math.Ceiling(MinWidth*dpi),(int)Math.Ceiling(MinHeight*dpi));
            limits.MaxSize=new(availableWidth,availableHeight);Marshal.StructureToPtr(limits,lParam,false);handled=true;
        }
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPoint(int x,int y){public int X=x,Y=y;}
    [StructLayout(LayoutKind.Sequential)] private struct WindowLimits{public WindowPoint Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize;}
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInformation{public int Size;public WindowBounds Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")]private static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW")]private static extern bool GetMonitorInfo(nint monitor,ref MonitorInformation information);
    [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern nint SendSizingMessage(nint hwnd,int message,nint wParam,nint lParam);
}
