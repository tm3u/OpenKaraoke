using System.Windows;
using System.Windows.Threading;
using OpenKaraoke.Scoring;

namespace OpenKaraoke.Prototype;

public partial class MainWindow
{
    private Task ApplySubtitleSettingsAsync(VoiceSettings value) => _player.SetSubtitlesAsync(value.SubtitlesEnabled, value.SubtitleFontPercent);

    private async void Subtitles_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _player == null || _closing) return;
        if (ToggleStatus.IsChanging(SubtitlesToggle))
        {
            _updating = true; SubtitlesToggle.IsChecked = ToggleStatus.GetTargetEnabled(SubtitlesToggle); _updating = false;
            return;
        }
        bool enabled = SubtitlesToggle.IsChecked == true;
        ToggleStatus.Begin(SubtitlesToggle, enabled);
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await _player.SetSubtitlesAsync(enabled, _voice.SubtitleFontPercent);
            var value = _voice with { SubtitlesEnabled = enabled };
            value.Save(VoiceSettingsPath); _voice = value;
        }
        catch (Exception error)
        {
            try { await ApplySubtitleSettingsAsync(_voice); }
            catch (Exception restore) { Log(restore.ToString()); }
            ShowError(error);
        }
        finally
        {
            ToggleStatus.Complete(SubtitlesToggle, _voice.SubtitlesEnabled);
            Render(_player.State);
        }
    }
}
