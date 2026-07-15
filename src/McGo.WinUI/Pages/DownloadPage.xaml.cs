using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Formatting;
using McGo.Core.Installation;
using McGo.Core.Logging;
using McGo.Core.Minecraft;

namespace McGo.WinUI.Pages;

public sealed partial class DownloadPage : Page
{
    private readonly AppSettings _settings = new();
    private readonly InstallServices _installServices = new();
    private readonly DownloaderService _downloader = new();
    private readonly LauncherService _launcher = new();
    private readonly VersionSettingsService _versionSettingsService;
    private readonly DownloadTaskManager _taskManager = DownloadTaskManager.Current;
    private bool _loadingSettings;
    private bool _initialized;
    private int _localVersionScanRequest;

    private static readonly IReadOnlyDictionary<string, DownloadPreset> DownloadPresets = new Dictionary<string, DownloadPreset>
    {
        ["稳定"] = new(8, 16, 0, "reuse"),
        ["均衡"] = new(12, 24, 0, "reuse"),
        ["极速"] = new(24, 48, 0, "reuse"),
    };

    public DownloadPage()
    {
        InitializeComponent();
        _versionSettingsService = new VersionSettingsService(_launcher);
        _taskManager.StateChanged += DownloadTaskManagerStateChanged;
        _initialized = true;
        DownloadSectionBar.SelectedItem = DownloadSectionBar.Items[0];
        LoadSettings();
        Loaded += DownloadPage_Loaded;
    }

    private void DownloadTaskManagerStateChanged(object? sender, EventArgs e)
        => UpdateFromAnyThread(UpdateTaskCenter);

