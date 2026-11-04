using System.Collections;

namespace OpenKaraoke.Scoring;

public sealed class SongScoreSession
{
    private sealed class Input(int frameCount)
    {
        public BitArray Received { get; } = new(frameCount);
        public List<SungPoint> Trace { get; } = [];
    }

    private struct FrameMeasurement
    {
        public int Samples;
        public double AbsoluteError, SignedOffset;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Input> _sources = [];
    private readonly List<SungNote> _notes = [];
    private readonly BitArray _received;
    private readonly FrameMeasurement[] _frames;
    private readonly double _analysisLag;
    private int _captured;
    private double _analysisThrough, _hitFrames, _referenceFrames;
    private bool _finished;

    public ReferenceTrack Reference { get; }
    public ScoringDifficulty Difficulty { get; }

    public SongScoreSession(ReferenceTrack reference, ScoringDifficulty difficulty = ScoringDifficulty.Casual)
        : this(reference, difficulty, analysisLag: .25) { }

    internal SongScoreSession(ReferenceTrack reference, ScoringDifficulty difficulty, double analysisLag)
    {
        Reference = reference;
        Difficulty = difficulty;
        _analysisLag = analysisLag;
        _frames = new FrameMeasurement[reference.Midi.Length];
        _received = new BitArray(_frames.Length);
    }

    public void Add(PitchFrame frame, int key, string source = "local")
    {
        lock (_gate)
        {
            if (_finished || !double.IsFinite(frame.Time) || frame.Time < 0 || frame.Time >= Reference.Document.Duration) return;
            _analysisThrough = Math.Max(_analysisThrough, frame.Time);
            int index = ReferenceTrack.FrameAt(frame.Time);
            if (index >= _frames.Length) return;
            if (!_sources.TryGetValue(source, out var input)) _sources[source] = input = new(_frames.Length);
            if (input.Received[index]) return;
            input.Received[index] = true;
            if (!_received[index])
            {
                _received[index] = true;
                _captured++;
            }
            if (!frame.Voiced) return;

            double midi = frame.Midi;
            input.Trace.Add(new(frame.Time, midi, key));
            double target = Reference.Midi[index];
            if (!double.IsFinite(target)) return;
            double offset = PitchTolerance.Offset(midi, target + key);
            ref var measurement = ref _frames[index];
            measurement.Samples++;
            measurement.AbsoluteError += Math.Abs(offset);
            measurement.SignedOffset += offset;
        }
    }

    private void FinalizeNotes()
    {
        double until = _finished ? double.PositiveInfinity : _analysisThrough - _analysisLag;
        double tolerance = PitchTolerance.For(Difficulty).Perfect;
        while (_notes.Count < Reference.Notes.Length && Reference.Notes[_notes.Count].End <= until)
        {
            var note = Reference.Notes[_notes.Count];
            int start = ReferenceTrack.FrameAt(note.Start), end = ReferenceTrack.FrameLimit(note.End);
            double lastFrame = note.End / PitchDetector.FramePeriod - (end - 1);
            if (lastFrame >= 1 - 1e-8) lastFrame = 1;
            _referenceFrames += end - start - 1 + lastFrame;

            int samples = 0;
            double absoluteError = 0, signedOffset = 0;
            for (int i = start; i < end; i++)
            {
                var measurement = _frames[i];
                samples += measurement.Samples;
                absoluteError += measurement.AbsoluteError;
                signedOffset += measurement.SignedOffset;
                if (measurement.Samples > 0 && measurement.AbsoluteError / measurement.Samples <= tolerance)
                    _hitFrames += i == end - 1 ? lastFrame : 1;
            }
            _notes.Add(PitchJudgement.Average(note, samples, absoluteError, signedOffset, Difficulty)
                ?? new(note.Start, note.End, note.Midi, false));
        }
    }

    public ScoreResult Result(double position, bool complete = false)
    {
        lock (_gate)
        {
            if (complete) _finished = true;
            FinalizeNotes();
            double? score = complete && _captured >= 10 && _referenceFrames > 0
                ? Math.Round(100 * _hitFrames / _referenceFrames, MidpointRounding.AwayFromZero)
                : null;
            return new(score, complete, _captured);
        }
    }

    public SungPoint[] Trace(string source = "local", double from = double.NegativeInfinity)
    {
        lock (_gate) return _sources.GetValueOrDefault(source)?.Trace.Where(p => p.Time >= from).ToArray() ?? [];
    }

    public SungPoint[][] Traces(double from = double.NegativeInfinity)
    {
        lock (_gate) return _sources.Values.Select(s => s.Trace.Where(p => p.Time >= from).ToArray()).Where(t => t.Length > 0).ToArray();
    }

    public SungNote[] Notes()
    {
        lock (_gate)
        {
            FinalizeNotes();
            return _notes.ToArray();
        }
    }
}
