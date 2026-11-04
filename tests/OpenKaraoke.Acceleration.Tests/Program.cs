using System.Diagnostics;
using System.Text.Json;
using OpenKaraoke.Scoring;

string root=Path.GetFullPath(args.FirstOrDefault()??".");
string model=Path.Combine(root,"tools/scoring/swift-f0-0.3.0.onnx");
var results=new List<object>();
string folder=Path.Combine(root,"artifacts","acceleration-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(folder);
int gpuKernels=0;
foreach(var configuration in new[]{(PitchBackend.Cpu,0),(PitchBackend.Cuda,0)})
{
    var (backend,deviceId)=configuration;
    var detectors=new List<PitchDetector>();
    try
    {
        for(int i=0;i<4;i++)detectors.Add(new(model,backend,deviceId));
        var elapsed=new double[4][];int iteration=0;
        await Task.WhenAll(detectors.Select((detector,index)=>Task.Run(()=>
        {
            double[] frequencies=[440,466.1637615,523.2511306,0];double hz=frequencies[index];
            var audio=Enumerable.Range(0,6400).Select(i=>(float)(.2*Math.Sin(2*Math.PI*hz*i/16000))).ToArray();
            detector.Detect(audio);var samples=new List<double>();
            for(int i=0;i<30;i++)
            {
                long start=Stopwatch.GetTimestamp();var frames=detector.Detect(audio);samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if(frames.Length<20||(hz==0?frames.Any(f=>f.Voiced):!frames[12].Voiced||Math.Abs(frames[12].Midi-(69+12*Math.Log2(hz/440)))>.4))throw new Exception("四路獨立音高結果不符");
                Interlocked.Increment(ref iteration);
            }
            elapsed[index]=samples.ToArray();
        })));
        var times=elapsed.SelectMany(x=>x).Order().ToArray();
        results.Add(new{Backend=backend.ToString(),DeviceId=deviceId,Passed=true,Runs=iteration,Median=times[times.Length/2],P95=times[(int)(times.Length*.95)],Maximum=times[^1]});
        if(backend==PitchBackend.Cuda)
        {
            using var profiled=new PitchDetector(model,backend,0,Path.Combine(folder,"cuda-profile"));profiled.Detect(new float[6400]);string profile=profiled.EndProfiling();
            using var document=JsonDocument.Parse(File.ReadAllText(profile));gpuKernels=document.RootElement.EnumerateArray().Count(node=>node.TryGetProperty("args",out var details)&&details.TryGetProperty("provider",out var provider)&&provider.GetString()=="CUDAExecutionProvider");
            if(gpuKernels==0)throw new Exception("GPU 路徑未實際執行 CUDA 運算");
        }
    }
    catch(Exception e){results.Add(new{Backend=backend.ToString(),DeviceId=deviceId,Passed=false,Error=e.ToString()});}
    finally{foreach(var detector in detectors)detector.Dispose();}
}
var automatic=await PitchAcceleration.PrepareAsync(model);
if(args.Contains("--require-gpu")&&gpuKernels==0)throw new Exception("沒有通過實際 GPU 運算驗證");
if(automatic.CpuP95<=0)throw new Exception("CPU 基準測量未完成");
using(var detector=new PitchDetector(model))
{
    var frames=detector.Detect(Enumerable.Range(0,16000).Select(i=>(float)(.2*Math.Sin(2*Math.PI*440*i/16000))).ToArray());
    if(detector.Backend!=automatic.Backend||frames[30..50].Any(f=>!f.Voiced||Math.Abs(f.Midi-69)>.25))throw new Exception("自動選擇與音高結果不符");
}
string installedCuda=CudaLibraries.DirectoryPath;CudaLibraries.DirectoryPath=Path.Combine(folder,"missing-cuda");var withoutCuda=await PitchAcceleration.PrepareAsync(model);if(withoutCuda.Backend!=PitchBackend.Cpu)throw new Exception("缺少 CUDA 元件時沒有回退 CPU");CudaLibraries.DirectoryPath=installedCuda;
string json=JsonSerializer.Serialize(new{Passed=true,Automatic=automatic,ActualCudaKernelCount=gpuKernels,WithoutCuda=withoutCuda,FourConcurrentStreams=results},new JsonSerializerOptions{WriteIndented=true});await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),json);Console.WriteLine(json);Console.WriteLine(folder);
