using McGo.Core.Configuration;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace McGo.WinUI.Services;

public static class MusicPlaybackService
{
    private static readonly MediaPlayer Player = new() { IsLoopingEnabled = true };

    public static bool ApplySavedSettings()
    {
        var settings = new AppSettings();
        settings.LoadConfig();
        return Apply(
            settings.Config.Get("MUSIC", "path", ""),
            settings.Config.GetBool("MUSIC", "enabled", false),
            settings.Config.GetInt("MUSIC", "volume", 35));
    }

    public static bool Apply(string path, bool enabled, int volume)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables((path ?? "").Trim());
        if (!enabled || expandedPath.Length == 0 || !File.Exists(expandedPath))
        {
            Stop();
            return false;
        }

        Player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(expandedPath)));
        Player.Volume = Math.Clamp(volume / 100.0, 0, 1);
        Player.Play();
        return true;
    }

    public static void Stop()
    {
        Player.Pause();
        Player.Source = null;
    }

    public static void Pause() => Player.Pause();

    public static void PauseForLaunchIfConfigured()
    {
        var settings = new AppSettings();
        settings.LoadConfig();
        if (settings.Config.GetBool("MUSIC", "pause_on_launch", true))
        {
            Pause();
        }
    }
}
