using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OpenKaraoke.Scoring;

public enum PitchBackend { Auto, Cpu, Cuda }

public readonly record struct PitchFrame(double Time, double Hertz, double Confidence)
{
    public bool Voiced => Confidence >= .5 && Hertz is >= 46.875 and <= 2093.75;
    public double Midi => 69 + 12 * Math.Log2(Hertz / 440);
}

public sealed class PitchDetector : IDisposable
{
    public const int SampleRate = 16000, Hop = 256;
    public const double FramePeriod = .016;
    public const string ModelHash = "6385e8c2ebc3872e82c9ff5946228de44cd3be77a750ea53698b7dbfe94b0a22";
    private InferenceSession _session;
    private readonly string _model;
    private PitchBackend _backend;
    public PitchBackend Backend=>_backend;

    public PitchDetector(string model, PitchBackend backend = PitchBackend.Auto, int deviceId = 0,string? profilePrefix=null)
    {
        using var input = File.OpenRead(model);
        if (Convert.ToHexStringLower(SHA256.HashData(input)) != ModelHash)
            throw new IOException("音高模型驗證失敗，請重新解壓縮完整程式。");
        _model=model;
        bool automatic=backend==PitchBackend.Auto;
        if(backend==PitchBackend.Auto)(backend,deviceId)=PitchAcceleration.Current(model);
        _backend=backend;
        try{_session=CreateSession(model,backend,deviceId,profilePrefix);}
        catch(Exception e)when(automatic&&backend==PitchBackend.Cuda&&e is OnnxRuntimeException or System.ComponentModel.Win32Exception or IOException)
        {_backend=PitchBackend.Cpu;_session=CreateSession(model,PitchBackend.Cpu,0);}
    }
    private static InferenceSession CreateSession(string model,PitchBackend backend,int deviceId,string? profilePrefix=null)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        if(profilePrefix!=null){options.ProfileOutputPathPrefix=profilePrefix;options.EnableProfiling=true;}
        if (backend == PitchBackend.Cuda)
        {
            CudaLibraries.Load();
            using var cuda=new OrtCUDAProviderOptions();
            cuda.UpdateOptions(new Dictionary<string,string>{{"device_id",deviceId.ToString()},{"gpu_mem_limit","134217728"},{"arena_extend_strategy","kSameAsRequested"},{"cudnn_conv_algo_search","HEURISTIC"},{"do_copy_in_default_stream","1"},{"cudnn_conv_use_max_workspace","0"},{"use_tf32","0"}});
            options.AppendExecutionProvider_CUDA(cuda);
        }
        return new InferenceSession(model, options);
    }

    public PitchFrame[] Detect(float[] audio, double start = 0)
    {
        try{return DetectCore(audio,start);}
        catch(OnnxRuntimeException)when(_backend==PitchBackend.Cuda)
        {
            _session.Dispose();_session=CreateSession(_model,PitchBackend.Cpu,0);_backend=PitchBackend.Cpu;
            return DetectCore(audio,start);
        }
    }
    private PitchFrame[] DetectCore(float[] audio,double start)
    {
        if (audio.Length == 0) return [];
        var inputs = new[] {
            NamedOnnxValue.CreateFromTensor("audio", new DenseTensor<float>(audio, new[] {1, audio.Length})),
            NamedOnnxValue.CreateFromTensor("fmin", new DenseTensor<float>(new[] {46.875f}, Array.Empty<int>())),
            NamedOnnxValue.CreateFromTensor("fmax", new DenseTensor<float>(new[] {2093.75f}, Array.Empty<int>()))
        };
        using var output = _session.Run(inputs);
        double[] Read(string name) => _session.OutputMetadata[name].ElementType == typeof(double)
            ? output.First(v => v.Name == name).AsEnumerable<double>().ToArray()
            : output.First(v => v.Name == name).AsEnumerable<float>().Select(v => (double)v).ToArray();
        var pitch = Read("pitch"); var confidence = Read("confidence");
        var result = new PitchFrame[pitch.Length];
        for (int i = 0; i < result.Length; i++)
        {
            float peak = 0;
            for (int j = i * Hop; j < Math.Min(audio.Length, (i + 1) * Hop); j++) peak = Math.Max(peak, Math.Abs(audio[j]));
            result[i] = new(start + i * FramePeriod, pitch[i], peak < .001 ? 0 : confidence[i]);
        }
        return result;
    }

    public void Dispose() => _session.Dispose();
    public string EndProfiling()=>_session.EndProfiling();
}

public sealed class PitchStream(PitchDetector detector)
{
    private readonly List<float> _buffer = [];
    private int _base, _emitted;
    private bool _closed;
    public PitchFrame[] Push(float[] audio, bool final = false)
    {
        if (_closed) throw new InvalidOperationException("音高串流已結束。");
        _buffer.AddRange(audio);
        int first = _emitted - _base, available = _buffer.Count / PitchDetector.Hop;
        if (final && _base == 0 && available == 0 && _buffer.Count > 0) available = 1;
        int last = final ? available : available - 10;
        PitchFrame[] result = last > first
            ? detector.Detect(_buffer.ToArray(), _base * PitchDetector.FramePeriod)[first..last] : [];
        _emitted += result.Length;
        int keep = Math.Max(0, _emitted - 11) - _base;
        if (keep > 0) { _buffer.RemoveRange(0, keep * PitchDetector.Hop); _base += keep; }
        if (final) { _closed = true; _buffer.Clear(); }
        return result;
    }
}
