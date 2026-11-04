namespace OpenKaraoke.Scoring;

public static class VocalActivity
{
    public static PitchFrame[] Filter(PitchFrame[] frames, float[] vocalRms, float[]? mixRms = null)
    {
        if (vocalRms.Length < frames.Length || mixRms != null && mixRms.Length < frames.Length)
            throw new ArgumentException("人聲活動資料長度不完整。");
        if (vocalRms.Any(v => !float.IsFinite(v) || v < 0) || mixRms?.Any(v => !float.IsFinite(v) || v < 0) == true)
            throw new ArgumentException("人聲活動資料含有無效音量。");
        var levels = vocalRms.Take(frames.Length).Order().ToArray();
        if (levels.Length == 0) return frames;
        double loud = levels[(int)((levels.Length - 1) * .9)], quiet = levels[(int)((levels.Length - 1) * .15)];
        double threshold = Math.Max(.00001, loud * .015);
        if (quiet < loud * .1) threshold = Math.Max(threshold, quiet * 3);
        var result = new PitchFrame[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            int from = Math.Max(0, i - 2), to = Math.Min(frames.Length - 1, i + 2);
            double vocal = 0, mix = 0;
            for (int j = from; j <= to; j++) { vocal += vocalRms[j] * vocalRms[j]; if (mixRms != null) mix += mixRms[j] * mixRms[j]; }
            vocal = Math.Sqrt(vocal / (to - from + 1)); mix = Math.Sqrt(mix / (to - from + 1));
            bool active = vocal >= threshold && (mixRms == null || vocal >= mix * .04);
            result[i] = active ? frames[i] : frames[i] with { Confidence = 0 };
        }
        return result;
    }
}