    private async void DownloadPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DownloadPage_Loaded;
        await RefreshLocalInstallTargetsAsync();
    }

    private void DownloadSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        var section = (sender.SelectedItem as SelectorBarItem)?.Tag?.ToString() ?? "vanilla";
        if (section == "resources" || section == "modpacks")
        {
            Navigate(section);
            return;
        }

        DownloadVanillaPanel.Visibility = section == "vanilla" ? Visibility.Visible : Visibility.Collapsed;
        DownloadAddonPanel.Visibility = section == "addons" ? Visibility.Visible : Visibility.Collapsed;
        DownloadTasksPanel.Visibility = section == "tasks" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        _settings.LoadConfig();
        GameDirectoryBox.Text = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        JavaPathBox.Text = _settings.Config.Get("JAVA", "path", "java");
        MirrorBox.SelectedIndex = _settings.Config.Get("DOWNLOAD", "mirror_source", "official") == "bmclapi" ? 1 : 0;
        SetComboText(DownloadPresetBox, _settings.Config.Get("DOWNLOAD", "download_preset", "均衡"));
        SetComboTag(InstallTypeBox, "");
        SetComboText(CacheStrategyBox, _settings.Config.Get("DOWNLOAD", "cache_strategy", "reuse"));
        CoreThreadsBox.Value = ClampDouble(_settings.Config.GetInt("DOWNLOAD", "max_core_threads", 12), 1, 64);
        AssetThreadsBox.Value = ClampDouble(_settings.Config.GetInt("DOWNLOAD", "max_asset_threads", 24), 1, 96);
        SpeedLimitBox.Value = ClampDouble(_settings.Config.GetInt("DOWNLOAD", "speed_limit_kbps", 0), 0, 1_048_576);
        ApplyAdvancedVisibility();
        UpdateInstallAddonControls();
        ResetInstallVersionChoices("不安装");
        ResetAddonInstallVersionChoices("点击刷新安装器版本");
        InstallTargetVersionBox.ItemsSource = new[] { "正在扫描本地版本..." };
        InstallTargetVersionBox.SelectedIndex = 0;
        UpdateAddonInstallButtonText();
        UpdateDownloadMetrics("等待下载");
        UpdateDownloadQueueSummary();
        UpdateInstallStatus("等待安装任务", "尚未开始安装");
        _loadingSettings = false;
    }

    private void ApplyAdvancedVisibility()
    {
        var visibility = _settings.Config.GetBool("UI", "advanced_mode", false) ? Visibility.Visible : Visibility.Collapsed;
        AdvancedDownloadSettingsPanel.Visibility = visibility;
    }

    internal void RunSmokeChecks()
    {
        if (DownloadSectionBar.Items.Count != 5)
        {
            throw new InvalidOperationException("下载页缺少 Python 版下载任务分段。");
        }

        DownloadSectionBar.SelectedItem = DownloadSectionBar.Items[1];
        if (DownloadAddonPanel.Visibility != Visibility.Visible || DownloadVanillaPanel.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("下载页安装扩展分段状态不正确。");
        }

        DownloadSectionBar.SelectedItem = DownloadSectionBar.Items[4];
        if (DownloadTasksPanel.Visibility != Visibility.Visible || DownloadAddonPanel.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("下载页任务管理分段状态不正确。");
        }

        DownloadSectionBar.SelectedItem = DownloadSectionBar.Items[0];
        if (DownloadVanillaPanel.Visibility != Visibility.Visible || DownloadTasksPanel.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("下载页下载原版分段状态不正确。");
        }

        var expected = _settings.Config.GetBool("UI", "advanced_mode", false) ? Visibility.Visible : Visibility.Collapsed;
        if (AdvancedDownloadSettingsPanel.Visibility != expected)
        {
            throw new InvalidOperationException("下载调优控件可见性与高级模式不一致。");
        }

        SetComboTag(InstallTypeBox, "");
        UpdateInstallAddonControls();
        if (InstallVersionPanel.Visibility != Visibility.Collapsed || Grid.GetColumn(InstallExtrasPanel) != 0 || Grid.GetColumnSpan(InstallExtrasPanel) != 2)
        {
            throw new InvalidOperationException("未选择加载器时安装选项布局不正确。");
        }

        SetComboTag(InstallTypeBox, "fabric");
        UpdateInstallAddonControls();
        if (InstallVersionPanel.Visibility != Visibility.Visible || Grid.GetColumn(InstallExtrasPanel) != 1 || InstallFabricApiCheckBox.IsEnabled == false)
        {
            throw new InvalidOperationException("选择 Fabric 后安装选项布局不正确。");
        }

        SetComboTag(InstallTypeBox, "");
        UpdateInstallAddonControls();
    }

    internal void SelectDownloadSectionForSmoke(string section)
    {
        DownloadSectionBar.SelectedItem = DownloadSectionBar.Items.OfType<SelectorBarItem>()
            .First(item => string.Equals(item.Tag?.ToString(), section, StringComparison.Ordinal));
    }

    internal void OpenGlobalTaskCenter()
    {
        SelectDownloadSectionForSmoke("tasks");
        BringDownloadSectionIntoViewForSmoke("tasks");
        UpdateTaskCenter();
    }

    internal bool IsGlobalTaskCenterOpen => DownloadTasksPanel.Visibility == Visibility.Visible;

    internal void BringDownloadSectionIntoViewForSmoke(string section)
    {
        var target = section switch
        {
            "addons" => DownloadAddonPanel,
            "tasks" => DownloadTasksPanel,
            _ => DownloadVanillaPanel,
        };
        target.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 0,
        });
    }

    private static void Navigate(string tag)
    {
        if (App.MainWindow is MainWindow window)
        {
            window.NavigateToTag(tag);
        }
    }

    private void StayOnDownloads(object sender, RoutedEventArgs e) => AppendDownload("已停留在下载任务页。");
    private void OpenResources(object sender, RoutedEventArgs e) => Navigate("resources");
    private void OpenModpacks(object sender, RoutedEventArgs e) => Navigate("modpacks");

    private async void RefreshRemoteVersions(object sender, RoutedEventArgs e)
    {
        try
        {
            DownloadButton.IsEnabled = false;
            AppendDownload("正在刷新远程版本...");
            var versions = await _installServices.GetRemoteVersionsAsync(ComboText(VersionTypeBox, "release"), ComboText(MirrorBox, "official"));
            RemoteVersionsBox.ItemsSource = versions;
            if (versions.Count > 0)
            {
                RemoteVersionsBox.SelectedIndex = 0;
            }

            AppendDownload($"已获取 {versions.Count} 个远程版本。");
        }
        catch (Exception exc)
        {
            AppendDownload("刷新远程版本失败：" + exc.Message);
        }
        finally
        {
            DownloadButton.IsEnabled = true;
        }
    }

    private async void DownloadSelectedVersion(object sender, RoutedEventArgs e)
    {
        var versionId = RemoteVersionsBox.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(versionId))
        {
            AppendDownload("请先选择远程版本。");
            return;
        }

        var mirror = ComboText(MirrorBox, "official");
        var gameDir = GameDirectoryBox.Text.Trim();
        var javaPath = JavaPathBox.Text.Trim();
        var installTypes = SelectedInstallTypes();
        if (installTypes.Any(type => type is "forge" or "neoforge") && string.IsNullOrWhiteSpace(javaPath))
        {
            AppendDownload("自动安装 Forge 或 NeoForge 需要可用的 Java 路径。");
            return;
        }

        _settings.Config.Set("GAME", "directory", gameDir);
        _settings.Config.Set("JAVA", "path", javaPath);
        _settings.Config.Set("DOWNLOAD", "mirror_source", mirror);
        _settings.Config.Set("DOWNLOAD", "download_preset", ComboText(DownloadPresetBox, "均衡"));
        _settings.Config.Set("DOWNLOAD", "max_core_threads", NumberValue(CoreThreadsBox, 1, 64, 12).ToString());
        _settings.Config.Set("DOWNLOAD", "max_asset_threads", NumberValue(AssetThreadsBox, 1, 96, 24).ToString());
        _settings.Config.Set("DOWNLOAD", "speed_limit_kbps", NumberValue(SpeedLimitBox, 0, 1_048_576, 0).ToString());
        _settings.Config.Set("DOWNLOAD", "cache_strategy", ComboText(CacheStrategyBox, "reuse"));
        _settings.SaveConfig();

        var options = _settings.ReadDownloadOptions();
        var title = installTypes.Count == 0
            ? $"下载 {versionId}"
            : $"下载 {versionId} 并安装 {string.Join(" + ", installTypes.Select(InstallTypeLabel))}";
        AppendDownload($"下载设置：核心并发 {options.MaxCoreConcurrency}，资源并发 {options.MaxAssetConcurrency}，限速 {options.SpeedLimitKbps} KB/s，缓存 {options.CacheStrategy}。");
        _taskManager.Enqueue(
            "download",
            title,
            (record, token) => ExecuteDownloadTaskAsync(record, versionId, mirror, gameDir, javaPath, [.. installTypes], token));
        AppendDownload($"已加入全局队列：{title}");
        await Task.CompletedTask;
    }

    private async Task<string> ExecuteDownloadTaskAsync(
        DownloadTaskRecord record,
        string versionId,
        string mirror,
        string gameDir,
        string javaPath,
        IReadOnlyList<string> installTypes,
        CancellationToken cancellationToken)
    {
        ResetTaskSpeed(record);
        AppendDownload($"正在下载 {versionId}...");
        var (metadata, usedMirror) = await _installServices.GetVersionMetadataWithFallbackAsync(
            versionId,
            mirror,
            message => UpdateFromAnyThread(() =>
            {
                _taskManager.Update(record, item => item.Detail = message);
                AppendDownload(message);
            }),
            cancellationToken);
        var progress = new Progress<DownloadProgress>(snapshot => ApplyDownloadProgress(record, snapshot));
        var summary = await _downloader.DownloadGameFilesAsync(
            metadata,
            gameDir,
            versionId,
            usedMirror,
            IncludeAssetsCheckBox.IsChecked == true,
            progress,
            cancellationToken);

        var resultLines = new List<string>
        {
            $"下载完成：{summary.CompletedJobs}/{summary.TotalJobs}，复用 {summary.ReusedJobs}，写入 {AppFormatter.FormatBytes(summary.DownloadedBytes)}",
        };
        AppendDownload(resultLines[0]);

        if (installTypes.Count > 0)
        {
            var installProgress = new Progress<DownloadSnapshot>(snapshot => ApplyInstallProgress(record, snapshot));
            var engine = new InstallerEngine(
                versionId,
                usedMirror,
                gameDir,
                javaPath,
                message => UpdateFromAnyThread(() =>
                {
                    _taskManager.Update(record, item => item.Detail = message);
                    AppendDownload(message);
                }),
                installProgress,
                _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            var requestedVersions = SelectedInstallVersions(installTypes);
            var result = await engine.InstallSequenceAsync(installTypes, requestedVersions, cancellationToken);
            resultLines.Add(result.Message);
            AppendDownload(result.Message);
        }

        return string.Join("；", resultLines);
    }

    private void CancelCurrentTask(object sender, RoutedEventArgs e)
    {
        var title = _taskManager.ActiveTitle;
        if (!_taskManager.CancelCurrent())
        {
            AppendDownload("当前没有可取消的任务。");
            return;
        }

        AppendDownload($"正在取消任务：{title}");
    }

    private void RetryFailedTask(object sender, RoutedEventArgs e)
    {
        var title = _taskManager.LastFailedTitle;
        if (!_taskManager.RetryLastFailed())
        {
            AppendDownload("当前没有可以重试的失败任务。");
            return;
        }

        AppendDownload($"已重新加入全局队列：{title}");
    }

    private void ClearDownloadQueue(object sender, RoutedEventArgs e)
    {
        var count = _taskManager.ClearQueue();
        AppendDownload($"已清空等待队列：移除 {count} 个任务。");
    }

    private List<string> SelectedInstallTypes()
    {
        var installTypes = new List<string>();
        var installType = ComboTag(InstallTypeBox, "");
        if (!string.IsNullOrWhiteSpace(installType))
        {
            installTypes.Add(installType);
        }

        if (installType == "fabric" && InstallFabricApiCheckBox.IsChecked == true)
        {
            installTypes.Add("fabric_api");
        }

        return installTypes;
    }

    private Dictionary<string, string> SelectedInstallVersions(IReadOnlyList<string> installTypes)
    {
        var selected = ComboText(InstallVersionBox, "自动选择最新");
        if (selected == "自动选择最新" ||
            selected.StartsWith("点击", StringComparison.Ordinal) ||
            selected.StartsWith("正在", StringComparison.Ordinal) ||
            selected == "不安装")
        {
            return [];
        }

        var primaryType = installTypes.FirstOrDefault(type => type is "fabric" or "forge" or "neoforge" or "optifine");
        return string.IsNullOrWhiteSpace(primaryType) ? [] : new Dictionary<string, string> { [primaryType] = selected };
    }

    private void DownloadPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        var presetName = ComboText(DownloadPresetBox, "均衡");
        if (!DownloadPresets.TryGetValue(presetName, out var preset))
        {
            return;
        }

        CoreThreadsBox.Value = preset.CoreThreads;
        AssetThreadsBox.Value = preset.AssetThreads;
        SpeedLimitBox.Value = preset.SpeedLimitKbps;
        SetComboText(CacheStrategyBox, preset.CacheStrategy);
        AppendDownload($"已应用下载预设：{presetName}。");
    }

    private void InstallTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInstallAddonControls();
        ResetInstallVersionChoices("点击刷新安装器版本");
    }

    private async void RefreshInstallVersions(object sender, RoutedEventArgs e)
    {
        var installType = ComboTag(InstallTypeBox, "");
        if (string.IsNullOrWhiteSpace(installType))
        {
            ResetInstallVersionChoices("不安装");
            AppendDownload("当前未选择加载器。");
            return;
        }

        var versionId = RemoteVersionsBox.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(versionId))
        {
            AppendDownload("请先选择远程版本。");
            return;
        }

        try
        {
            ResetInstallVersionChoices("正在刷新...");
            var mirror = ComboText(MirrorBox, "official");
            var versions = await LoadInstallVersionsAsync(installType, versionId, mirror);
            InstallVersionBox.ItemsSource = new[] { "自动选择最新" }.Concat(versions).ToList();
            InstallVersionBox.SelectedIndex = 0;
            AppendDownload(versions.Count == 0
                ? $"未找到 {installType} 的安装器版本，将使用自动选择。"
                : $"已获取 {installType} 安装器版本 {versions.Count} 个。");
        }
        catch (Exception exc)
        {
            ResetInstallVersionChoices("自动选择最新");
            AppendDownload("刷新安装器版本失败：" + exc.Message);
        }
    }

    private async void RefreshLocalInstallVersions(object sender, RoutedEventArgs e)
    {
        await RefreshLocalInstallTargetsAsync();
        AppendDownload("本地安装目标已同步。");
    }

    private async Task RefreshLocalInstallTargetsAsync()
    {
        var request = ++_localVersionScanRequest;
        var gameDirectory = GameDirectoryBox.Text.Trim();
        IReadOnlyList<string> versions;
        try
        {
            versions = await Task.Run(() => _launcher.GetLocalVersions(gameDirectory).ToList());
        }
        catch (Exception exc)
        {
            if (request == _localVersionScanRequest)
            {
                InstallTargetVersionBox.ItemsSource = new[] { "本地版本扫描失败" };
                InstallTargetVersionBox.SelectedIndex = 0;
                UpdateInstallStatus("本地版本扫描失败", exc.Message);
            }

            return;
        }

        if (request != _localVersionScanRequest)
        {
            return;
        }

        InstallTargetVersionBox.ItemsSource = versions.Count == 0 ? new[] { "请先下载本地版本" } : versions;
        InstallTargetVersionBox.SelectedIndex = 0;
        UpdateInstallStatus(versions.Count == 0 ? "没有可安装扩展的本地版本" : "等待安装任务", versions.Count == 0 ? "请先下载原版或选择已有版本目录" : $"发现 {versions.Count} 个本地版本");
    }

    private void AddonInstallTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        ResetAddonInstallVersionChoices("点击刷新安装器版本");
        UpdateAddonInstallButtonText();
    }

    private async void RefreshAddonInstallVersions(object sender, RoutedEventArgs e)
    {
        var installType = ComboText(AddonInstallTypeBox, "fabric");
        var targetVersion = InstallTargetVersionBox.SelectedItem?.ToString() ?? "";
        var gameDir = GameDirectoryBox.Text.Trim();
        var minecraftVersion = NormalizeInstallTargetVersion(gameDir, installType, targetVersion);
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            AppendDownload("请先同步并选择本地目标版本。");
            UpdateInstallStatus("缺少目标版本", "请先同步本地版本");
            return;
        }

        if (installType == "fabric_api")
        {
            ResetAddonInstallVersionChoices("自动选择最新");
            UpdateInstallStatus("Fabric API 将自动选择兼容版本", $"目标 Minecraft：{minecraftVersion}");
            return;
        }

        try
        {
            ResetAddonInstallVersionChoices("正在刷新...");
            UpdateInstallStatus($"正在刷新 {InstallTypeLabel(installType)} 安装器版本", $"目标 Minecraft：{minecraftVersion}");
            var mirror = ComboText(MirrorBox, "official");
            var versions = await LoadInstallVersionsAsync(installType, minecraftVersion, mirror);
            AddonInstallVersionBox.ItemsSource = new[] { "自动选择最新" }.Concat(versions).ToList();
            AddonInstallVersionBox.SelectedIndex = 0;
            UpdateInstallStatus("安装器版本已刷新", versions.Count == 0 ? "未找到可选版本，将使用自动选择" : $"已获取 {versions.Count} 个版本");
            AppendDownload(versions.Count == 0
                ? $"未找到 {installType} 的安装器版本，将使用自动选择。"
                : $"已获取 {installType} 安装器版本 {versions.Count} 个。");
        }
        catch (Exception exc)
        {
            ResetAddonInstallVersionChoices("自动选择最新");
            var message = AppSettings.ConciseDownloadError(exc.Message);
            UpdateInstallStatus("刷新安装器版本失败", message);
            AppendDownload("刷新安装器版本失败：" + message);
        }
    }

    private async void InstallAddonToLocalVersion(object sender, RoutedEventArgs e)
    {
        var installType = ComboText(AddonInstallTypeBox, "fabric");
        var targetVersion = InstallTargetVersionBox.SelectedItem?.ToString() ?? "";
        var gameDir = GameDirectoryBox.Text.Trim();
        var javaPath = JavaPathBox.Text.Trim();
        var minecraftVersion = NormalizeInstallTargetVersion(gameDir, installType, targetVersion);
        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            AppendDownload("请先同步并选择本地目标版本。");
            UpdateInstallStatus("缺少目标版本", "请先下载并选择目标 Minecraft 版本");
            return;
        }

        if (installType is "forge" or "neoforge" && string.IsNullOrWhiteSpace(javaPath))
        {
            AppendDownload($"安装 {installType} 需要可用的 Java 路径。");
            UpdateInstallStatus("缺少 Java", $"安装 {InstallTypeLabel(installType)} 需要可用的 Java");
            return;
        }

        _settings.Config.Set("GAME", "directory", gameDir);
        _settings.Config.Set("JAVA", "path", javaPath);
        _settings.Config.Set("DOWNLOAD", "mirror_source", ComboText(MirrorBox, "official"));
        _settings.SaveConfig();

        var requestedVersion = SelectedAddonInstallVersion();
        var mirror = ComboText(MirrorBox, "official");
        var title = $"安装 {InstallTypeLabel(installType)} 到 {minecraftVersion}";
        if (!string.Equals(targetVersion, minecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            AppendDownload($"安装 {InstallTypeLabel(installType)} 时已从 {targetVersion} 切换到原版 Minecraft {minecraftVersion}。");
        }

        _taskManager.Enqueue(
            "install",
            title,
            (record, token) => ExecuteAddonInstallTaskAsync(record, installType, minecraftVersion, requestedVersion, mirror, gameDir, javaPath, token));
        AppendDownload($"已加入全局队列：{title}");
        await Task.CompletedTask;
    }

    private async Task<string> ExecuteAddonInstallTaskAsync(
        DownloadTaskRecord record,
        string installType,
        string minecraftVersion,
        string requestedVersion,
        string mirror,
        string gameDir,
        string javaPath,
        CancellationToken cancellationToken)
    {
        ResetTaskSpeed(record);
        UpdateFromAnyThread(() =>
        {
            UpdateInstallStatus($"正在准备安装 {InstallTypeLabel(installType)}...", "正在等待安装器返回进度");
            UpdateDownloadMetrics($"准备安装 {installType}...");
        });

        var progress = new Progress<DownloadSnapshot>(snapshot => ApplyInstallProgress(record, snapshot));
        var engine = new InstallerEngine(
            minecraftVersion,
            mirror,
            gameDir,
            javaPath,
            message => UpdateFromAnyThread(() =>
            {
                _taskManager.Update(record, item => item.Detail = message);
                UpdateInstallStatus(message, message);
                AppendDownload(message);
            }),
            progress,
            _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
        var result = await engine.InstallAsync(installType, requestedVersion, cancellationToken);
        UpdateFromAnyThread(() =>
        {
            _ = RefreshLocalInstallTargetsAsync();
            UpdateInstallStatus("安装完成", result.Message);
        });
        return result.Message;
    }

    private async Task<IReadOnlyList<string>> LoadInstallVersionsAsync(string installType, string versionId, string mirror)
    {
        return installType switch
        {
            "fabric" => (await _installServices.GetFabricLoaderVersionsAsync(versionId, mirror))
                .OfType<JsonObject>()
                .Select(item => item["loader"]?["version"]?.GetValue<string>() ?? "")
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList(),
            "forge" => (await _installServices.GetForgeVersionsForMcAsync(versionId, mirror)).Take(80).ToList(),
            "neoforge" => (await _installServices.GetNeoForgeListAsync(versionId, mirror))
                .OfType<JsonObject>()
                .Select(item => item["version"]?.GetValue<string>() ?? item["rawVersion"]?.GetValue<string>() ?? "")
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList(),
            "optifine" => (await _installServices.GetOptifineListAsync(mirror))
                .OfType<JsonObject>()
                .Where(item => string.Equals(item["mcversion"]?.GetValue<string>() ?? "", versionId, StringComparison.OrdinalIgnoreCase))
                .Select(OptifineVersionKey)
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList(),
            _ => [],
        };
    }

    private void UpdateInstallAddonControls()
    {
        var installType = ComboTag(InstallTypeBox, "");
        var isFabric = installType == "fabric";
        var hasInstaller = installType.Length > 0;
        InstallVersionPanel.Visibility = hasInstaller ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(InstallExtrasPanel, hasInstaller ? 1 : 0);
        Grid.SetColumnSpan(InstallExtrasPanel, hasInstaller ? 1 : 2);
        InstallFabricApiCheckBox.IsEnabled = isFabric;
        if (!isFabric)
        {
            InstallFabricApiCheckBox.IsChecked = false;
        }
    }

    private void ResetInstallVersionChoices(string label)
    {
        InstallVersionBox.ItemsSource = new[] { label };
        InstallVersionBox.SelectedIndex = 0;
    }

    private void ResetAddonInstallVersionChoices(string label)
    {
        AddonInstallVersionBox.ItemsSource = new[] { label };
        AddonInstallVersionBox.SelectedIndex = 0;
    }

    private string SelectedAddonInstallVersion()
    {
        var selected = ComboText(AddonInstallVersionBox, "自动选择最新");
        return selected == "自动选择最新" ||
               selected.StartsWith("点击", StringComparison.Ordinal) ||
               selected.StartsWith("正在", StringComparison.Ordinal)
            ? ""
            : selected;
    }

    private string NormalizeInstallTargetVersion(string gameDir, string installType, string selectedVersion)
    {
        if (string.IsNullOrWhiteSpace(selectedVersion) || selectedVersion.StartsWith("请先", StringComparison.Ordinal))
        {
            return "";
        }

        return installType == "fabric_api"
            ? _versionSettingsService.ResolveBaseMinecraftVersion(gameDir, selectedVersion)
            : _versionSettingsService.ResolveBaseMinecraftVersion(gameDir, selectedVersion);
    }

    private void UpdateAddonInstallButtonText()
    {
        if (InstallAddonButton is not null)
        {
            InstallAddonButton.Content = $"开始安装 {InstallTypeLabel(ComboText(AddonInstallTypeBox, "fabric"))}";
        }
    }

    private void AppendDownload(string message)
    {
        AppLog.Info("Download", message);
        DownloadLogBox.Text += (DownloadLogBox.Text.Length == 0 ? "" : Environment.NewLine) + message;
        DownloadLogBox.SelectionStart = DownloadLogBox.Text.Length;
    }

    private void ApplyDownloadProgress(DownloadTaskRecord record, DownloadProgress snapshot)
    {
        var file = Path.GetFileName(snapshot.CurrentFile);
        _taskManager.Update(record, item =>
        {
            var speed = UpdateSpeed(item, snapshot.DownloadedBytes);
            item.Progress = (int)Math.Clamp(Math.Round(snapshot.Progress * 100), 0, 100);
            item.CompletedFiles = snapshot.CompletedJobs;
            item.TotalFiles = snapshot.TotalJobs;
            item.ReusedFiles = snapshot.ReusedJobs;
            item.DownloadedBytes = snapshot.DownloadedBytes;
            item.SpeedBytes = speed;
            item.Detail = $"[{snapshot.Phase}] {snapshot.CompletedJobs}/{snapshot.TotalJobs}，复用 {snapshot.ReusedJobs}，写入 {AppFormatter.FormatBytes(snapshot.DownloadedBytes)}，{AppFormatter.FormatBytes(speed)}/s，{file}";
        });
        DownloadProgressBar.Value = record.Progress;
        UpdateDownloadMetrics(record.Detail);
    }

    private void ApplyInstallProgress(DownloadTaskRecord record, DownloadSnapshot snapshot)
    {
        _taskManager.Update(record, item =>
        {
            var speed = UpdateSpeed(item, snapshot.DownloadedBytes);
            item.Progress = (int)Math.Clamp(Math.Round(snapshot.Progress * 100), 0, 100);
            item.CompletedFiles = snapshot.CompletedFiles;
            item.TotalFiles = snapshot.TotalFiles;
            item.ReusedFiles = snapshot.ReusedFiles;
            item.DownloadedBytes = snapshot.DownloadedBytes;
            item.SpeedBytes = snapshot.SpeedBytes > 0 ? snapshot.SpeedBytes : speed;
            item.Detail = $"[{snapshot.Phase}] {snapshot.CompletedFiles}/{snapshot.TotalFiles}，复用 {snapshot.ReusedFiles}，{AppFormatter.FormatBytes(item.SpeedBytes)}/s，{snapshot.CurrentFile}";
        });
        DownloadProgressBar.Value = record.Progress;
        UpdateInstallStatus(snapshot.Phase, record.Detail);
        UpdateDownloadMetrics(record.Detail);
    }

    private static long UpdateSpeed(DownloadTaskRecord record, long downloadedBytes)
    {
        var now = DateTimeOffset.UtcNow;
        if (record.LastProgressAt == default)
        {
            record.LastProgressAt = now;
            record.LastDownloadedBytes = downloadedBytes;
            return 0;
        }

        var elapsed = Math.Max(0.001, (now - record.LastProgressAt).TotalSeconds);
        var delta = Math.Max(0, downloadedBytes - record.LastDownloadedBytes);
        record.LastProgressAt = now;
        record.LastDownloadedBytes = downloadedBytes;
        return (long)(delta / elapsed);
    }

    private static void ResetTaskSpeed(DownloadTaskRecord record)
    {
        record.LastProgressAt = default;
        record.LastDownloadedBytes = 0;
        record.SpeedBytes = 0;
    }

    private void UpdateTaskCenter()
    {
        var active = _taskManager.ActiveRecord;
        var activeTitle = _taskManager.ActiveTitle ?? "无";
        var nextTitle = _taskManager.NextTitle ?? "无";
        CurrentTaskText.Text = $"当前任务：{activeTitle}";
        TaskQueueText.Text = $"队列：{_taskManager.QueuedCount} | 下一个：{nextTitle}";
        UpdateDownloadQueueSummary();
        CancelTaskButton.IsEnabled = active is not null && !_taskManager.IsCancelRequested;
        RetryTaskButton.IsEnabled = _taskManager.LastFailedTitle is not null && active is null;
        ClearQueueButton.IsEnabled = _taskManager.QueuedCount > 0;
        if (active is not null)
        {
            DownloadProgressBar.Value = active.Progress;
            UpdateDownloadMetrics(active.Detail);
        }
        else if (_taskManager.QueuedCount == 0)
        {
            DownloadProgressBar.Value = _taskManager.Records.LastOrDefault()?.Progress ?? 0;
        }

        TaskRecordsList.ItemsSource = RenderTaskRecords();
    }

    private void UpdateDownloadMetrics(string message)
    {
        if (DownloadMetricsText is not null)
        {
            DownloadMetricsText.Text = string.IsNullOrWhiteSpace(message) ? "等待下载" : message;
        }
    }

    private void UpdateDownloadQueueSummary()
    {
        if (DownloadQueueSummaryText is null)
        {
            return;
        }

        var active = _taskManager.ActiveTitle ?? "无";
        var next = _taskManager.NextTitle ?? "无";
        DownloadQueueSummaryText.Text = _taskManager.ActiveRecord is null && _taskManager.QueuedCount == 0
            ? "任务队列：空"
            : $"当前任务：{active} | 队列：{_taskManager.QueuedCount} | 下一个：{next}";
    }

    private void UpdateInstallStatus(string status, string metrics)
    {
        if (InstallStatusText is not null)
        {
            InstallStatusText.Text = status;
        }

        if (InstallMetricsText is not null)
        {
            InstallMetricsText.Text = string.IsNullOrWhiteSpace(metrics) ? "尚未开始安装" : metrics;
        }
    }

    private IReadOnlyList<string> RenderTaskRecords()
    {
        var records = _taskManager.Records;
        if (records.Count == 0)
        {
            return ["暂无下载任务"];
        }

        var order = new Dictionary<string, int>
        {
            ["running"] = 0,
            ["queued"] = 1,
            ["failed"] = 2,
            ["canceled"] = 3,
            ["completed"] = 4,
        };
        var items = new List<string>();
        foreach (var record in records
                     .OrderBy(record => order.GetValueOrDefault(record.Status, 9))
                     .ThenBy(record => record.Status is "running" or "queued" ? record.Id : -record.Id)
                     .Take(50))
        {
            var builder = new System.Text.StringBuilder();
            builder.Append('[').Append(StatusLabel(record.Status)).Append("] ")
                .Append(record.Title).AppendLine();
            builder.Append(record.Progress).Append("%");
            if (record.TotalFiles > 0)
            {
                builder.Append(" | ")
                    .Append(record.CompletedFiles).Append('/').Append(record.TotalFiles)
                    .Append(" | 复用 ").Append(record.ReusedFiles);
            }

            if (record.DownloadedBytes > 0)
            {
                builder.Append(" | 写入 ").Append(AppFormatter.FormatBytes(record.DownloadedBytes));
            }

            if (record.SpeedBytes > 0)
            {
                builder.Append(" | ").Append(AppFormatter.FormatBytes(record.SpeedBytes)).Append("/s");
            }

            if (!string.IsNullOrWhiteSpace(record.Detail))
            {
                builder.AppendLine();
                builder.Append(record.Detail);
            }

            items.Add(builder.ToString());
        }

        return items;
    }

    private void UpdateFromAnyThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }

    private static string ComboText(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? fallback;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }

    private static string ComboTag(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is not null)
            {
                return item.Tag.ToString() ?? fallback;
            }

            var content = item.Content?.ToString() ?? "";
            return string.Equals(content, "不安装", StringComparison.OrdinalIgnoreCase) ? fallback : content;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }

    private static void SetComboText(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static void SetComboTag(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString() ?? "", value, StringComparison.OrdinalIgnoreCase))
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

    private static string OptifineVersionKey(JsonObject item)
    {
        var filename = item["filename"]?.GetValue<string>()?.Trim() ?? "";
        if (filename.Length > 0)
        {
            return filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? filename[..^4] : filename;
        }

        var type = item["type"]?.GetValue<string>()?.Trim() ?? "";
        var patch = item["patch"]?.GetValue<string>()?.Trim() ?? "";
        return string.Join('_', new[] { type, patch }.Where(value => value.Length > 0));
    }

    private static string InstallTypeLabel(string installType)
        => installType switch
        {
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "fabric_api" => "Fabric API",
            "optifine" => "OptiFine",
            _ => installType,
        };

    private static string StatusLabel(string status)
        => status switch
        {
            "queued" => "等待中",
            "running" => "进行中",
            "completed" => "已完成",
            "failed" => "失败",
            "canceled" => "已取消",
            _ => status,
        };

    private sealed record DownloadPreset(int CoreThreads, int AssetThreads, int SpeedLimitKbps, string CacheStrategy);
}
