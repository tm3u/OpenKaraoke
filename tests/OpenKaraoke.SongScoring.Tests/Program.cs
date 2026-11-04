using System.Text.Json;
using OpenKaraoke.Scoring;

string root=Path.GetFullPath(args.FirstOrDefault()??".");var checks=new List<object>();
void Check(string name,bool passed,object? evidence=null){checks.Add(new{Name=name,Passed=passed,Evidence=evidence});Console.WriteLine($"{(passed?"PASS":"FAIL")} {name}");if(!passed)throw new Exception(name);}
var frames=Enumerable.Range(0,400).Select(i=>new PitchFrame(i*.016,i is >=20 and <120 or >=150 and <250 or >=280 and <380?440:0,i is >=20 and <120 or >=150 and <250 or >=280 and <380?1:0)).ToArray();
var reference=ReferenceTrack.FromFrames(frames,6.4);
var folder=Path.Combine(root,"artifacts","song-scoring-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(folder);
try
{
 var solo=new ScoreSession(reference);var group=new SongScoreSession(reference);
 foreach(var frame in frames){solo.Add(frame,0);group.Add(frame,0);}
 Check("單一來源與原本評分一致",solo.Result(6.4,true)==group.Result(6.4,true));
 group=new(reference);foreach(var frame in frames){group.Add(frame with{Confidence=frame.Time<3.5?frame.Confidence:0},0,"a");group.Add(frame with{Confidence=frame.Time>=3.5?frame.Confidence:0},0,"b");}
 var relay=group.Result(6.4,true);Check("接唱接續整首歌而非平均各自的整首分數",relay.Pitch==100,relay);
 group=new(reference);foreach(var frame in frames)for(int i=0;i<4;i++)group.Add(frame,0,i.ToString());
 var chorus=group.Result(6.4,true);Check("四人同時正確演唱不重複加分",chorus.Pitch==100&&chorus.CapturedFrames==400,chorus);
 group=new(reference);foreach(var frame in frames){group.Add(frame,0,"a");group.Add(frame with{Hertz=frame.Hertz*Math.Pow(2,4d/12)},0,"b");}
 var mixed=group.Result(6.4,true);Check("共同演唱依時間合併各路有效音準",mixed.Pitch==0,mixed);
 group=new(reference);foreach(var frame in frames){group.Add(frame with{Hertz=frame.Hertz*Math.Pow(2,1d/12)},0,"a");group.Add(frame with{Hertz=frame.Hertz*Math.Pow(2,-1d/12)},0,"b");}
 var opposite=group.Result(6.4,true);Check("正負走音不會因平均頻率而變成滿分",opposite.Pitch==0,opposite);
 var difficultyScores=Enum.GetValues<ScoringDifficulty>().Select(difficulty=>
 {
  var session=new ScoreSession(reference,difficulty);foreach(var frame in frames)session.Add(frame with{Hertz=frame.Hertz*Math.Pow(2,(frame.Time<2?.3:frame.Time<4?.6:1.2)/12)},0);
  return session.Result(6.4,true).Pitch;
 }).ToArray();
 Check("休閒預設較寬鬆，進階與專業依序提高音準要求",new VoiceSettings().Difficulty==ScoringDifficulty.Casual&&difficultyScores[0]==67&&difficultyScores[0]>difficultyScores[1]&&difficultyScores[1]>difficultyScores[2],difficultyScores);
 var firstNote=reference.Notes[0];var slight=Enumerable.Range(0,30).Select(i=>new SungPoint(firstNote.Start+i*.016,firstNote.Midi+.6)).ToArray();
 Check("紅綠判定使用相同難度容忍值",PitchJudgement.Judge(firstNote,slight,ScoringDifficulty.Casual,6.4)?.Correct==true&&PitchJudgement.Judge(firstNote,slight,ScoringDifficulty.Advanced,6.4)?.Correct==false&&PitchJudgement.Judge(firstNote,slight,ScoringDifficulty.Professional,6.4)?.Correct==false);
 var changedKey=slight.Select(p=>p with{Midi=p.Midi+14,Key=2}).ToArray();
 Check("評分記錄依當時升降調並保留八度等價",PitchJudgement.Judge(firstNote,changedKey,ScoringDifficulty.Casual,6.4)?.Correct==true);
 var averageSession=new ScoreSession(reference);int samples=(int)Math.Round((firstNote.End-firstNote.Start)/.016);
 for(int i=0;i<samples;i++)averageSession.Add(new(firstNote.Start+i*.016,440*Math.Pow(2,(i<samples*.6?0:2)/12d),1),0);
 Check("音符未完成時不公布紅綠結果",averageSession.Notes().Length==0&&PitchJudgement.Judge(firstNote,slight,ScoringDifficulty.Casual,firstNote.End-.001)==null);
 averageSession.Add(new(firstNote.End,0,0),0);var averaged=averageSession.Notes().Single();
 Check("音符依全部樣本平均判斷，不採多數決",!averaged.Correct&&Math.Abs(averaged.Midi-firstNote.Midi-.8)<.001,averaged);
 for(int i=0;i<50;i++)averageSession.Add(new(firstNote.Start+i*.016,440,1),0);
 Check("已公布的音符結果固定，延後資料不再改色",averageSession.Notes().Single()==averaged);
 var opposed=slight.Select((point,i)=>point with{Midi=firstNote.Midi+(i<15?2:-2)}).ToArray();
 Check("平均音準採絕對偏差，正負走音不相互抵銷",PitchJudgement.Judge(firstNote,opposed,ScoringDifficulty.Casual,6.4)?.Correct==false);
 var tailFrames=Enumerable.Range(0,400).Select(i=>new PitchFrame(i*.016,440,1)).ToArray();var tailReference=ReferenceTrack.FromFrames(tailFrames,6.39);var tailSession=new SongScoreSession(tailReference);
 foreach(var frame in tailFrames)tailSession.Add(frame,0);tailSession.Result(6.39,true);
 Check("歌曲結束時包含不足一個分析格的最後音條",tailSession.Notes().Length==1&&tailSession.Notes()[0].Correct);
 group=new(reference);foreach(var frame in frames)for(int i=0;i<4;i++)group.Add(frame with{Confidence=i==0?frame.Confidence:0},0,i.ToString());
 Check("未演唱的手機不拉低其他人的分數",group.Result(6.4,true).Pitch==100);
 group=new(reference);foreach(var frame in frames){group.Add(frame with{Confidence=0},0,"a");group.Add(frame with{Confidence=0},0,"b");}
 Check("所有人漏唱仍為零分",group.Result(6.4,true).Pitch==0);
 group=new(reference);foreach(var frame in frames.Where(f=>f.Time<4))group.Add(frame,0,"a");
 Check("沒有任何人接唱的段落仍扣分",group.Result(6.4,true).Pitch<80);
 var roundFrames=Enumerable.Range(0,400).Select(i=>new PitchFrame(i*.016,440*Math.Pow(2,(i/50)*2d/12),1)).ToArray();
 var roundReference=ReferenceTrack.FromFrames(roundFrames,6.4);var rounded=new SongScoreSession(roundReference);
 foreach(var frame in roundFrames)rounded.Add(frame.Time<.8?frame:frame with{Hertz=frame.Hertz*Math.Pow(2,4d/12)},0);
 var roundedResult=rounded.Result(6.4,true);
 Check("唱準時間比例為 12.5% 時四捨五入為 13 分",roundReference.Notes.Length==8&&rounded.Notes().Count(n=>n.Correct)==1&&roundedResult.Pitch==13,roundedResult);
 var durationFrames=Enumerable.Range(0,400).Select(i=>new PitchFrame(i*.016,i<50?440:660,1)).ToArray();
 var durationReference=ReferenceTrack.FromFrames(durationFrames,6.4);var durationScore=new SongScoreSession(durationReference);
 foreach(var frame in durationFrames)durationScore.Add(frame.Time<.8?frame:frame with{Hertz=frame.Hertz*Math.Pow(2,4d/12)},0);
 Check("只唱準 0.8 秒短音，不等於唱準半首歌",durationReference.Notes.Length==2&&durationScore.Result(6.4,true).Pitch==13);
 var partial=new SongScoreSession(tailReference);
 foreach(var frame in tailFrames)partial.Add(frame.Time<3.2?frame:frame with{Confidence=0},0);
 Check("只唱準一個長音的前半段取得約一半分數",partial.Result(6.39,true).Pitch==50);
 var duplicates=new SongScoreSession(tailReference);
 duplicates.Add(tailFrames[0],0);
 for(int i=0;i<800;i++)duplicates.Add(new(.004,520,1),0);
 foreach(var frame in tailFrames.Skip(1))duplicates.Add(frame,0);
 Check("同一來源重複傳入錯音不會扭曲已收到的時間格",duplicates.Result(6.39,true)==new ScoreResult(100,true,400)&&duplicates.Trace().Length==400);

 var stable=tailFrames.Select((f,i)=>i<200?f:f with{Hertz=587.3295358,Confidence=.90}).ToArray();
 var steadyNotes=ReferenceTrack.FromFrames(stable,6.4).Notes;
 Check("穩定的 0.90 信心音高保留完整後段音條",steadyNotes.Length==2&&Math.Abs(steadyNotes[1].Start-3.2)<1e-8&&Math.Abs(steadyNotes[1].End-6.4)<1e-8);
 var vibrato=tailFrames.Select((f,i)=>i<200?f:f with{Hertz=440*Math.Pow(2,(i/3%2==0?.8:-.8)/12)}).ToArray();
 var vibratoNotes=ReferenceTrack.FromFrames(vibrato,6.4).Notes;
 Check("短週期顫音不被切成斷續小片段",vibratoNotes.Length==1&&Math.Abs(vibratoNotes[0].Midi-69)<.01&&Math.Abs(vibratoNotes[0].End-6.4)<1e-8);
 var quick=tailFrames.Select((f,i)=>i<200?f:f with{Hertz=440*Math.Pow(2,(i/4%2==0?5:10)/12d)}).ToArray();
 var shortReference=ReferenceTrack.FromFrames(quick,6.4);
 var shortScore=new ScoreSession(shortReference);
 foreach(var f in quick)shortScore.Add(f,0);
 Check("64ms 快速交替音符保有正確音高與可判分結果",shortReference.Notes.Length==51&&shortScore.Result(6.4,true).Pitch==100&&shortScore.Notes().Count(n=>n.Correct)>=50);
 var unpublished=new SongScoreSession(reference);foreach(var frame in frames)unpublished.Add(frame,0);
 Check("歌曲進行中只累計音條，不公布成績",unpublished.Result(6.4).Pitch==null&&unpublished.Result(6.4).CapturedFrames==400);
 var leakageFrames=Enumerable.Range(0,400).Select(i=>new PitchFrame(i*.016,440,1)).ToArray();
 float[] vocalLevels=Enumerable.Range(0,400).Select(i=>i<200?.0004f:.05f).ToArray();
 var filtered=VocalActivity.Filter(leakageFrames,vocalLevels,Enumerable.Repeat(.1f,400).ToArray());
 var filteredReference=ReferenceTrack.FromFrames(filtered,6.4);
 Check("人聲軌微弱樂器殘留不建立音條，保留真正收音區間",filtered.Take(190).All(f=>!f.Voiced)&&filtered.Skip(210).All(f=>f.Voiced)&&filteredReference.Notes.All(n=>n.Start>=3.04)&&filteredReference.Notes[^1].End==6.4);
 var soft=VocalActivity.Filter(leakageFrames,Enumerable.Repeat(.004f,400).ToArray(),Enumerable.Repeat(.05f,400).ToArray());
 Check("微弱但高於伴奏殘留比例的持續人聲不被清除",soft.All(f=>f.Voiced));
 group=new(reference);foreach(var frame in frames){group.Add(frame,0,"a");group.Add(frame with{Time=frame.Time+.128},0,"b");}
 var late=group.Result(6.4,true);Check("合唱只依當下音準，不額外比較各路起音",late.Pitch==100,late);
 group=new(reference);foreach(var frame in frames){group.Add(frame with{Hertz=frame.Hertz*Math.Pow(2,3d/12)},3,"a");group.Add(frame with{Hertz=frame.Hertz*Math.Pow(2,3d/12)},3,"b");}
 Check("共同評分保留升降調基準",group.Result(6.4,true).Pitch==100);
 var frozen=group.Result(6.4,true);group.Add(new(1,460,1),0,"c");Check("唱完的共同成績不受後來輸入改寫",group.Result(6.4,true)==frozen);
 Check("未收到收音時不捏造分數",new SongScoreSession(reference).Result(6.4,true).Pitch==null);
 Check("來源的音高軌跡維持分開",group.Traces().Length==2&&group.Trace("c").Length==0);
 var longFrames=Enumerable.Range(0,11250).Select(i=>new PitchFrame(i*.016,440*Math.Pow(2,(i/25%4)*2d/12),1)).ToArray();var longReference=ReferenceTrack.FromFrames(longFrames,180);var longSong=new SongScoreSession(longReference);
 foreach(var frame in longFrames)for(int i=0;i<4;i++)longSong.Add(frame,0,i.ToString());longSong.Result(179);
 Check("保留完整三分鐘演唱資料，不再只保留最後二十五秒",longSong.Trace("0").Length==11250&&longSong.Trace("0")[0].Time==0&&longSong.Traces(175).All(t=>t.Length<400));
 var history=PitchJudgement.History(longReference,longSong.Traces(),ScoringDifficulty.Casual,180);
 Check("全曲評分記錄包含第一個及最後一個音符",history.Min(n=>n.Start)==0&&history.Max(n=>n.End)>179&&history.All(n=>n.Correct));
 var timings=new List<double>();for(int i=0;i<30;i++){long start=System.Diagnostics.Stopwatch.GetTimestamp();longSong.Result(179);timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);}timings.Sort();
 Check("三分鐘四路成績聚合可在 50ms 更新週期內完成",timings[28]<50,new{MedianMs=timings[15],P95Ms=timings[28],Scope="僅成績聚合，不含音高推論或傳輸"});
 await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(folder);
}
catch(Exception e){await File.WriteAllTextAsync(Path.Combine(folder,"report.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=e.ToString()},new JsonSerializerOptions{WriteIndented=true}));throw;}
