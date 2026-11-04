using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenKaraoke.Scoring;

public sealed record PitchAccelerationResult(PitchBackend Backend,int DeviceId,double CpuP95,double SelectedP95,string Reason,string[] GpuErrors);

public static class PitchAcceleration
{
    private static readonly ConcurrentDictionary<string,Lazy<Task<PitchAccelerationResult>>> Results=new(StringComparer.OrdinalIgnoreCase);
    public static Task<PitchAccelerationResult> PrepareAsync(string model)
    {string path=Path.GetFullPath(model);return Results.GetOrAdd(path+"|"+CudaLibraries.Available,_=>new(()=>Task.Run(()=>Measure(path)))).Value;}
    internal static (PitchBackend Backend,int DeviceId) Current(string model)
    {
        var pending=PrepareAsync(model);
        return pending.IsCompletedSuccessfully?(pending.Result.Backend,pending.Result.DeviceId):(PitchBackend.Cpu,0);
    }
    private static PitchAccelerationResult Measure(string model)
    {
        var errors=new List<string>();double cpu;
        try{cpu=MeasureTwo(model,PitchBackend.Cpu,0);}
        catch(Exception e){return new(PitchBackend.Cpu,0,0,0,"使用 CPU；背景測量未完成",[e.Message]);}
        double fastest=cpu;bool gpu=false;
        try{double time=MeasureTwo(model,PitchBackend.Cuda,0);if(time<cpu*.9){fastest=time;gpu=true;}}
        catch(Exception e){errors.Add(e.Message);}
        return new(gpu?PitchBackend.Cuda:PitchBackend.Cpu,0,cpu,fastest,gpu?"兩路 GPU 實測較快":errors.Count==0?"兩路 CPU 實測較快":"GPU 無法使用，保留 CPU",errors.ToArray());
    }
    private static double MeasureTwo(string model,PitchBackend backend,int device)
    {
        var detectors=new List<PitchDetector>();
        try
        {
            for(int i=0;i<2;i++)detectors.Add(new(model,backend,device));
            var times=new double[2][];
            Parallel.For(0,2,index=>
            {
                var audio=Enumerable.Range(0,6400).Select(i=>(float)(.2*Math.Sin(2*Math.PI*440*i/16000))).ToArray();
                var detector=detectors[index];detector.Detect(audio);times[index]=new double[8];
                for(int i=0;i<8;i++)
                {
                    long start=Stopwatch.GetTimestamp();var frames=detector.Detect(audio);times[index][i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if(frames.Length<20||!frames[12].Voiced||Math.Abs(frames[12].Hertz-440)>3)throw new IOException("音高推論結果不符，保留 CPU。");
                }
            });
            var ordered=times.SelectMany(t=>t).Order().ToArray();return ordered[(int)Math.Floor((ordered.Length-1)*.95)];
        }
        finally{foreach(var detector in detectors)detector.Dispose();}
    }
}
