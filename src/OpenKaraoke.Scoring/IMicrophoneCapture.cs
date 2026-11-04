namespace OpenKaraoke.Scoring;

public interface IMicrophoneCapture : IAsyncDisposable
{
    event Action<PitchFrame,double>? Frame;
    event Action<double>? Level;
    event Action<Exception>? Failed;
    Task StartAsync(string deviceId,string model,bool detectPitch=true);
}
