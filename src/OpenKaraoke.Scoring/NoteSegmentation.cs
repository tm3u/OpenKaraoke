namespace OpenKaraoke.Scoring;

internal static class NoteSegmentation
{
    private const double NoteChangeCost = .060 / PitchDetector.FramePeriod;
    private const int LoudnessWindow = 4;

    public static MelodyNote[] Find(ReferenceDocument document, int frameCount, CancellationToken cancellation)
    {
        var pitches = new double[frameCount];
        var candidates = new SortedSet<double>();
        for (int i = 0; i < frameCount; i++)
        {
            if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
            float hz = document.PitchHz[i];
            if (hz < 46.875) continue;
            pitches[i] = 69 + 12 * Math.Log2(hz / 440d);
            candidates.Add(Math.Floor(pitches[i] * 100 + .5) / 100);
        }
        if (candidates.Count == 0) return [];

        var notePitches = candidates.ToArray();
        var costs = new double[notePitches.Length];
        Array.Fill(costs, double.PositiveInfinity);
        var starts = new int[notePitches.Length];
        var previous = new int[frameCount + 1];
        var selected = new int[frameCount + 1];
        double best = 0;
        for (int frame = 0; frame < frameCount; frame++)
        {
            if ((frame & 255) == 0) cancellation.ThrowIfCancellationRequested();
            double confidence = document.PitchHz[frame] >= 46.875 ? document.Confidence[frame] : 0;
            previous[frame + 1] = frame;
            selected[frame + 1] = -1;
            if (confidence == 0)
            {
                if (frame > 0 && document.Confidence[frame - 1] > 0)
                    Array.Fill(costs, double.PositiveInfinity);
                continue;
            }
            double probability = Math.Clamp(confidence, .01, .99);
            double frameCost = -Math.Log(probability / (1 - probability)) + QuietPenalty(document.LoudnessDb, frame, frameCount);
            double startCost = best + NoteChangeCost;
            for (int note = 0; note < costs.Length; note++)
            {
                if (startCost < costs[note])
                {
                    starts[note] = frame;
                    costs[note] = startCost;
                }
                costs[note] += confidence * Math.Min(Math.Abs(pitches[frame] - notePitches[note]), 2) + frameCost;
                if (costs[note] >= best) continue;
                best = costs[note];
                previous[frame + 1] = starts[note];
                selected[frame + 1] = note;
            }
        }

        var notes = new List<MelodyNote>();
        for (int end = frameCount; end > 0; end = previous[end])
        {
            int note = selected[end];
            if (note >= 0)
                notes.Add(new(previous[end] * PitchDetector.FramePeriod,
                    Math.Min(document.Duration, end * PitchDetector.FramePeriod), notePitches[note]));
        }
        notes.Reverse();
        return notes.ToArray();
    }

    private static double QuietPenalty(float[]? levels, int frame, int frameCount)
    {
        if (levels == null) return 0;
        float left = levels[frame], right = left;
        for (int offset = 1; offset <= LoudnessWindow; offset++)
        {
            left = Math.Max(left, levels[Math.Max(0, frame - offset)]);
            right = Math.Max(right, levels[Math.Min(frameCount - 1, frame + offset)]);
        }
        return Math.Max(0, Math.Min(left, right) - (double)levels[frame] - 3.010299956639812);
    }
}
