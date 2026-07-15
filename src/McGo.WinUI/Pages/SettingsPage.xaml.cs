using System.Text.Json;
using System.Text.Json.Nodes;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Java;
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
    private bool _loadingJavaCandidates;
    private bool _loadingSettings;
    private int _javaScanRequest;

    public SettingsPage()
    {
        InitializeComponent();
        SettingsSectionBar.SelectedItem = SettingsSectionBar.Items[0];
        _loadingSettings = true;
        LoadSettings();
        _loadingSettings = false;
        ApplyAdvancedVisibility();
        _ = RefreshHomeContentAsync(showFeedback: false);
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SettingsPage_Loaded;
        await RefreshJavaCandidatesAsync(JavaPathBox.Text);
    }

    private void SettingsSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        var section = sender.SelectedItem?.Tag?.ToString() ?? "environment";
        EnvironmentSettingsPanel.Visibility = section == "environment" ? Visibility.Visible : Visibility.Collapsed;
        DownloadSettingsPanel.Visibility = section == "download" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSettingsPanel.Visibility = section == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        MusicSettingsPanel.Visibility = section == "music" ? Visibility.Visible : Visibility.Collapsed;
        UpdateSettingsPanel.Visibility = section == "update" ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void RunSmokeChecks()
    {
        var originalAdvanced = AdvancedModeCheckBox.IsChecked;
        AdvancedModeCheckBox.IsChecked = false;
        if (CoreThreadsBox.Visibility != Visibility.Collapsed || ModrinthApiBaseBox.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("关闭高级模式后下载调优控件仍然可见。");
        }

        AdvancedModeCheckBox.IsChecked = true;
        if (CoreThreadsBox.Visibility != Visibility.Visible || ModrinthApiBaseBox.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("开启高级模式后下载调优控件没有显示。");
        }

        AdvancedModeCheckBox.IsChecked = originalAdvanced;
        foreach (var item in SettingsSectionBar.Items.OfType<SelectorBarItem>())
        {
            SettingsSectionBar.SelectedItem = item;
            var section = item.Tag?.ToString() ?? "";
            var visiblePanels = new[]
            {
                EnvironmentSettingsPanel.Visibility,
                DownloadSettingsPanel.Visibility,
                AppearanceSettingsPanel.Visibility,
                MusicSettingsPanel.Visibility,
                UpdateSettingsPanel.Visibility,
            }.Count(value => value == Visibility.Visible);
            if (visiblePanels != 1)
            {
                throw new InvalidOperationException($"设置分段 {section} 没有只显示一个面板。");
            }
        }

        SettingsSectionBar.SelectedItem = SettingsSectionBar.Items[0];
    }

    internal void SelectSettingsSectionForSmoke(string section)
    {
        SettingsSectionBar.SelectedItem = SettingsSectionBar.Items.OfType<SelectorBarItem>()
            .First(item => string.Equals(item.Tag?.ToString(), section, StringComparison.Ordinal));
        SettingsLayoutRoot.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void LoadSettings()
    {
        _settings.LoadConfig();
        GameDirectoryBox.Text = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        JavaPathBox.Text = _settings.Config.Get("JAVA", "path", "java");
        _loadingJavaCandidates = true;
        try
        {
            JavaCandidatesBox.ItemsSource = new[] { "正在扫描 Java..." };
            JavaCandidatesBox.SelectedIndex = 0;
        }
        finally
        {
            _loadingJavaCandidates = false;
        }
        CoreThreadsBox.Value = ClampDouble(_settings.Config.GetInt("DOWNLOAD", "max_core_threads", 12), 1, 64);
        AssetThreadsBox.Value = ClampDouble(_settings.Config.GetInt("DOWNLOAD", "max_asset_threads", 24), 1, 96);
        SpeedLimitBox.Value = ClampDouble(_settings.Config.GetInt("DOWNLOAD", "speed_limit_kbps", 0), 0, 1_048_576);
        ModrinthApiBaseBox.Text = _settings.Config.Get("DOWNLOAD", "modrinth_api_base", "");
        ModrinthCdnBaseBox.Text = _settings.Config.Get("DOWNLOAD", "modrinth_cdn_base", "");
        ThemeImageBox.Text = _settings.Config.Get("UI", "theme_image", "");
        HomeContentSourceBox.Text = _settings.Config.Get("HOME", "content_source", "");
        HomeNetworkCheckBox.IsChecked = _settings.Config.GetBool("HOME", "allow_network", false);
        AdvancedModeCheckBox.IsChecked = _settings.Config.GetBool("UI", "advanced_mode", false);
        ResourceIsolationCheckBox.IsChecked = _settings.Config.GetBool("GAME", "enable_resource_isolation", true);
        ShowDownloadCheckBox.IsChecked = _settings.Config.GetBool("FEATURES", "show_download", true);
        ShowManageCheckBox.IsChecked = _settings.Config.GetBool("FEATURES", "show_manage", true);
        MusicPathBox.Text = _settings.Config.Get("MUSIC", "path", "");
        MusicEnabledBox.IsChecked = _settings.Config.GetBool("MUSIC", "enabled", false);
        MusicVolumeBox.Value = ClampDouble(_settings.Config.GetInt("MUSIC", "volume", 35), 0, 100);
        MusicPauseOnLaunchBox.IsChecked = _settings.Config.GetBool("MUSIC", "pause_on_launch", true);
        UpdateManifestBox.Text = _settings.Config.Get("UPDATE", "manifest_url", AppConstants.DefaultUpdateManifestUrl);
        UpdateAutoCheckBox.IsChecked = _settings.Config.GetBool("UPDATE", "auto_check", true);
        SetUpdateStatus($"当前 McGo 版本：{AppConstants.AppVersion}");
        SetComboValue(MirrorBox, _settings.Config.Get("DOWNLOAD", "mirror_source", "official"));
        SetComboValue(CacheStrategyBox, _settings.Config.Get("DOWNLOAD", "cache_strategy", "reuse"));
        SetComboValue(ThemeBox, _settings.Config.Get("UI", "theme", "dark"));
    }

    private void AdvancedModeChanged(object sender, RoutedEventArgs e)
    {
        ApplyAdvancedVisibility();
        if (_loadingSettings)
        {
            return;
        }

        _settings.Config.Set("UI", "advanced_mode", (AdvancedModeCheckBox.IsChecked == true).ToString());
        _settings.SaveConfig();
        StatusText.Text = AdvancedModeCheckBox.IsChecked == true ? "高级选项已显示。" : "高级选项已隐藏。";
    }

    private void ApplyAdvancedVisibility()
    {
        var visibility = AdvancedModeCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CacheStrategyBox.Visibility = visibility;
        CoreThreadsBox.Visibility = visibility;
        AssetThreadsBox.Visibility = visibility;
        ModrinthApiBaseBox.Visibility = visibility;
        ModrinthCdnBaseBox.Visibility = visibility;
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
        _settings.Config.Set("DOWNLOAD", "max_core_threads", NumberValue(CoreThreadsBox, 1, 64, 12).ToString());
        _settings.Config.Set("DOWNLOAD", "max_asset_threads", NumberValue(AssetThreadsBox, 1, 96, 24).ToString());
        _settings.Config.Set("DOWNLOAD", "speed_limit_kbps", NumberValue(SpeedLimitBox, 0, 1_048_576, 0).ToString());
        _settings.Config.Set("DOWNLOAD", "cache_strategy", ComboValue(CacheStrategyBox, "reuse"));
        _settings.Config.Set("DOWNLOAD", "mirror_source", ComboValue(MirrorBox, "official"));
        _settings.Config.Set("DOWNLOAD", "modrinth_api_base", modrinthApiBase);
        _settings.Config.Set("DOWNLOAD", "modrinth_cdn_base", modrinthCdnBase);
        _settings.Config.Set("UI", "theme", ComboValue(ThemeBox, "dark"));
        _settings.Config.Set("UI", "theme_image", ThemeImageBox.Text.Trim());
        _settings.Config.Set("UI", "advanced_mode", (AdvancedModeCheckBox.IsChecked == true).ToString());
        _settings.Config.Set("GAME", "enable_resource_isolation", (ResourceIsolationCheckBox.IsChecked == true).ToString());
        _settings.Config.Set("FEATURES", "show_download", (ShowDownloadCheckBox.IsChecked == true).ToString());
        _settings.Config.Set("FEATURES", "show_manage", (ShowManageCheckBox.IsChecked == true).ToString());
        _settings.Config.Set("HOME", "content_source", HomeContentSourceBox.Text.Trim());
        _settings.Config.Set("HOME", "allow_network", (HomeNetworkCheckBox.IsChecked == true).ToString());
        SaveMusicConfig();
        _settings.Config.Set("UPDATE", "manifest_url", UpdateManifestBox.Text.Trim());
        _settings.Config.Set("UPDATE", "auto_check", (UpdateAutoCheckBox.IsChecked == true).ToString());
        _settings.SaveConfig();

        CoreThreadsBox.Value = ClampDouble(_settings.Config.GetInt("DOWNLOAD", "max_core_threads", 12), 1, 64);
        AssetThreadsBox.Value = ClampDouble(_settings.Config.GetInt("DOWNLOAD", "max_asset_threads", 24), 1, 96);
        SpeedLimitBox.Value = ClampDouble(_settings.Config.GetInt("DOWNLOAD", "speed_limit_kbps", 0), 0, 1_048_576);
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
            await RefreshJavaCandidatesAsync(file.Path);
        }
    }

    private async void RefreshJavaCandidates(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "正在扫描 Java 运行时...";
        await RefreshJavaCandidatesAsync(JavaPathBox.Text.Trim());
        StatusText.Text = $"已刷新 Java 候选：{JavaCandidatesBox.Items.Count} 项。";
    }

    private void JavaCandidateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingJavaCandidates)
        {
            return;
        }

        if (JavaCandidatesBox.SelectedItem is string path && path.Length > 0)
        {
            JavaPathBox.Text = path;
        }
    }

    private async Task RefreshJavaCandidatesAsync(string selectedPath)
    {
        var request = ++_javaScanRequest;
        var gameDirectory = GameDirectoryBox.Text.Trim();
        IReadOnlyList<string> candidates;
        try
        {
            candidates = await Task.Run(() => FindJavaCandidates(gameDirectory, selectedPath));
        }
        catch (Exception exc)
        {
            if (request == _javaScanRequest)
            {
                StatusText.Text = "扫描 Java 失败：" + exc.Message;
            }

            return;
        }

        if (request != _javaScanRequest)
        {
            return;
        }

        _loadingJavaCandidates = true;
        try
        {
            JavaCandidatesBox.ItemsSource = candidates;
            if (candidates.Count > 0)
            {
                JavaCandidatesBox.SelectedItem = candidates.FirstOrDefault(candidate => SamePathOrText(candidate, selectedPath)) ?? candidates[0];
            }
        }
        finally
        {
            _loadingJavaCandidates = false;
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
                NumberValue(MusicVolumeBox, 0, 100, 35));
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
        _settings.Config.Set("MUSIC", "volume", NumberValue(MusicVolumeBox, 0, 100, 35).ToString());
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
                SetUpdateStatus($"当前已是最新版：{AppConstants.AppVersion}");
                StatusText.Text = "当前没有可下载更新。";
                return;
            }

            var version = manifest["version"]?.GetValue<string>() ?? "新版本";
            SetUpdateStatus($"McGo {version} 已加入全局下载队列");
            DownloadTaskManager.Current.Enqueue("launcher_update", $"下载 McGo {version}", (record, token) =>
                DownloadUpdateTaskAsync(record, manifest, token));
        }
        catch (Exception exc)
        {
            UpdateOutputBox.Text = exc.Message;
            SetUpdateStatus("下载更新失败：" + exc.Message);
            StatusText.Text = "下载更新失败。";
        }
    }

    private async Task<string> DownloadUpdateTaskAsync(
        DownloadTaskRecord record,
        JsonObject manifest,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<int>(value =>
            {
                UpdateProgressBar.Value = value;
                SetUpdateStatus($"正在下载更新：{value}%");
                StatusText.Text = $"正在下载更新：{value}%";
                DownloadTaskManager.Current.Update(record, item =>
                {
                    item.Progress = value;
                    item.Detail = $"正在下载更新：{value}%";
                });
            });
            var result = await _updater.DownloadUpdateAsync(
                manifest,
                progress: progress,
                status: message => DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateOutputBox.Text = message;
                    DownloadTaskManager.Current.Update(record, item => item.Detail = message);
                }),
                cancellationToken: cancellationToken);
            _lastUpdateManifest = result;
            _lastUpdatePath = result["path"]?.GetValue<string>();
            UpdateOutputBox.Text = result.ToJsonString(JsonOptions);
            var downloadedVersion = result["version"]?.GetValue<string>() ?? "";
            SetUpdateStatus(downloadedVersion.Length > 0 ? $"McGo {downloadedVersion} 已下载" : "更新已下载");
            StatusText.Text = UpdaterService.CanReplaceCurrentExecutable()
                ? "更新已下载，可点击“应用更新”。"
                : "更新已下载；当前不是打包后的 McGo.exe，无法自动替换。";
            return downloadedVersion.Length > 0 ? $"McGo {downloadedVersion} 已下载" : "更新已下载";
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus("更新下载已取消");
            StatusText.Text = "更新下载已取消。";
            throw;
        }
        catch (Exception exc)
        {
            UpdateOutputBox.Text = exc.Message;
            SetUpdateStatus("下载更新失败：" + exc.Message);
            StatusText.Text = "下载更新失败。";
            throw;
        }
    }

    private async void ApplyDownloadedUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_lastUpdatePath) || !File.Exists(_lastUpdatePath))
            {
                SetUpdateStatus("请先下载可用更新。");
                StatusText.Text = "请先下载可用更新。";
                return;
            }

            if (!UpdaterService.CanReplaceCurrentExecutable())
            {
                UpdateOutputBox.Text = $"更新文件已下载：{_lastUpdatePath}{Environment.NewLine}当前运行目标不是打包后的 McGo.exe，无法自动替换。";
                SetUpdateStatus("当前运行模式不支持自动应用更新。");
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
            SetUpdateStatus("正在退出并应用更新...");
            StatusText.Text = "正在关闭 McGo 以应用更新。";
            Application.Current.Exit();
        }
        catch (Exception exc)
        {
            UpdateOutputBox.Text = exc.Message;
            SetUpdateStatus("应用更新失败：" + exc.Message);
            StatusText.Text = "应用更新失败。";
        }
    }

    private async Task<JsonObject?> CheckForUpdatesAsync(bool silent)
    {
        try
        {
            SaveAllSettings();
            if (!silent)
            {
                SetUpdateStatus("正在检查 McGo 更新...");
            }

            var manifest = await _updater.CheckForUpdateAsync(UpdateManifestBox.Text.Trim(), AppConstants.AppVersion);
            _lastUpdateManifest = manifest;
            _lastUpdateManifestUrl = UpdateManifestBox.Text.Trim();
            var updateAvailable = manifest["update_available"]?.GetValue<bool>() == true;
            var remoteVersion = manifest["version"]?.GetValue<string>() ?? "";
            var updateStatus = updateAvailable && remoteVersion.Length > 0
                ? $"发现新版本：{remoteVersion}"
                : $"当前已是最新版：{AppConstants.AppVersion}";
            if (!silent || updateAvailable)
            {
                UpdateOutputBox.Text = manifest.ToJsonString(JsonOptions);
                SetUpdateStatus(updateStatus);
                StatusText.Text = updateAvailable ? "发现可用更新。" : "当前已是最新。";
            }

            return manifest;
        }
        catch (Exception exc)
        {
            if (!silent)
            {
                UpdateOutputBox.Text = exc.Message;
                SetUpdateStatus("检查更新失败：" + exc.Message);
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
            mainWindow.ApplyFeatureVisibility(
                ShowDownloadCheckBox.IsChecked == true,
                ShowManageCheckBox.IsChecked == true);
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

    private static int NumberValue(NumberBox box, int min, int max, int fallback)
    {
        if (double.IsNaN(box.Value))
        {
            return fallback;
        }

        return Math.Clamp((int)Math.Round(box.Value), min, max);
    }

    private static double ClampDouble(int value, int min, int max) => Math.Clamp(value, min, max);

    private void SetUpdateStatus(string message)
    {
        if (UpdateStatusText is not null)
        {
            UpdateStatusText.Text = message;
        }
    }

    private static IReadOnlyList<string> FindJavaCandidates(string gameDirectory, string configuredPath)
    {
        var candidates = new List<string>();
        AddJavaCandidate(candidates, configuredPath);
        AddJavaCandidate(candidates, "java");
        AddJavaCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(Path.Combine(gameDirectory, "java_runtimes")));

        foreach (var pathDir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddJavaCandidate(candidates, Path.Combine(pathDir, OperatingSystem.IsWindows() ? "java.exe" : "java"));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     }.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                AddJavaCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(Path.Combine(root, "Java")));
                AddJavaCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(Path.Combine(root, "Eclipse Adoptium")));
                AddJavaCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(Path.Combine(root, "Microsoft")));
            }
        }

        return candidates
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
    }

    private static void AddJavaCandidate(List<string> candidates, string? path)
    {
        var value = (path ?? "").Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (string.Equals(value, "java", StringComparison.OrdinalIgnoreCase) || File.Exists(value))
        {
            candidates.Add(value);
        }
    }

    private static bool SamePathOrText(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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
