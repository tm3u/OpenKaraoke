using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private async Task VerifyIdlePerformanceAsync(string report)
    {
        var samples=new List<object>();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            if(_queue.CurrentSong!=null)throw new InvalidOperationException("待機效能量測必須使用獨立的空白資料目錄。");
            foreach(bool fullscreen in new[]{false,true})
            {
                SetFullscreen(fullscreen);await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
                await Task.Delay(1000);
                if(!IdleScene.IsAnimating)throw new InvalidOperationException("目前系統未啟用動畫，不能量測動畫更新頻率。");
                var note=((Canvas)IdleScene.FindName("FrontNotes")).Children.OfType<Icon>().First();
                var intervals=new List<double>();int callbacks=0,updates=0;
                TimeSpan lastRender=TimeSpan.MinValue;double lastUpdate=-1;Matrix previous=note.RenderTransform.Value;
                var process=Process.GetCurrentProcess();double cpuBefore=process.TotalProcessorTime.TotalMilliseconds;
                long allocatedBefore=GC.GetTotalAllocatedBytes();var watch=Stopwatch.StartNew();
                void Rendering(object? sender,EventArgs args)
                {
                    var time=((RenderingEventArgs)args).RenderingTime;if(time==lastRender)return;lastRender=time;callbacks++;
                    var current=note.RenderTransform.Value;if(current==previous)return;
                    double now=watch.Elapsed.TotalMilliseconds;
                    if(lastUpdate>=0)intervals.Add(now-lastUpdate);
                    lastUpdate=now;previous=current;updates++;
                }
                CompositionTarget.Rendering+=Rendering;
                try{await Task.Delay(4000);}finally{CompositionTarget.Rendering-=Rendering;watch.Stop();}
                intervals.Sort();
                double Percentile(double fraction)=>intervals.Count==0?0:intervals[Math.Clamp((int)Math.Ceiling(intervals.Count*fraction)-1,0,intervals.Count-1)];
                samples.Add(new
                {
                    Mode=fullscreen?"fullscreen":"window",Width=ActualWidth,Height=ActualHeight,Seconds=watch.Elapsed.TotalSeconds,
                    RenderCallbacks=callbacks,MotionUpdates=updates,MotionUpdatesPerSecond=updates/watch.Elapsed.TotalSeconds,
                    MedianUpdateIntervalMs=Percentile(.5),P95UpdateIntervalMs=Percentile(.95),MaximumUpdateIntervalMs=Percentile(1),
                    IntervalsOver25Ms=intervals.Count(i=>i>25),ProcessCpuMilliseconds=process.TotalProcessorTime.TotalMilliseconds-cpuBefore,
                    AllocatedBytes=GC.GetTotalAllocatedBytes()-allocatedBefore,HardwareTier=RenderCapability.Tier>>16,
                    IdleScene.MotionChannelCount,IdleScene.DesiredMotionFrameRate
                });
            }
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{Completed=true,Version=typeof(MainWindow).Assembly.GetName().Version?.ToString(3),Samples=samples,Measurement="CompositionTarget.Rendering 時實際音符矩陣的位置變更頻率；不是螢幕硬體呈現率。"},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception error){await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{Completed=false,Samples=samples,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));}
        finally{Close();}
    }
}
