using System.Text.Json;

namespace OpenKaraoke.Scoring;

public sealed record VoiceSettings(string DeviceId = "default", int DelayMilliseconds = 0, bool ScoringEnabled = false,
    bool PhoneEnabled=false,string PhoneAddress="",int LocalVolume=100,int EchoLevel=0,
    ScoringDifficulty Difficulty=ScoringDifficulty.Casual,bool AutoPlayNext=false,int PhoneMonitorVolume=100,
    bool SubtitlesEnabled=true,int SubtitleFontPercent=100)
{
    public static VoiceSettings Load(string path)
    {
        try
        {
            var value=JsonSerializer.Deserialize<VoiceSettings>(File.ReadAllText(path));if(value==null)return new();
            return value with { Difficulty=Enum.IsDefined(value.Difficulty)?value.Difficulty:ScoringDifficulty.Casual, DelayMilliseconds = Math.Clamp(value.DelayMilliseconds, 0, 1000), DeviceId = value.DeviceId ?? "default",PhoneEnabled=value.DeviceId=="phone",PhoneAddress=value.PhoneAddress??"",LocalVolume=Math.Clamp(value.LocalVolume,0,100),EchoLevel=Math.Clamp(value.EchoLevel,-3,3),PhoneMonitorVolume=Math.Clamp(value.PhoneMonitorVolume,0,100),SubtitleFontPercent=Math.Clamp(value.SubtitleFontPercent,75,125) };
        }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }
    public void Save(string path)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this with{PhoneEnabled=DeviceId=="phone"})); File.Move(temp, path, true);
    }
}
