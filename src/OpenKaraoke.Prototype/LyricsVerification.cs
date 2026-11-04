using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenKaraoke.Player;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private const string VerificationLyrics = """
        [00:01.00]<00:01.00>今<00:01.50>天<00:02.00>一<00:02.50>起<00:03.00>唱<00:03.50>歌<00:04.00>
        [00:05.00]<00:05.00>跟<00:05.50>著<00:06.00>節<00:06.50>奏<00:07.00>向<00:07.50>前<00:08.00>
        [00:09.00]<00:09.00>讓<00:09.50>聲<00:10.00>音<00:10.50>更<00:11.00>遠<00:11.50>
        """;

    private async Task VerifyLyricsPlaybackAsync(string folder, string source, Action<string, bool, object?> check)
    {
        await using var connection = new MpvConnection(_player.PipeName);
        await connection.ConnectAsync();
        foreach (string mode in new[] { "karaoke", "scroll" })
        {
            string file = Path.Combine(folder, "lyrics-" + mode + ".opok");
            await _packages.ExportAsync(source, 1, 2, "歌詞顯示驗收", "測試歌手", file,
                lyrics: new SongLyrics("逐字.lrc", VerificationLyrics, mode));
            await _player.LoadAsync(file, play: false);
            await _player.SeekAsync(2.25);
            await Task.Delay(200);
            string text = (await connection.GetAsync("sub-text")).GetString() ?? "";
            check(mode + " 模式在實際播放器顯示匯入歌詞", _player.State.LyricsMode == mode && text.Contains("今天一起唱歌"), text);
            await _player.CaptureFrameAsync(Path.Combine(folder, "lyrics-" + mode + "-playing.png"));
            await _player.TogglePauseAsync(); await Task.Delay(150); await _player.SetPausedAsync(true);
            double paused = _player.State.Position; await Task.Delay(120);
            check(mode + " 歌詞與播放共用時間軸，暫停不繼續前進", Math.Abs(_player.State.Position - paused) < .05, _player.State.Position);
            await _player.SeekAsync(5.5); await Task.Delay(150);
            text = (await connection.GetAsync("sub-text")).GetString() ?? "";
            check(mode + " 跳轉後同步切換到相對應歌詞", text.Contains("跟著節奏向前"), text);
            await _player.CaptureFrameAsync(Path.Combine(folder, "lyrics-" + mode + "-next.png"));
            await VerifySubtitleControlsAsync(folder, mode, check);
        }
        await _player.LoadAsync(source, play: false);
        var tracks = await connection.GetAsync("track-list");
        bool selected = tracks.EnumerateArray().Any(t => t.GetProperty("type").GetString() == "sub"
            && t.TryGetProperty("selected", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True);
        check("播放無歌詞歌曲時不殘留上一首字幕", _player.State.LyricsMode == "" && !selected, tracks);
    }

    private async Task VerifySubtitleControlsAsync(string folder, string mode, Action<string, bool, object?> check)
    {
        await using var connection = new MpvConnection(_player.PipeName);
        await connection.ConnectAsync();
        bool scoring = _voice.ScoringEnabled;
        async Task Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.Elapsed.TotalSeconds > 12) throw new TimeoutException("字幕控制驗收逾時。");
                await Task.Delay(20);
            }
        }
        async Task Toggle(bool enabled)
        {
            SubtitlesToggle.IsChecked = enabled;
            await Wait(() => !ToggleStatus.IsChanging(SubtitlesToggle));
        }
        async Task FontDialog(int percent, bool cancel = false, VoiceSettings? settings = null)
        {
            bool isolated = settings != null;
            var dialog = new VoiceSettingsDialog(this, settings ?? _voice, _references.Model, _phone, _localInput, _store.DataDirectory,
                value => { if (!isolated) { value.Save(VoiceSettingsPath); _voice = value; } }, applyDisplaySettings: ApplySubtitleSettingsAsync);
            Exception? failure = null;
            dialog.Loaded += async (_, _) =>
            {
                try
                {
                    ((TabItem)dialog.FindName("DisplayTab")).IsSelected = true;
                    var slider = (Slider)dialog.FindName("SubtitleFontSize");
                    slider.Value = percent;
                    check("字體大小在套用前維持草稿，不改變現有字幕", _player.State.SubtitleFontPercent == _voice.SubtitleFontPercent, _player.State.SubtitleFontPercent);
                    await Dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    CaptureDialog(dialog, Path.Combine(folder, "lyrics-display-settings.png"));
                    if (cancel)
                    {
                        ((Button)dialog.FindName("ResetSubtitleFontButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        check("恢復預設大小只更新設定草稿", slider.Value == 100 && _player.State.SubtitleFontPercent == _voice.SubtitleFontPercent, slider.Value);
                    }
                    ((Button)dialog.FindName(cancel ? "CancelButton" : "ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Wait(() => !dialog.IsVisible || ((TextBlock)dialog.FindName("DialogError")).Text.Length > 0);
                    if (dialog.IsVisible) throw new InvalidOperationException(((TextBlock)dialog.FindName("DialogError")).Text);
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            };
            bool? applied = dialog.ShowDialog();
            if (failure != null) throw failure;
            check(cancel ? "取消顯示設定不保存字體草稿" : "顯示設定套用成功", cancel ? applied != true : applied == true, applied);
        }
        static int DifferentPixels(string file, string baseline)
        {
            byte[] Pixels(string path)
            {
                var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
            }
            byte[] actual = Pixels(file), original = Pixels(baseline);
            if (actual.Length != original.Length) throw new InvalidDataException("字幕畫面尺寸不一致。");
            int changed = 0;
            for (int i = 0; i < actual.Length; i += 4)
                if (Math.Abs(actual[i] - original[i]) + Math.Abs(actual[i + 1] - original[i + 1]) + Math.Abs(actual[i + 2] - original[i + 2]) > 40) changed++;
            return changed;
        }
        string file = _player.State.File;
        string hidden = Path.Combine(folder, "lyrics-" + mode + "-hidden.png");
        await Toggle(false);
        check(mode + " 字幕開關立即隱藏並保存，評分開關不變", !(await connection.GetAsync("sub-visibility")).GetBoolean()
            && !VoiceSettings.Load(VoiceSettingsPath).SubtitlesEnabled && _voice.ScoringEnabled == scoring, _player.State);
        await Task.Delay(100);
        await _player.CaptureFrameAsync(hidden);
        check(mode + " 畫面中的字幕確實隱藏", DifferentPixels(Path.Combine(folder, "lyrics-" + mode + "-next.png"), hidden) > 100, null);
        await Toggle(true);
        await FontDialog(75);
        string small = Path.Combine(folder, "lyrics-" + mode + "-small.png");
        await Task.Delay(100);
        await _player.CaptureFrameAsync(small);
        await FontDialog(125);
        string large = Path.Combine(folder, "lyrics-" + mode + "-large.png");
        await Task.Delay(100);
        await _player.CaptureFrameAsync(large);
        int smallPixels = DifferentPixels(small, hidden), largePixels = DifferentPixels(large, hidden);
        check(mode + " 字體調整實際改變字幕大小，保留所選模式", largePixels > smallPixels * 1.3 && _player.State.LyricsMode == mode
            && Math.Abs((await connection.GetAsync("sub-scale")).GetDouble() - 1.25) < .001, new { SmallPixels = smallPixels, LargePixels = largePixels });
        await FontDialog(75, cancel: true);
        check("取消或重設草稿後保留原本的字幕大小", _voice.SubtitleFontPercent == 125 && VoiceSettings.Load(VoiceSettingsPath).SubtitleFontPercent == 125
            && _player.State.SubtitleFontPercent == 125, _voice.SubtitleFontPercent);
        if (mode == "karaoke")
        {
            string device = _localInput.DeviceId; bool running = _localInput.Running;
            await FontDialog(75, settings: _voice with { DeviceId = "unconnected-subtitle-test", PhoneEnabled = false });
            check("只調整字幕大小不需要開啟未連接的麥克風", _localInput.DeviceId == device && _localInput.Running == running, device);
            await ApplySubtitleSettingsAsync(_voice);
        }
        await Toggle(false);
        await _player.LoadAsync(file, play: false);
        check(mode + " 重新載入歌曲後保留字幕關閉與字體大小", !_player.State.SubtitlesEnabled && _player.State.SubtitleFontPercent == 125
            && !(await connection.GetAsync("sub-visibility")).GetBoolean() && Math.Abs((await connection.GetAsync("sub-scale")).GetDouble() - 1.25) < .001, _player.State);
        await Toggle(true);
        _voice = _voice with { SubtitleFontPercent = 100 }; _voice.Save(VoiceSettingsPath); await ApplySubtitleSettingsAsync(_voice);
        if (mode == "karaoke")
        {
            string blocked = VoiceSettingsPath + ".tmp";
            Directory.CreateDirectory(blocked);
            try
            {
                await Toggle(false);
                check("字幕開關保存失敗時還原畫面與原設定", _voice.SubtitlesEnabled && SubtitlesToggle.IsChecked == true
                    && (await connection.GetAsync("sub-visibility")).GetBoolean() && VoiceSettings.Load(VoiceSettingsPath).SubtitlesEnabled, null);
            }
            finally { Directory.Delete(blocked); Toast.Visibility = Visibility.Collapsed; }
        }
    }
}
