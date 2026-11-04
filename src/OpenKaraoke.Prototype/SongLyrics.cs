using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenKaraoke.Prototype;

public sealed record SongLyrics(string Filename, string Content, string Mode = "")
{
    private const int MaximumLength = 2 * 1024 * 1024;
    private const string ClockPattern = @"\d{1,3}:[0-5]\d(?:\.\d{1,3})?";
    private sealed record Word(string Text, double Start, double? End);
    private sealed record Cue(string Text, double Start, double? End, Word[] Words);

    public static SongLyrics Read(string file)
    {
        if (new FileInfo(file).Length > MaximumLength) throw new InvalidDataException("歌詞檔案不可超過 2 MB。");
        string content;
        try { content = File.ReadAllText(file, new UTF8Encoding(false, true)); }
        catch (DecoderFallbackException) { throw new InvalidDataException("歌詞檔案編碼無法讀取，請使用 UTF-8 或具有編碼標記的 Unicode 檔案。"); }
        var lyrics = new SongLyrics(Path.GetFileName(file), content);
        _ = lyrics.Parse();
        return lyrics;
    }

    public void Validate(double? duration = null)
    {
        if (Mode is not ("karaoke" or "scroll")) throw new InvalidDataException("請選擇歌詞呈現方式。");
        var cues = Parse();
        if (Mode == "karaoke" && cues.Any(c => !WordTimed(c)))
            throw new InvalidDataException("此檔案缺少完整的逐字時間，請匯入具有逐字時間的歌詞，或選擇滾動歌詞。");
        if (duration is {} seconds && (!double.IsFinite(seconds) || seconds <= 0
            || cues.Any(c => c.Start >= seconds || c.End > seconds + .1
                || c.Words.Any(w => w.Start >= seconds || w.End > seconds + .1))))
            throw new InvalidDataException("歌詞時間超出歌曲長度，請確認使用的是同一版本的歌曲。");
    }

    private static bool WordTimed(Cue cue) => cue.Words.Length > 1 || cue.Words.Length == 1
        && (new StringInfo(cue.Text.Trim()).LengthInTextElements == 1 || Regex.IsMatch(cue.Text.Trim(), @"^[A-Za-z][A-Za-z'-]*$"));

    private Cue[] Parse()
    {
        if (string.IsNullOrWhiteSpace(Content) || Content.Length > MaximumLength || string.IsNullOrWhiteSpace(Filename))
            throw new InvalidDataException("歌詞檔案為空白或大小不受支援。");
        var cues = Path.GetExtension(Filename).ToLowerInvariant() switch
        {
            ".lrc" => ParseLrc(),
            ".ass" => ParseAss(),
            _ => throw new InvalidDataException("歌詞僅支援 .lrc 或 .ass 檔案。")
        };
        if (cues.Length == 0 || cues.Length > 10000) throw new InvalidDataException("歌詞檔案沒有可讀取的時間軸，或行數超出上限。");
        foreach (var cue in cues)
        {
            if (!double.IsFinite(cue.Start) || cue.Start < 0 || cue.End is {} end && (!double.IsFinite(end) || end <= cue.Start))
                throw new InvalidDataException("歌詞的開始或結束時間無效。");
            double previous = cue.Start;
            foreach (var word in cue.Words)
            {
                if (word.Start < previous || word.End < word.Start || cue.End is {} limit && (word.Start > limit || word.End > limit + .01))
                    throw new InvalidDataException("逐字時間順序錯誤，或超出該句的時間範圍。");
                previous = word.End ?? word.Start;
            }
        }
        return cues;
    }

