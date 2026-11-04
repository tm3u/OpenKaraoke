namespace OpenKaraoke.Scoring;

public sealed record ScoreResult(double? Pitch, bool Complete, int CapturedFrames);
public readonly record struct SungPoint(double Time, double Midi, int? Key = null);

public sealed class ScoreSession(ReferenceTrack reference, ScoringDifficulty difficulty = ScoringDifficulty.Casual)
{
    private readonly SongScoreSession _session = new(reference, difficulty, analysisLag: 0);
    public ReferenceTrack Reference => reference;
    public void Add(PitchFrame frame, int key) => _session.Add(frame, key);
    public SungPoint[] Trace(double from = double.NegativeInfinity) => _session.Trace(from: from);
    public SungNote[] Notes() => _session.Notes();
    public ScoreResult Result(double position, bool complete = false) => _session.Result(position, complete);
}
