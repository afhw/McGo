using System.Text.Json;
using System.Text.Json.Nodes;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Personalization;
using McGo.Core.Updates;
using McGo.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace McGo.WinUI.Pages;

public sealed partial class SettingsPage : Page
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppSettings _settings = new();
    private readonly HomeContentService _homeContent = new();
    private readonly UpdaterService _updater = new();
    private JsonObject? _lastUpdateManifest;
    private string? _lastUpdateManifestUrl;
    private string? _lastUpdatePath;

    public SettingsPage()
    {
        InitializeComponent();
        LoadSettings();
        _ = RefreshHomeContentAsync(showFeedback: false);
    }

    private void LoadSettings()
    {
        _settings.LoadConfig();
        GameDirectoryBox.Text = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        JavaPathBox.Text = _settings.Config.Get("JAVA", "path", "java");
        CoreThreadsBox.Text = _settings.Config.Get("DOWNLOAD", "max_core_threads", "12");
        AssetThreadsBox.Text = _settings.Config.Get("DOWNLOAD", "max_asset_threads", "24");
        SpeedLimitBox.Text = _settings.Config.Get("DOWNLOAD", "speed_limit_kbps", "0");
        ModrinthApiBaseBox.Text = _settings.Config.Get("DOWNLOAD", "modrinth_api_base", "");
        ModrinthCdnBaseBox.Text = _settings.Config.Get("DOWNLOAD", "modrinth_cdn_base", "");
        ThemeImageBox.Text = _settings.Config.Get("UI", "theme_image", "");
        HomeContentSourceBox.Text = _settings.Config.Get("HOME", "content_source", "");
        HomeNetworkCheckBox.IsChecked = _settings.Config.GetBool("HOME", "allow_network", false);
        MusicPathBox.Text = _settings.Config.Get("MUSIC", "path", "");
        MusicEnabledBox.IsChecked = _settings.Config.GetBool("MUSIC", "enabled", false);
        MusicVolumeSlider.Value = Math.Clamp(_settings.Config.GetInt("MUSIC", "volume", 35), 0, 100);
        MusicPauseOnLaunchBox.IsChecked = _settings.Config.GetBool("MUSIC", "pause_on_launch", true);
        UpdateManifestBox.Text = _settings.Config.Get("UPDATE", "manifest_url", AppConstants.DefaultUpdateManifestUrl);
        UpdateAutoCheckBox.IsChecked = _settings.Config.GetBool("UPDATE", "auto_check", true);
        SetComboValue(MirrorBox, _settings.Config.Get("DOWNLOAD", "mirror_source", "official"));
        SetComboValue(CacheStrategyBox, _settings.Config.Get("DOWNLOAD", "cache_strategy", "reuse"));
        SetComboValue(ThemeBox, _settings.Config.Get("UI", "theme", "dark"));
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveAllSettings();
            ApplyAppearance();
            StatusText.Text = "设置已保存。";
            _ = RefreshHomeContentAsync(showFeedback: false);
        }
        catch (Exception exc)
        {
            StatusText.Text = "保存设置失败：" + exc.Message;
        }
    }

    private void SaveAllSettings()
    {
        var modrinthApiBase = NormalizeOptionalHttpBase(ModrinthApiBaseBox.Text, "Modrinth API");
        var modrinthCdnBase = NormalizeOptionalHttpBase(ModrinthCdnBaseBox.Text, "Modrinth CDN");
        _settings.Config.Set("GAME", "directory", GameDirectoryBox.Text.Trim());
        _settings.Config.Set("JAVA", "path", JavaPathBox.Text.Trim());
        _settings.Config.Set("DOWNLOAD", "max_core_threads", ClampIntText(CoreThreadsBox.Text, 1, 128, 12).ToString());
        _settings.Config.Set("DOWNLOAD", "max_asset_threads", ClampIntText(AssetThreadsBox.Text, 1, 256, 24).ToString());
        _settings.Config.Set("DOWNLOAD", "speed_limit_kbps", ClampIntText(SpeedLimitBox.Text, 0, 10_000_000, 0).ToString());
        _settings.Config.Set("DOWNLOAD", "cache_strategy", ComboValue(CacheStrategyBox, "reuse"));
        _settings.Config.Set("DOWNLOAD", "mirror_source", ComboValue(MirrorBox, "official"));
        _settings.Config.Set("DOWNLOAD", "modrinth_api_base", modrinthApiBase);
        _settings.Config.Set("DOWNLOAD", "modrinth_cdn_base", modrinthCdnBase);
        _settings.Config.Set("UI", "theme", ComboValue(ThemeBox, "dark"));
        _settings.Config.Set("UI", "theme_image", ThemeImageBox.Text.Trim());
        _settings.Config.Set("HOME", "content_source", HomeContentSourceBox.Text.Trim());
        _settings.Config.Set("HOME", "allow_network", (HomeNetworkCheckBox.IsChecked == true).ToString());
        SaveMusicConfig();
        _settings.Config.Set("UPDATE", "manifest_url", UpdateManifestBox.Text.Trim());
        _settings.Config.Set("UPDATE", "auto_check", (UpdateAutoCheckBox.IsChecked == true).ToString());
        _settings.SaveConfig();

        CoreThreadsBox.Text = _settings.Config.Get("DOWNLOAD", "max_core_threads", "12");
        AssetThreadsBox.Text = _settings.Config.Get("DOWNLOAD", "max_asset_threads", "24");
        SpeedLimitBox.Text = _settings.Config.Get("DOWNLOAD", "speed_limit_kbps", "0");
        ModrinthApiBaseBox.Text = modrinthApiBase;
        ModrinthCdnBaseBox.Text = modrinthCdnBase;
    }

    private async void ChooseGameDirectory(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            GameDirectoryBox.Text = folder.Path;
        }
    }

    private async void ChooseJavaFile(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync([".exe", ".bat", ".cmd"]);
        if (file is not null)
        {
            JavaPathBox.Text = file.Path;
        }
    }

    private async void ChooseThemeImageFile(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync([".png", ".jpg", ".jpeg", ".webp", ".bmp"]);
        if (file is not null)
        {
            ThemeImageBox.Text = file.Path;
            SaveAllSettings();
            ApplyAppearance();
            StatusText.Text = "主题背景图已应用。";
        }
    }

    private async void ChooseHomeContentFile(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync([".txt", ".md", ".json", ".log"]);
        if (file is not null)
        {
            HomeContentSourceBox.Text = file.Path;
            SaveAllSettings();
            await RefreshHomeContentAsync(showFeedback: true);
        }
    }

    private async void RefreshHomeContent(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveAllSettings();
            await RefreshHomeContentAsync(showFeedback: true);
        }
        catch (Exception exc)
        {
            StatusText.Text = "刷新主页失败：" + exc.Message;
        }
    }

    private async Task RefreshHomeContentAsync(bool showFeedback)
    {
        try
        {
            var result = await _homeContent.LoadContentAsync(HomeContentSourceBox.Text.Trim(), HomeNetworkCheckBox.IsChecked == true);
            HomePreviewBox.Text = result.Text;
            if (showFeedback)
            {
                StatusText.Text = result.FromNetwork ? "联网主页已刷新。" : "主页内容已刷新。";
            }
        }
        catch (Exception exc)
        {
            HomePreviewBox.Text = "主页内容加载失败：" + exc.Message;
            if (showFeedback)
            {
                StatusText.Text = "主页内容加载失败。";
            }
        }
    }

    private async void ChooseMusicFile(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync([".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg"]);
        if (file is not null)
        {
            MusicPathBox.Text = file.Path;
            MusicEnabledBox.IsChecked = true;
            ApplyMusicSettings(showFeedback: true);
        }
    }

    private void ApplyMusicSettings(object sender, RoutedEventArgs e) => ApplyMusicSettings(showFeedback: true);

    private void ApplyMusicSettings(bool showFeedback)
    {
        try
        {
            SaveMusicConfig();
            _settings.SaveConfig();
            var played = MusicPlaybackService.Apply(
                MusicPathBox.Text.Trim(),
                MusicEnabledBox.IsChecked == true,
                (int)Math.Round(MusicVolumeSlider.Value));
            if (played)
            {
                if (showFeedback)
                {
                    StatusText.Text = "背景音乐已播放。";
                }

                return;
            }

            if (showFeedback)
            {
                StatusText.Text = MusicEnabledBox.IsChecked == true ? "音乐文件不存在。" : "背景音乐已关闭。";
            }
        }
        catch (Exception exc)
        {
            if (showFeedback)
            {
                StatusText.Text = "背景音乐设置失败：" + exc.Message;
            }
        }
    }

    private void StopMusic(object sender, RoutedEventArgs e)
    {
        MusicPlaybackService.Stop();
        MusicEnabledBox.IsChecked = false;
        SaveMusicConfig();
        _settings.SaveConfig();
        StatusText.Text = "背景音乐已停止。";
    }

    private void SaveMusicConfig()
    {
        _settings.Config.Set("MUSIC", "path", MusicPathBox.Text.Trim());
        _settings.Config.Set("MUSIC", "enabled", (MusicEnabledBox.IsChecked == true).ToString());
        _settings.Config.Set("MUSIC", "volume", ((int)Math.Round(MusicVolumeSlider.Value)).ToString());
        _settings.Config.Set("MUSIC", "pause_on_launch", (MusicPauseOnLaunchBox.IsChecked == true).ToString());
    }

    private async void CheckUpdates(object sender, RoutedEventArgs e)
        => await CheckForUpdatesAsync(silent: false);

    private async void DownloadUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveAllSettings();
            var manifestUrl = UpdateManifestBox.Text.Trim();
            var manifest = _lastUpdateManifestUrl == manifestUrl ? _lastUpdateManifest : null;
            manifest ??= await CheckForUpdatesAsync(silent: false);
            if (manifest is null)
            {
                return;
            }

            if (manifest["update_available"]?.GetValue<bool>() != true)
            {
                StatusText.Text = "当前没有可下载更新。";
                return;
            }

            var progress = new Progress<int>(value =>
            {
                UpdateProgressBar.Value = value;
                StatusText.Text = $"正在下载更新：{value}%";
            });
            var result = await _updater.DownloadUpdateAsync(
                manifest,
                progress: progress,
                status: message => DispatcherQueue.TryEnqueue(() => UpdateOutputBox.Text = message));
            _lastUpdateManifest = result;
            _lastUpdatePath = result["path"]?.GetValue<string>();
            UpdateOutputBox.Text = result.ToJsonString(JsonOptions);
            StatusText.Text = UpdaterService.CanReplaceCurrentExecutable()
                ? "更新已下载，可点击“应用更新”。"
                : "更新已下载；当前不是打包后的 McGo.exe，无法自动替换。";
        }
        catch (Exception exc)
        {
            UpdateOutputBox.Text = exc.Message;
            StatusText.Text = "下载更新失败。";
        }
    }

    private async void ApplyDownloadedUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_lastUpdatePath) || !File.Exists(_lastUpdatePath))
            {
                StatusText.Text = "请先下载可用更新。";
                return;
            }

            if (!UpdaterService.CanReplaceCurrentExecutable())
            {
                UpdateOutputBox.Text = $"更新文件已下载：{_lastUpdatePath}{Environment.NewLine}当前运行目标不是打包后的 McGo.exe，无法自动替换。";
                StatusText.Text = "当前运行模式不支持自动应用更新。";
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "应用更新",
                Content = "应用更新需要关闭 McGo，并在替换完成后重新启动。是否继续？",
                PrimaryButtonText = "继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var result = UpdaterService.ScheduleWindowsReplaceAndRestart(_lastUpdatePath);
            UpdateOutputBox.Text = $"已安排更新替换。日志：{result.LogPath}{Environment.NewLine}需要管理员权限：{(result.Elevated ? "是" : "否")}";
            StatusText.Text = "正在关闭 McGo 以应用更新。";
            Application.Current.Exit();
        }
        catch (Exception exc)
        {
            UpdateOutputBox.Text = exc.Message;
            StatusText.Text = "应用更新失败。";
        }
    }

    private async Task<JsonObject?> CheckForUpdatesAsync(bool silent)
    {
        try
        {
            SaveAllSettings();
            var manifest = await _updater.CheckForUpdateAsync(UpdateManifestBox.Text.Trim(), AppConstants.AppVersion);
            _lastUpdateManifest = manifest;
            _lastUpdateManifestUrl = UpdateManifestBox.Text.Trim();
            var updateAvailable = manifest["update_available"]?.GetValue<bool>() == true;
            if (!silent || updateAvailable)
            {
                UpdateOutputBox.Text = manifest.ToJsonString(JsonOptions);
                StatusText.Text = updateAvailable ? "发现可用更新。" : "当前已是最新。";
            }

            return manifest;
        }
        catch (Exception exc)
        {
            if (!silent)
            {
                UpdateOutputBox.Text = exc.Message;
                StatusText.Text = "检查更新失败。";
            }

            return null;
        }
    }

    private async Task<Windows.Storage.StorageFile?> PickFileAsync(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        return await picker.PickSingleFileAsync();
    }

    private static void InitializePicker(object picker)
    {
        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }
    }

    private void ApplyAppearance()
    {
        if (App.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ApplyAppearance(ComboValue(ThemeBox, "dark"), ThemeImageBox.Text.Trim());
        }
    }

    private static string ComboValue(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Tag?.ToString() ?? item.Content?.ToString() ?? fallback;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }

    private static void SetComboValue(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static int ClampIntText(string value, int min, int max, int fallback)
        => int.TryParse(value.Trim(), out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private static string NormalizeOptionalHttpBase(string value, string label)
    {
        var trimmed = value.Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return "";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{label} 需要填写 http/https URL。");
        }

        return trimmed;
    }
}