    private Cue[] ParseLrc()
    {
        var cues = new List<Cue>();
        double offset = 0;
        foreach (string raw in Content.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            var adjustment = Regex.Match(line, @"^\[offset:([+-]?\d+)\]$", RegexOptions.IgnoreCase);
            if (adjustment.Success)
            {
                if (!int.TryParse(adjustment.Groups[1].Value, out int milliseconds) || Math.Abs((long)milliseconds) > 3600000)
                    throw new InvalidDataException("歌詞 offset 時間無效。");
                offset = milliseconds / 1000d;
                continue;
            }
            if (Regex.IsMatch(line, @"^\[[a-z][a-z0-9_-]*:.*\]$", RegexOptions.IgnoreCase)) continue;
            var prefix = Regex.Match(line, @"^(?:\[" + ClockPattern + @"\])+");
            if (!prefix.Success) throw new InvalidDataException("LRC 歌詞含有缺少時間或時間格式錯誤的行。");
            string text = line[prefix.Length..].Trim();
            var starts = Regex.Matches(prefix.Value, @"\[(" + ClockPattern + @")\]");
            var timings = Regex.Matches(text, @"<(" + ClockPattern + @")>");
            if (timings.Count > 0 && starts.Count != 1) throw new InvalidDataException("逐字 LRC 每行僅能包含一個句首時間。");
            var words = new List<Word>();
            bool complete = timings.Count > 0 && string.IsNullOrWhiteSpace(text[..timings[0].Index]);
            for (int i = 0; i < timings.Count; i++)
            {
                var stamp = timings[i];
                double start = Clock(stamp.Groups[1].Value);
                double? end = i + 1 < timings.Count ? Clock(timings[i + 1].Groups[1].Value) : null;
                if (end < start) throw new InvalidDataException("逐字時間必須依演唱順序排列。");
                int until = i + 1 < timings.Count ? timings[i + 1].Index : text.Length;
                string word = text[(stamp.Index + stamp.Length)..until];
                if (word.Length > 0) words.Add(new(word, start, end));
            }
            string plain = Regex.Replace(text, @"<(" + ClockPattern + @")>", "");
            foreach (Match stamp in starts) cues.Add(new(plain, Clock(stamp.Groups[1].Value),
                timings.Count > 0 && timings[^1].Index + timings[^1].Length == text.Length ? Clock(timings[^1].Groups[1].Value) : null,
                complete ? words.ToArray() : []));
        }
        var ordered = cues.OrderBy(c => c.Start).ToArray();
        double?[] following = new double?[ordered.Length];
        for (int i = ordered.Length - 2; i >= 0; i--)
            following[i] = ordered[i + 1].Start > ordered[i].Start ? ordered[i + 1].Start : following[i + 1];
        var result = new List<Cue>();
        for (int i = 0; i < ordered.Length; i++)
        {
            var cue = ordered[i];
            double? end = cue.End ?? following[i];
            if (string.IsNullOrWhiteSpace(cue.Text)) continue;
            result.Add(cue with { Start = cue.Start + offset, End = end + offset,
                Words = cue.Words.Select(w => w with { Start = w.Start + offset, End = (w.End ?? end) + offset }).ToArray() });
        }
        return result.ToArray();
    }

