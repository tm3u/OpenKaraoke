using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyIdleLoopAsync(Action<string,bool,object?> check,string folder)
    {
        var canvas=(Canvas)IdleScene.FindName("LandscapeCanvas");
        var back=(Canvas)IdleScene.FindName("BackNotes");var front=(Canvas)IdleScene.FindName("FrontNotes");
        var notes=back.Children.Cast<Icon>().Concat(front.Children.Cast<Icon>()).ToArray();
        check("待機保留山景與日月，加入前後分層的 32 個音符",back.Children.Count==22&&front.Children.Count==10&&notes.All(n=>n.Kind=="music")&&IdleScene.FindName("DaySky")!=null&&IdleScene.FindName("Orbit")!=null,null);
        async Task<Point[]> Positions(double phase)
        {
            IdleScene.PreviewPhase(phase);await Dispatcher.InvokeAsync(IdleScene.UpdateLayout,DispatcherPriority.ApplicationIdle);
            return notes.Select(n=>n.TranslatePoint(new(n.ActualWidth/2,n.ActualHeight/2),canvas)).ToArray();
        }
        byte[] Pixels(string? name=null)
        {
            var bitmap=new RenderTargetBitmap(720,405,96,96,PixelFormats.Pbgra32);bitmap.Render(canvas);
            var pixels=new byte[720*405*4];bitmap.CopyPixels(pixels,720*4,0);
            if(name!=null){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(folder,name));encoder.Save(file);}
            return pixels;
        }
        var start=await Positions(0);var first=Pixels("idle-notes-day.png");
        var end=await Positions(1);var last=Pixels("idle-loop-end.png");
        check("90 秒循環首尾整張畫面逐像素一致",first.SequenceEqual(last),new{DifferentChannels=first.Zip(last).Count(p=>p.First!=p.Second)});
        check("所有音符循環接點沒有位置跳動",start.Zip(end).All(p=>(p.First-p.Second).Length<.000001),start.Zip(end).Max(p=>(p.First-p.Second).Length));
        await Positions(2);var repeated=Pixels();
        check("連續兩輪後畫面仍一致，沒有相位漂移",first.SequenceEqual(repeated),null);
        const double frame=1d/30;
        foreach(double second in new[]{30d,45,60,90})
        {
            var before=await Positions((second-frame)/90);var center=await Positions(second/90);var after=await Positions((second+frame)/90);
            double distance=0,velocityChange=0;
            for(int i=0;i<notes.Length;i++)
            {
                var incoming=center[i]-before[i];var outgoing=after[i]-center[i];
                distance=Math.Max(distance,Math.Max(incoming.Length,outgoing.Length));velocityChange=Math.Max(velocityChange,(incoming-outgoing).Length);
            }
            check($"{second} 秒軌道接點前後不瞬移或突然改變速度",distance<4&&velocityChange<.15,new{MaximumFrameDistance=distance,FrameVelocityChange=velocityChange});
        }
        await Positions(.25);Pixels("idle-notes-dusk.png");
        await Positions(.5);Pixels("idle-notes-night.png");
        var beforeFrame=await Positions(1-frame/90);Pixels("idle-loop-before.png");
        var afterFrame=await Positions(1+frame/90);Pixels("idle-loop-after.png");
        check("跨循環前後一格音符仍沿同方向移動",beforeFrame.Zip(afterFrame).All(p=>(p.First-p.Second).Length<8),null);
        IdleScene.ResumeAnimation();await Task.Delay(120);
        var moving=notes.Select(n=>n.TranslatePoint(new(),canvas)).ToArray();
        await Task.Delay(160);
        var delta=notes.Select((n,i)=>(n.TranslatePoint(new(),canvas)-moving[i]).Length).ToArray();
        check("實際時鐘持續推進所有音符，無須畫面重載",delta.All(d=>d>.1),new{MinimumDelta=delta.Min(),MaximumDelta=delta.Max(),IdleScene.IsAnimating,IdleScene.CyclePositionSeconds});
        IdleScene.Visibility=Visibility.Collapsed;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        double phase=IdleScene.CyclePositionSeconds;var hidden=notes.Select(n=>n.TranslatePoint(new(),canvas)).ToArray();await Task.Delay(120);
        check("隱藏待機時音符動畫一併移除時鐘並停止推進",!IdleScene.HasAnimationClock&&Math.Abs(IdleScene.CyclePositionSeconds-phase)<.001&&notes.Select((n,i)=>(n.TranslatePoint(new(),canvas)-hidden[i]).Length).All(d=>d<.000001),null);
        IdleScene.Visibility=Visibility.Visible;await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        check("重新顯示待機音符從保存相位接續，沒有重新從頭播放",IdleScene.IsAnimating&&Math.Abs(IdleScene.CyclePositionSeconds-phase)<.1,new{IdleScene.IsAnimating,Before=phase,After=IdleScene.CyclePositionSeconds});
        UiMotion.SetAllowAnimation(IdleScene,false);
        var still=notes.Select(n=>n.TranslatePoint(new(),canvas)).ToArray();await Task.Delay(120);
        var stillDelta=notes.Select((n,i)=>(n.TranslatePoint(new(),canvas)-still[i]).Length).ToArray();
        check("停用動態效果時音符不移動",!IdleScene.IsAnimating&&stillDelta.All(d=>d<.000001),new{IdleScene.IsAnimating,MaximumDelta=stillDelta.Max()});
        UiMotion.SetAllowAnimation(IdleScene,true);
    }
}
