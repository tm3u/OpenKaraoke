using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Dsp;
using NAudio.Wave;
using OpenKaraoke.Player;

if (args.Length != 2) throw new ArgumentException("用法：OpenKaraoke.Verifier <player-state.json> <report.json>");
using var diagnostics = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
var root = diagnostics.RootElement;
if (!root.GetProperty("State").GetProperty("Loaded").GetBoolean()) throw new InvalidOperationException("請先播放合成雙音軌測試影片");
string pipeName = root.GetProperty("PipeName").GetString()!;
int mpvProcess = root.GetProperty("MpvProcessId").GetInt32();
nint mainWindow = new(root.GetProperty("MainWindow").GetInt64());
nint videoWindow = new(root.GetProperty("VideoWindow").GetInt64());
var checks = new List<object>();
var measures = new List<object>();
var failures = new List<string>();
await using var connection = new MpvConnection(pipeName);
await connection.ConnectAsync();
double initialVolume=(await connection.GetAsync("volume")).GetDouble();

void Check(string name, bool passed, object evidence)
{
    checks.Add(new { Name = name, Passed = passed, Evidence = evidence });
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {JsonSerializer.Serialize(evidence)}");
    if (!passed) failures.Add(name);
}

try
{
    var renderWindows = new List<long>();
    Native.EnumChildWindows(videoWindow, (window, _) =>
    {
        Native.GetWindowThreadProcessId(window, out int process);
        if (process == mpvProcess && Native.IsWindowVisible(window)) renderWindows.Add(window.ToInt64());
        return true;
    }, 0);
    int detachedWindows = 0;
    Native.EnumWindows((window, _) =>
    {
        Native.GetWindowThreadProcessId(window, out int process);
        if (process == mpvProcess && Native.IsWindowVisible(window)) detachedWindows++;
        return true;
    }, 0);
    Check("影片嵌入同一主視窗", Native.IsChild(mainWindow, videoWindow) && renderWindows.Count > 0 && detachedWindows == 0,
        new { MainWindow = mainWindow.ToInt64(), VideoWindow = videoWindow.ToInt64(), RenderWindows = renderWindows, DetachedMpvWindows = detachedWindows });

    string frameFolder = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
    string framePath = Path.Combine(frameFolder, root.GetProperty("Fullscreen").GetBoolean() ? "embedded-video-fullscreen.png" : "embedded-video-windowed.png");
    await connection.CommandAsync("screenshot-to-file", framePath, "window");
    bool configured = (await connection.GetAsync("vo-configured")).GetBoolean();
    var output = await connection.GetAsync("video-out-params");
    var dimensions = await connection.GetAsync("osd-dimensions");
    Check("影片輸出已建立且可取得實際播放畫面", configured && File.Exists(framePath) && new FileInfo(framePath).Length > 10000,
        new { Frame = framePath, VideoOutput = output, RenderDimensions = dimensions });

    await connection.SetAsync("loop-file", "inf");
    await connection.SetAsync("pause", false);
    await connection.SetAsync("volume", 65);
    await connection.CommandAsync("seek", 10, "absolute+exact");
    foreach (var test in new[] { (Track: 1, Key: 0, Base: 440d), (Track: 2, Key: 0, Base: 660d),
        (Track: 2, Key: 6, Base: 660d), (Track: 2, Key: -6, Base: 660d),
        (Track: 1, Key: 3, Base: 440d), (Track: 2, Key: 3, Base: 660d) })
    {
        double factor = Math.Pow(2, test.Key / 12d);
        await connection.SetAsync("aid", test.Track);
        await connection.SetAsync("pitch", factor);
        await Task.Delay(650);
        double expected = test.Base * factor;
        var measurement = await MeasureAsync(expected);
        double actualPitch = (await connection.GetAsync("pitch")).GetDouble();
        double speed = (await connection.GetAsync("speed")).GetDouble();
        int track = (await connection.GetAsync("aid")).GetInt32();
        var evidence = new { Track = track, test.Key, ExpectedHz = expected, measurement.Frequency, measurement.Peak,
            measurement.Rms, measurement.SampleRate, measurement.GlobalPeakHz, measurement.ToneToMedianNoise, Pitch = actualPitch, Speed = speed };
        measures.Add(evidence);
        Check($"實際輸出音軌 {test.Track}、KEY {test.Key:+0;-0;0}", track == test.Track && Math.Abs(actualPitch - factor) < 0.001
            && Math.Abs(speed - 1) < 0.001 && Math.Abs(measurement.Frequency - expected) < 3
            && measurement.Peak > 0.0003 && measurement.ToneToMedianNoise > 8, evidence);
    }

    double before = (await connection.GetAsync("time-pos")).GetDouble();
    var clock = System.Diagnostics.Stopwatch.StartNew();
    await Task.Delay(2500);
    double after = (await connection.GetAsync("time-pos")).GetDouble();
    double elapsed = clock.Elapsed.TotalSeconds;
    Check("升降調維持正常播放進度", Math.Abs((after - before) - elapsed) < 0.3, new { MediaSeconds = after - before, WallSeconds = elapsed });
    await connection.SetAsync("pause", true);
    before = (await connection.GetAsync("time-pos")).GetDouble();
    await Task.Delay(500);
    after = (await connection.GetAsync("time-pos")).GetDouble();
    Check("暫停停止播放進度", Math.Abs(after - before) < 0.05, new { Before = before, After = after });
    await connection.SetAsync("pause", false);
}
finally
{
    await connection.SetAsync("pitch", 1.0);
    await connection.SetAsync("aid", 2);
    await connection.SetAsync("volume", initialVolume);
    await connection.SetAsync("loop-file", "no");
    await connection.SetAsync("pause", false);
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
    {
        Timestamp = DateTimeOffset.Now, Environment = Environment.OSVersion.ToString(),
        MpvVersion = (await connection.GetAsync("mpv-version")).GetString(),
        Fixture = "人工合成的雙音軌測試影片，非真人原唱或 AI 分離成品", Checks = checks, Measurements = measures,
        Passed = failures.Count == 0, Failures = failures
    }, new JsonSerializerOptions { WriteIndented = true }));
}
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

