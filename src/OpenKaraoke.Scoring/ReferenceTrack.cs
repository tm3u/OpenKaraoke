using System.Text.Json;

namespace OpenKaraoke.Scoring;

public sealed record ReferenceDocument(int Version, string Algorithm, double Duration, float[] PitchHz, float[] Confidence, float[]? LoudnessDb = null);
public readonly record struct MelodyNote(double Start, double End, double Midi);

public sealed class ReferenceTrack
{
    public ReferenceDocument Document { get; }
    public double[] Midi { get; }
    public MelodyNote[] Notes { get; }
    public int VoicedFrames => Midi.Count(double.IsFinite);
    public ReferenceTrack(ReferenceDocument document, CancellationToken cancellation = default)
    {
        if (document == null || document.Version != 1 || document.Algorithm != "swift-f0-0.3.0" || !double.IsFinite(document.Duration)
            || document.Duration is <= 0 or > 86400 || document.PitchHz == null || document.Confidence == null
            || document.PitchHz.Length != document.Confidence.Length
            || Math.Abs(document.PitchHz.Length * PitchDetector.FramePeriod - document.Duration) > .1
            || document.PitchHz.Any(v => !float.IsFinite(v) || v is < 0 or > 2200)
            || document.Confidence.Any(v => !float.IsFinite(v) || v is < 0 or > 1)
            || (document.LoudnessDb is { } levels && (levels.Length != document.PitchHz.Length || levels.Any(v => !float.IsFinite(v)))))
            throw new InvalidDataException("歌曲評分資料格式不正確。");
        Document = document;
        Midi = new double[document.PitchHz.Length]; Array.Fill(Midi, double.NaN);
        int frameCount = Math.Min(Midi.Length, FrameLimit(document.Duration));
        Notes = NoteSegmentation.Find(document, frameCount, cancellation);
        foreach (var note in Notes)
        {
            int start = FrameAt(note.Start);
            int end = FrameLimit(note.End);
            Array.Fill(Midi, note.Midi, start, end - start);
        }
        if (VoicedFrames < 50 || Notes.Length == 0) throw new InvalidDataException("無法辨識足夠的主唱旋律，這首歌目前無法評分。");
    }

    internal static int FrameAt(double time) => (int)Math.Floor(time / PitchDetector.FramePeriod + 1e-8);
    internal static int FrameLimit(double time) => (int)Math.Ceiling(time / PitchDetector.FramePeriod - 1e-8);

    public static ReferenceTrack FromFrames(PitchFrame[] frames, double duration, float[]? loudnessDb = null, CancellationToken cancellation = default) => new(new(1, "swift-f0-0.3.0", duration,
        frames.Select(f => (float)f.Hertz).ToArray(), frames.Select(f => (float)f.Confidence).ToArray(), loudnessDb), cancellation);
    public static ReferenceTrack Read(string path, CancellationToken cancellation = default)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("評分資料過大。");
        return new(JsonSerializer.Deserialize<ReferenceDocument>(File.ReadAllText(path)) ?? throw new InvalidDataException("評分資料為空。"), cancellation);
    }
    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Document));
}
