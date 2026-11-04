using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private static void VerifyPitchGuide(Action<string,bool,object?> check,string folder)
    {
        MelodyNote[] melody=[new(1,2,60),new(5,6,72),new(12,13,67),new(20,21,84)];
        var frames=Enumerable.Range(0,1500).Select(i=>
        {
            double time=i*.016;var note=melody.FirstOrDefault(n=>time>=n.Start&&time<n.End);
            return new PitchFrame(time,note.End==0?0:440*Math.Pow(2,(note.Midi-69)/12),note.End==0?0:1);
        }).ToArray();
        var referenceTrack=ReferenceTrack.FromFrames(frames,24);
        var guide=new PitchGuide{Reference=referenceTrack,Width=800,Height=112,Position=2};
        Color reference=Color.FromRgb(143,153,167),green=Color.FromRgb(100,221,142),red=Color.FromRgb(240,120,131);
        IEnumerable<Drawing> Flatten(Drawing drawing)=>drawing is DrawingGroup group?group.Children.SelectMany(Flatten):[drawing];
        GeometryDrawing[] Draw(string? file=null)
        {
            guide.Measure(new Size(800,112));guide.Arrange(new Rect(0,0,800,112));guide.InvalidateVisual();guide.UpdateLayout();
            var bitmap=new RenderTargetBitmap(800,112,96,96,PixelFormats.Pbgra32);bitmap.Render(guide);
            if(file!=null){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(folder,file));encoder.Save(stream);}
            return Flatten(VisualTreeHelper.GetDrawing(guide)).OfType<GeometryDrawing>().ToArray();
        }
        static GeometryDrawing[] Bars(GeometryDrawing[] drawings,Color color)=>drawings.Where(d=>d.Brush is SolidColorBrush brush&&brush.Color==color).ToArray();
        static bool Same(double a,double b)=>Math.Abs(a-b)<.001;
        SongScoreSession Feed(double midi,double until=2,int key=0)
        {
            var session=new SongScoreSession(referenceTrack);
            foreach(int i in Enumerable.Range(0,50)){double time=1.008+i*.016;if(time>until)break;session.Add(new(time,440*Math.Pow(2,(midi-69)/12),1),key);}
            session.Add(new(until+.27,0,0),key);guide.Records=session.Notes();return session;
        }
        var neutral=Draw("pitch-guide-neutral.png");var target=Bars(neutral,reference)[0].Geometry.Bounds;
        var lines=neutral.Where(d=>d.Pen!=null).Select(d=>(LineGeometry)d.Geometry).ToArray();
        check("背景顯示五條水平參考線並保留固定時間游標",lines.Count(l=>Same(l.StartPoint.Y,l.EndPoint.Y))==5&&lines.Count(l=>Same(l.StartPoint.X,l.EndPoint.X))==1,null);
        check("沒有演唱資料時不捏造紅綠結果",Bars(neutral,green).Length==0&&Bars(neutral,red).Length==0,null);
        guide.Position=1.2;Feed(60.2,1.2);var incomplete=Draw("pitch-guide-pending.png");
        check("音符尚未唱完只保留參考色，不即時變紅變綠",Bars(incomplete,green).Length==0&&Bars(incomplete,red).Length==0,null);
        guide.Position=2;Feed(60.2);var correct=Draw("pitch-guide-correct.png");
        check("整條音符評估後顯示等寬同位置的綠條",Bars(correct,green).Single().Geometry.Bounds==target,null);
        Feed(62);var wrong=Draw("pitch-guide-wrong.png");var wrongBar=Bars(wrong,red).Single().Geometry.Bounds;
        check("平均唱高的紅條保留時長並顯示較高音位",Same(wrongBar.Width,target.Width)&&Same(wrongBar.X,target.X)&&wrongBar.Y<target.Y,null);
        Feed(58);check("平均唱低的紅條顯示較低音位",Bars(Draw(),red).Single().Geometry.Bounds.Y>target.Y,null);
        Feed(72);check("不同八度唱準仍顯示綠色",Bars(Draw(),green).Single().Geometry.Bounds==target,null);
        guide.Key=2;Feed(62,key:2);check("音符結果依演唱當時的升降調判定",Bars(Draw(),green).Single().Geometry.Bounds==target,null);guide.Key=0;
        var multiple=new SongScoreSession(referenceTrack);
        for(int i=0;i<50;i++){double time=1.008+i*.016;multiple.Add(new(time,440*Math.Pow(2,(60-69)/12d),1),0,"a");multiple.Add(new(time,440*Math.Pow(2,(62-69)/12d),1),0,"b");}
        multiple.Add(new(2.3,0,0),0,"a");multiple.Add(new(2.3,0,0),0,"b");guide.Records=multiple.Notes();var multi=Draw("pitch-guide-multiple.png");
        check("多人音準合併後每條原始音符只公布一條結果",Bars(multi,green).Length==0&&Bars(multi,red).Length==1,null);
        check("評分區沒有即時音高曲線與圖例",multi.Count(d=>d.Pen!=null)==6&&multi.All(d=>d.Geometry is RectangleGeometry or LineGeometry),null);
        guide.Records=[];guide.Position=7;var before=Bars(Draw(),reference).Last().Geometry.Bounds;guide.Position=14;var after=Bars(Draw(),reference).First().Geometry.Bounds;
        check("旋律保持跑馬燈且垂直音域不跳動",after.X<before.X&&Same(after.Y,before.Y)&&Same(after.Width,before.Width),null);
        foreach(double position in new[]{-3d,-1,0,1,7})
        {
            guide.Position=position;var cursor=Draw().Where(d=>d.Pen!=null).Select(d=>(LineGeometry)d.Geometry).Single(l=>Same(l.StartPoint.X,l.EndPoint.X));
            check($"播放位置 {position} 秒時唱歌線固定於同一位置",Same(cursor.StartPoint.X,240),cursor.StartPoint.X);
        }
        guide.Overview=true;guide.Duration=24;guide.Reference=null;guide.OriginalNotes=melody;guide.Records=[new(0,1,60,true),new(23,24,72,false)];var overview=Draw("pitch-guide-overview.png");
        check("全曲記錄保留灰色原始音符與紅綠結果",overview.All(d=>d.Pen==null)&&Bars(overview,reference).Length==melody.Length&&Bars(overview,green).Length==1&&Bars(overview,red).Length==1,null);
        guide.FollowPlayback=true;guide.Position=3;var followStart=Draw();var first=Bars(followStart,reference).First().Geometry.Bounds;
        guide.Position=4;var followLater=Draw();var moved=Bars(followLater,reference).First().Geometry.Bounds;
        check("回顧旋律固定每秒 80 像素並依播放時間捲動，音域不跳動",Same(first.Width,80)&&Same(moved.Width,80)&&Same(first.Y,moved.Y)&&Same(first.X-moved.X,80),new{Before=first,After=moved});
        guide.Position=24;var followEnd=Draw();var last=Bars(followEnd,reference).Last().Geometry.Bounds;
        check("回顧可由同一時間軸看到最後音條與末端游標",last.X>=0&&last.Right<=800&&followEnd.Any(d=>d.Pen!=null&&d.Geometry is LineGeometry l&&Same(l.StartPoint.X,800)&&Same(l.EndPoint.X,800)),null);
        guide.FollowPlayback=false;
        guide.Overview=false;guide.Records=[];check("清除歌曲後不殘留音符",Draw().Length==0,null);
    }
}
