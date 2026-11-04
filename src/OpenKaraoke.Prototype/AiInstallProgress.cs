using System.Globalization;
using System.IO;

namespace OpenKaraoke.Prototype;

internal sealed class AiInstallProgress(IProgress<CreationProgress>? progress)
{
    private readonly object _gate=new();
    private string _package="AI 套件";
    public void Report(string line)
    {
        lock(_gate)
        {
            line=line.Trim();
            if(line.StartsWith("Collecting ",StringComparison.Ordinal))
            {
                _package=line[11..].Split([' ','<','>','=','[','('],StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??"AI 套件";
                progress?.Report(new("setup","準備套件 · "+_package));
            }
            else if(line.StartsWith("Downloading ",StringComparison.Ordinal)||line.StartsWith("Using cached ",StringComparison.Ordinal))
            {
                string file=Path.GetFileName(line[(line.StartsWith("Downloading ")?12:13)..].Split(' ')[0].Split('?','#')[0]);
                int version=file.IndexOf('-');
                if(version>0&&file.EndsWith(".whl",StringComparison.OrdinalIgnoreCase))_package=file[..version];
                progress?.Report(new("setup",(line.StartsWith("Downloading ")?"下載套件 · ":"讀取快取 · ")+_package));
            }
            else if(line.StartsWith("Progress ",StringComparison.Ordinal))
            {
                var parts=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(parts.Length!=4||parts[2]!="of"||!long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out long received)
                    ||!long.TryParse(parts[3],NumberStyles.None,CultureInfo.InvariantCulture,out long total))return;
                progress?.Report(new("setup","下載套件 · "+_package,total>0?Math.Clamp(100.0*received/total,0,100):null));
            }
            else if(line.StartsWith("Installing collected packages:",StringComparison.Ordinal))
                progress?.Report(new("setup","安裝 AI 套件"));
            else if(line.StartsWith("Successfully installed",StringComparison.Ordinal))
                progress?.Report(new("setup","檢查 AI 套件"));
        }
    }
}
