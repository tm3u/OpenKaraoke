namespace OpenKaraoke.Scoring;

public enum ScoringDifficulty { Casual, Advanced, Professional }

public readonly record struct PitchTolerance(double Perfect)
{
    public static PitchTolerance For(ScoringDifficulty difficulty) => difficulty switch
    {
        ScoringDifficulty.Advanced => new(.4),
        ScoringDifficulty.Professional => new(.2),
        _ => new(.75)
    };
    public static double Offset(double sung, double target) => sung - target - 12 * Math.Round((sung - target) / 12);
}

public readonly record struct SungNote(double Start, double End, double Midi, bool Correct);

public static class PitchJudgement
{
    public static SungNote? Judge(MelodyNote note, SungPoint[] trace, ScoringDifficulty difficulty, double until, int key = 0, ReferenceTrack? reference = null)
    {
        if(until<note.End)return null;
        double Offset(SungPoint point)
        {
            int index=ReferenceTrack.FrameAt(point.Time);
            double target=reference!=null&&index>=0&&index<reference.Midi.Length&&double.IsFinite(reference.Midi[index])?reference.Midi[index]:note.Midi;
            return PitchTolerance.Offset(point.Midi,target+(point.Key??key));
        }
        var offsets=trace.Where(p=>p.Time>=note.Start&&p.Time<note.End&&double.IsFinite(p.Midi)).Select(Offset).ToArray();
        return Average(note,offsets.Length,offsets.Sum(Math.Abs),offsets.Sum(),difficulty);
    }
    public static SungNote? Average(MelodyNote note,int count,double absoluteError,double signedOffset,ScoringDifficulty difficulty)
    {
        int frames = ReferenceTrack.FrameLimit(note.End) - ReferenceTrack.FrameAt(note.Start);
        if (count < Math.Clamp(frames, 1, 5)) return null;
        bool correct=absoluteError/count<=PitchTolerance.For(difficulty).Perfect;
        return new(note.Start,note.End,note.Midi+(correct?0:signedOffset/count),correct);
    }
    public static SungNote[] History(ReferenceTrack reference, SungPoint[][] traces, ScoringDifficulty difficulty, double until)
    {
        if(until>=reference.Document.Duration)until=double.PositiveInfinity;
        var result=new List<SungNote>();var starts=reference.Notes.Select(n=>n.Start).ToArray();
        foreach(var trace in traces)
        {
            var groups=new Dictionary<int,List<SungPoint>>();
            foreach(var point in trace)
            {
                if(!double.IsFinite(point.Time)||point.Time>until)continue;
                int index=Array.BinarySearch(starts,point.Time);if(index<0)index=~index-1;
                if(index<0||point.Time>=reference.Notes[index].End)continue;
                if(!groups.TryGetValue(index,out var group))groups[index]=group=[];
                group.Add(point);
            }
            foreach(var group in groups)
                if(Judge(reference.Notes[group.Key],group.Value.ToArray(),difficulty,until,reference:reference) is {} note)result.Add(note);
        }
        return result.OrderBy(n=>n.Start).ToArray();
    }
}