    private Cue[] ParseAss()
    {
        bool events = false;
        string[] fields = [];
        var cues = new List<Cue>();
        foreach (string raw in Content.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { events = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!events) continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                fields = line[7..].Split(',').Select(f => f.Trim().ToLowerInvariant()).ToArray();
                continue;
            }
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            int startAt = Array.IndexOf(fields, "start"), endAt = Array.IndexOf(fields, "end");
            if (startAt < 0 || endAt < 0 || fields.LastOrDefault() != "text") throw new InvalidDataException("ASS 歌詞的 Events 格式無法讀取。");
            string[] values = line[9..].TrimStart().Split(',', fields.Length);
            if (values.Length != fields.Length) throw new InvalidDataException("ASS 歌詞的 Dialogue 欄位不完整。");
            double start = Clock(values[startAt]), end = Clock(values[endAt]);
            double cursor = start;
            double? wordStart = null, wordEnd = null;
            bool complete = true;
            var words = new List<Word>();
            var plain = new StringBuilder();
            foreach (string part in Regex.Split(values[^1], @"((?<!\\)\{[^}]*\})"))
            {
                if (part.StartsWith('{'))
                {
                    if (Regex.IsMatch(part, @"\\p[1-9]")) throw new InvalidDataException("歌詞匯入不支援 ASS 繪圖內容。");
                    foreach (Match tag in Regex.Matches(part, @"\\(kf|ko|kt|k|K)(\d+)(?![\d.])"))
                    {
                        if (!int.TryParse(tag.Groups[2].Value, out int value)) throw new InvalidDataException("ASS 逐字時間無效。");
                        if (tag.Groups[1].Value == "kt") { cursor = start + value / 100d; continue; }
                        wordStart = cursor; wordEnd = cursor + value / 100d; cursor = wordEnd.Value;
                    }
                    continue;
                }
                string text = part.Replace(@"\N", " ").Replace(@"\n", " ").Replace(@"\h", "\u00a0")
                    .Replace(@"\{", "{").Replace(@"\}", "}").Replace("\\\u2060", "\\");
                plain.Append(text);
                if (text.Length == 0) continue;
                if (wordStart == null) { if (!string.IsNullOrWhiteSpace(text)) complete = false; continue; }
                if (words.LastOrDefault() is {} last && last.Start == wordStart && last.End == wordEnd)
                    words[^1] = last with { Text = last.Text + text };
                else words.Add(new(text, wordStart.Value, wordEnd));
            }
            if (!string.IsNullOrWhiteSpace(plain.ToString())) cues.Add(new(plain.ToString(), start, end, complete ? words.ToArray() : []));
        }
        return cues.OrderBy(c => c.Start).ToArray();
    }

    private static double Clock(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3 || !double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds)
            || seconds is < 0 or >= 60 || !int.TryParse(parts[^2], out int minutes) || minutes < 0
            || parts.Length == 3 && (minutes >= 60 || !int.TryParse(parts[0], out _)))
            throw new InvalidDataException("歌詞時間格式無效。");
        return seconds + minutes * 60 + (parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) * 3600d : 0);
    }

    public string SidecarExtension => Mode switch
    {
        "scroll" => ".lrc",
        "karaoke" => ".ass",
        _ => throw new InvalidDataException("請選擇歌詞呈現方式。")
    };

    public string ToSidecar(double duration)
    {
        Validate(duration);
        if (Path.GetExtension(Filename).Equals(SidecarExtension, StringComparison.OrdinalIgnoreCase)) return Content;
        if (Mode == "karaoke") return ToAss(duration, includePreview: false);
        var cues = Parse();
        var output = new StringBuilder();
        for (int i = 0; i < cues.Length; i++)
        {
            var cue = cues[i];
            string text = cue.Text;
            if (cue.Words.Length > 0 && string.Concat(cue.Words.Select(w => w.Text)) == text)
            {
                var timed = new StringBuilder();
                for (int word = 0; word < cue.Words.Length; word++)
                {
                    var value = cue.Words[word];
                    timed.Append($"<{LrcTime(value.Start)}>{value.Text}");
                    if (value.End is {} until && (word == cue.Words.Length - 1 || until < cue.Words[word + 1].Start))
                        timed.Append($"<{LrcTime(until)}>");
                }
                if (cue.Words[^1].End != (cue.End ?? duration)) timed.Append($"<{LrcTime(cue.End ?? duration)}>");
                text = timed.ToString();
            }
            output.AppendLine($"[{LrcTime(cue.Start)}]{text}");
            double end = Math.Min(cue.End ?? duration, duration);
            if (i == cues.Length - 1 || end < cues[i + 1].Start) output.AppendLine($"[{LrcTime(end)}]");
        }
        return output.ToString();
    }

    internal static SongLyrics FromEmbeddedAss(string content, string mode)
    {
        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        string filtered = string.Join('\n', lines.Where(line => !line.TrimStart().StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)
            || (mode == "karaoke" ? Regex.IsMatch(line, @"\\k(?:f|o|t)?\d+") : line.Contains(@"\1c&HC4DD64&\1a&H00&", StringComparison.OrdinalIgnoreCase))));
        var lyrics = new SongLyrics("embedded.ass", filtered, mode);
        lyrics.Validate();
        return lyrics;
    }

    private static string LrcTime(double seconds)
    {
        long value = (long)Math.Round(seconds * 1000);
        if (value / 60000 > 999) throw new InvalidDataException("歌詞時間超出 LRC 格式可保存的範圍。");
        return $"{value / 60000:00}:{value / 1000 % 60:00}.{value % 1000:000}";
    }

    public string ToAss(double duration, bool includePreview = true)
    {
        Validate(duration);
        var cues = Parse();
        var output = new StringBuilder("""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1920
            PlayResY: 1080
            WrapStyle: 2
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Microsoft JhengHei,64,&H00C4DD64,&H00FFFFFF,&H00101820,&H80000000,-1,0,0,0,100,100,0,0,1,3,1,5,60,60,40,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            """);
        output.AppendLine();
        void Event(double start, double end, string text)
        {
            if (end > start) output.AppendLine($"Dialogue: 0,{AssTime(start)},{AssTime(end)},Default,,0,0,0,,{text}");
        }
        for (int i = 0; i < cues.Length; i++)
        {
            var cue = cues[i];
            double end = Math.Min(cue.End ?? duration, duration);
            if (Mode == "karaoke")
            {
                string position = $@"{{\an5\q2\pos(960,{(i % 2 == 0 ? 860 : 950)})\fs{FontSize(cue.Text, 64)}}}";
                double preview = Math.Max(0, Math.Max(cue.Start - 5, i > 0 ? cues[i - 1].Start : 0));
                if (includePreview) Event(preview, cue.Start, position + @"{\1c&HFFFFFF&}" + Escape(cue.Text));
                var text = new StringBuilder(position);
                foreach (var word in cue.Words)
                {
                    int at = Centiseconds(word.Start - cue.Start), length = Centiseconds((word.End ?? end) - word.Start);
                    text.Append($@"{{\kt{at}\kf{length}}}").Append(Escape(word.Text));
                }
                Event(cue.Start, end, text.ToString());
                continue;
            }
            if (i + 1 < cues.Length) end = Math.Min(end, cues[i + 1].Start);
            for (int row = Math.Max(0, i - 2); row <= Math.Min(cues.Length - 1, i + 2); row++)
            {
                int y = 540 + (row - i) * 108;
                string position = i == 0 ? $@"\pos(960,{y})" : $@"\move(960,{y + 108},960,{y},0,{Math.Min(220, Math.Max(1, (int)((end - cue.Start) * 1000)))})";
                string style = row == i ? @"\1c&HC4DD64&\1a&H00&" : @"\1c&HFFFFFF&\1a&H70&";
                Event(cue.Start, end, $@"{{\an5\q2{position}\fs{FontSize(cues[row].Text, row == i ? 60 : 44)}{style}}}" + Escape(cues[row].Text));
            }
        }
        return output.ToString();
    }

    private static int FontSize(string text, int maximum)
    {
        double units = text.EnumerateRunes().Sum(r => r.Value < 128 ? .6 : 1d);
        return Math.Max(1, (int)Math.Min(maximum, 1640 / Math.Max(1, units)));
    }
    private static int Centiseconds(double seconds) => Math.Max(0, (int)Math.Round(seconds * 100));
    private static string AssTime(double seconds)
    {
        int value = Centiseconds(seconds);
        return $"{value / 360000}:{value / 6000 % 60:00}:{value / 100 % 60:00}.{value % 100:00}";
    }
    private static string Escape(string text) => text.Replace("\\", "\\\u2060").Replace("{", @"\{").Replace("}", @"\}");
}