static async Task<(double Frequency, double Peak, double Rms, int SampleRate, double GlobalPeakHz, double ToneToMedianNoise)> MeasureAsync(double expected)
{
    using var capture = new WasapiLoopbackCapture();
    using var bytes = new MemoryStream();
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    capture.DataAvailable += (_, e) => bytes.Write(e.Buffer, 0, e.BytesRecorded);
    capture.RecordingStopped += (_, e) => { if (e.Exception != null) stopped.TrySetException(e.Exception); else stopped.TrySetResult(); };
    capture.StartRecording();
    await Task.Delay(1400);
    capture.StopRecording();
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    int rate = capture.WaveFormat.SampleRate, block = capture.WaveFormat.BlockAlign;
    byte[] data = bytes.ToArray();
    if (capture.WaveFormat.BitsPerSample != 32) throw new InvalidOperationException("驗證器需要 32-bit loopback 格式");
    int count = data.Length / block;
    int power = (int)Math.Floor(Math.Log2(Math.Min(count, 65536)));
    int length = 1 << power;
    if (length < 8192) throw new IOException("輸出音訊不足，無法判斷頻率");
    var fft = new Complex[length];
    double sum = 0;
    for (int i = 0; i < length; i++)
    {
        float sample = BitConverter.ToSingle(data, (count - length + i) * block);
        sum += sample * sample;
        fft[i].X = (float)(sample * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (length - 1))));
    }
    FastFourierTransform.FFT(true, power, fft);
    int first = (int)(250d * length / rate), last = Math.Min(length / 2 - 2, (int)(1100d * length / rate));
    int globalBin = Enumerable.Range(first, last - first + 1).MaxBy(i => fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
    double Magnitude(int i) => Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);
    int targetFirst = Math.Max(first, (int)((expected - 8) * length / rate));
    int targetLast = Math.Min(last, (int)((expected + 8) * length / rate));
    int peakBin = Enumerable.Range(targetFirst, targetLast - targetFirst + 1).MaxBy(Magnitude);
    double[] noise = Enumerable.Range(first, last - first + 1)
        .Where(i => Math.Abs(i * (double)rate / length - expected) > 15).Select(Magnitude).Order().ToArray();
    double median = noise[noise.Length / 2];
    double a = Math.Log(Math.Max(1e-20, Magnitude(peakBin - 1))), b = Math.Log(Math.Max(1e-20, Magnitude(peakBin))),
        c = Math.Log(Math.Max(1e-20, Magnitude(peakBin + 1)));
    double offset = 0.5 * (a - c) / (a - 2 * b + c);
    return ((peakBin + offset) * rate / length, Magnitude(peakBin), Math.Sqrt(sum / length), rate,
        globalBin * (double)rate / length, Magnitude(peakBin) / Math.Max(median, 1e-12));
}

static class Native
{
    internal delegate bool EnumWindow(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out int process);
    [DllImport("user32.dll")] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
}
