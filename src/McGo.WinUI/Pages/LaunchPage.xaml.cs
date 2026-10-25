using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.WinUI.Dialogs;
using Microsoft.UI.Xaml.Media;
using McGo.Core.App;
using McGo.Core.Authentication;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Formatting;
using McGo.Core.Java;
using McGo.Core.Minecraft;
using McGo.Core.Resources;
using McGo.WinUI.Services;

namespace McGo.WinUI.Pages;

/// <summary>版本列表项：把纯字符串换成带图标与摘要的可视项。</summary>
public sealed class VersionListItem
{
    public VersionListItem(string versionId, string versionType, string? alias, bool favorite)
    {
        VersionId = versionId;
        IsFavorite = favorite;
        DisplayName = string.IsNullOrWhiteSpace(alias) ? versionId : $"{alias}（{versionId}）";
        Summary = BuildSummary(versionId, versionType, favorite);
        IconGlyph = BuildGlyph(versionType, favorite);
    }

    public string VersionId { get; }

    public bool IsFavorite { get; }

    public string DisplayName { get; }

    public string Summary { get; }

    public string IconGlyph { get; }

    public override string ToString() => VersionId;

    private static string BuildSummary(string versionId, string versionType, bool favorite)
    {
        var kind = versionType switch
        {
            "release" => "正式版",
            "snapshot" => "快照",
            "old_beta" => "远古 Beta",
            "old_alpha" => "远古 Alpha",
            _ => string.IsNullOrWhiteSpace(versionType) ? "未知类型" : versionType,
        };
        return favorite ? $"{kind} · 已收藏" : kind;
    }

    private static string BuildGlyph(string versionType, bool favorite)
    {
        if (favorite)
        {
            return "\uE735"; // 星形
        }

        return versionType switch
        {
            "release" => "\uE7FC",  // 方块
            "snapshot" => "\uE7C3", // 实验锥
            "old_beta" => "\uE81C", // 历史
            "old_alpha" => "\uE81C",
            _ => "\uE7FC",
        };
    }
}

public sealed partial class LaunchPage : Page
{
    private readonly AppSettings _settings = new();
    private readonly LauncherService _launcher = new();
    private readonly VersionSettingsService _versionSettingsService;
    private readonly LaunchPreflightService _launchPreflight = new();
    private readonly DownloaderService _downloader = new();
    private readonly ResourceMarketService _resourceMarket = new();
    private readonly MicrosoftAuthenticator _microsoftAuthenticator = new(AppConstants.ClientId, AppConstants.RedirectUri);
    private JsonObject _versionSettings = [];
    private List<JsonObject> _accounts = [];
    private readonly Dictionary<string, JsonObject> _accountsByLabel = [];
    private readonly Dictionary<string, LocalResourceInfo> _versionModsByLabel = [];
    private readonly List<string> _quickPlayWorldIds = [""];
    private bool _initialized;
    private bool _loadingVersion;
    private bool _loadingJavaCandidates;
    private bool _responsiveLayoutInitialized;
    private bool _wideLayout;
    private int _javaEnvironmentRequest;
    private int _versionRefreshRequest;

    public LaunchPage()
    {
        InitializeComponent();
        _versionSettingsService = new VersionSettingsService(_launcher);
        VersionSectionBar.SelectedItem = VersionSectionBar.Items[0];
        LoadState();
        _initialized = true;
        Loaded += LaunchPage_Loaded;
    }

    private async void LaunchPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LaunchPage_Loaded;
        await Task.WhenAll(
            RefreshJavaEnvironmentAsync(JavaPathBox.Text, showFeedback: false),
            RefreshVersionListAsync());
    }

    private void VersionSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        var section = (sender.SelectedItem as SelectorBarItem)?.Tag?.ToString() ?? "selector";
        VersionSelectorPanel.Visibility = section == "selector" ? Visibility.Visible : Visibility.Collapsed;
        VersionSettingsPanel.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadState()
    {
        _settings.LoadConfig();
        _versionSettings = _versionSettingsService.LoadVersionSettings();
        _accounts = _settings.LoadAccounts();
        ApplyAdvancedVisibility();
        _resourceMarket.ModrinthApiBase = _settings.Config.Get("DOWNLOAD", "modrinth_api_base", "");
        if (string.IsNullOrWhiteSpace(_resourceMarket.ModrinthApiBase))
        {
            _resourceMarket.ModrinthApiBase = "https://api.modrinth.com/v2";
        }

        _resourceMarket.ModrinthCdnBase = _settings.Config.Get("DOWNLOAD", "modrinth_cdn_base", "");
        GameDirectoryBox.Text = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        JavaPathBox.Text = _settings.Config.Get("JAVA", "path", "java");
        MemorySlider.Value = ClampDouble(AppSettings.RecommendedMemoryMb(), 1024, 65536);
        UpdateMemoryControls();
        JavaVersionText.Text = "正在检测 Java...";
        SetJavaDownloadStatus("可自动下载当前版本推荐的 Java 运行时", 0);
        RefreshAccountsList();
        OutputBox.Text = "正在扫描本地版本...";
        SetVersionSummaryText("正在扫描本地版本...");
        UpdateLaunchStatus("等待启动", 0, "当前步骤：等待启动");
    }

    private async void RefreshVersions(object sender, RoutedEventArgs e) => await RefreshVersionListAsync();

    private void OpenDownloadPage(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow window)
        {
            window.NavigateToTag("download");
        }
    }

    private async void VersionCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        await RefreshVersionListAsync();
    }

    private async Task RefreshVersionListAsync()
    {
        if (VersionsList is null || GameDirectoryBox is null || VersionCategoryBox is null || OutputBox is null)
        {
            return;
        }

        var selected = SelectedVersionId;
        var gameDir = GameDirectoryBox.Text.Trim();
        var category = ComboText(VersionCategoryBox, "全部版本");
        var request = ++_versionRefreshRequest;
        IReadOnlyList<LocalVersionCandidate> candidates;
        try
        {
            candidates = await Task.Run(() => _launcher.GetLocalVersions(gameDir)
                .Select(versionId => new LocalVersionCandidate(versionId, _versionSettingsService.DetectVersionType(gameDir, versionId)))
                .ToList());
        }
        catch (Exception exc)
        {
            if (request == _versionRefreshRequest)
            {
                OutputBox.Text = "扫描本地版本失败：" + exc.Message;
                SetVersionSummaryText("本地版本扫描失败");
            }

            return;
        }

        if (request != _versionRefreshRequest)
        {
            return;
        }

        var allVersions = candidates.Select(candidate => candidate.VersionId).ToList();
        var versions = candidates
            .Where(candidate => VersionVisibleInCategory(candidate.VersionId, candidate.DetectedType, category))
            .Select(candidate =>
            {
                var entry = VersionSettingsService.VersionSettingsEntry(_versionSettings, candidate.VersionId);
                return new VersionListItem(
                    candidate.VersionId,
                    candidate.DetectedType,
                    entry["alias"]?.GetValue<string>(),
                    entry["favorite"]?.GetValue<bool>() == true);
            })
            .OrderBy(item => item.IsFavorite ? 0 : 1)
            .ThenBy(item => item.VersionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        VersionsList.ItemsSource = versions;
        NoVersionsPanel.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VersionsList.Visibility = versions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // 空列表时不显示摘要，避免“空盒子 + 一行说明”的松散布局
        VersionSummaryText.Visibility = versions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var restored = selected is null ? null : versions.FirstOrDefault(item => item.VersionId == selected);
        if (restored is not null)
        {
            VersionsList.SelectedItem = restored;
        }
        else if (versions.Count > 0)
        {
            VersionsList.SelectedIndex = 0;
        }

        OutputBox.Text = allVersions.Count == 0 ? "没有发现本地版本。" : $"发现 {allVersions.Count} 个本地版本，当前显示 {versions.Count} 个。";
        if (allVersions.Count == 0)
        {
            SetVersionSummaryText("未选择版本");
            UpdateLaunchStatus("当前游戏目录下没有可启动的本地版本", 0, "当前步骤：等待版本");
        }
        else if (versions.Count == 0)
        {
            SetVersionSummaryText("当前分类没有可显示的版本");
            UpdateLaunchStatus("当前分类没有可显示的版本", 0, "当前步骤：等待版本");
        }

        RefreshVersionMods();
    }

    private bool VersionVisibleInCategory(string versionId, string detectedType, string category)
    {
        var entry = VersionSettingsService.VersionSettingsEntry(_versionSettings, versionId);
        var hidden = entry["hidden"]?.GetValue<bool>() == true;
        var favorite = entry["favorite"]?.GetValue<bool>() == true;
        return category switch
        {
            "隐藏" => hidden,
            "收藏" => favorite && !hidden,
            "原版" => !hidden && detectedType == "正式版",
            "快照" => !hidden && detectedType == "快照",
            "远古版本" => !hidden && detectedType is "远古 Beta" or "远古 Alpha",
            "仅 OptiFine" => !hidden && detectedType == "OptiFine",
            "可安装 Mod" => !hidden && detectedType is "Fabric" or "Forge" or "NeoForge" or "OptiFine",
            _ => !hidden,
        };
    }

    private void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedVersionId is not string versionId)
        {
            return;
        }


        var entry = VersionSettingsService.VersionSettingsEntry(_versionSettings, versionId);
        _loadingVersion = true;
        VersionAliasBox.Text = entry["alias"]?.GetValue<string>() ?? "";
        ExtraJvmArgsBox.Text = entry["jvm_args"]?.GetValue<string>() ?? "";
        ExtraGameArgsBox.Text = entry["game_args"]?.GetValue<string>() ?? "";
        PreLaunchCommandBox.Text = entry["pre_launch_command"]?.GetValue<string>() ?? "";
        var maxMemory = PositiveInt(entry["max_memory_mb"], 0);
        ManualMemoryCheckBox.IsChecked = entry["manual_memory"]?.GetValue<bool>() == true || maxMemory > 0;
        MemorySlider.Value = ClampDouble(maxMemory > 0 ? maxMemory : AppSettings.RecommendedMemoryMb(), 1024, 65536);
        MinMemoryBox.Value = ClampDouble(PositiveInt(entry["min_memory_mb"], 0), 0, 65536);
        WindowWidthBox.Value = ClampDouble(PositiveInt(entry["window_width"], 0), 0, 16384);
        WindowHeightBox.Value = ClampDouble(PositiveInt(entry["window_height"], 0), 0, 16384);
        RuntimeDirectoryBox.Text = entry["runtime_directory"]?.GetValue<string>() ?? "";
        CustomModsDirectoryBox.Text = entry["mods_directory"]?.GetValue<string>() ?? "";
        SetComboText(GcStrategyBox, entry["gc_strategy"]?.GetValue<string>() ?? "G1GC");
        SetComboText(VersionIconBox, entry["icon"]?.GetValue<string>() ?? "自动");
        UseIsolationCheckBox.IsChecked = entry["use_isolated_directory"]?.GetValue<bool>() ?? false;
        VersionFavoriteCheckBox.IsChecked = entry["favorite"]?.GetValue<bool>() ?? false;
        VersionHiddenCheckBox.IsChecked = entry["hidden"]?.GetValue<bool>() ?? false;
        RefreshQuickPlayWorlds(entry["quick_play_singleplayer"]?.GetValue<string>() ?? "");
        UpdateMemoryControls();
        UpdateVersionSummary(versionId);
        RefreshVersionMods();
        UpdateLaunchStatus("已选择版本，等待启动", 0, "当前步骤：等待启动");
        _loadingVersion = false;
    }


    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        _settings.Config.Set("GAME", "directory", GameDirectoryBox.Text.Trim());
        _settings.Config.Set("USER", "username", SelectedAccountName() ?? "");
        _settings.Config.Set("JAVA", "path", JavaPathBox.Text.Trim());
        _settings.SaveConfig();
        await RefreshVersionListAsync();
        await DetectJavaVersionAsync(JavaPathBox.Text);
        UpdateLaunchMethod();
        OutputBox.Text = "设置已保存。";
    }

    private async void RefreshJavaCandidates(object sender, RoutedEventArgs e)
    {
        await RefreshJavaEnvironmentAsync(JavaPathBox.Text.Trim(), showFeedback: true);
        UpdateLaunchStatus($"已刷新 Java 候选：{JavaCandidatesBox.Items.Count} 项。", 0, "当前步骤：检测 Java");
        OutputBox.Text = $"已刷新 Java 候选：{JavaCandidatesBox.Items.Count} 项。";
    }

    private async void JavaCandidateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingJavaCandidates)
        {
            return;
        }

        if (JavaCandidatesBox.SelectedItem is string path && path.Length > 0)
        {
            JavaPathBox.Text = path;
            await DetectJavaVersionAsync(path);
            UpdateVersionSummary(SelectedVersionId);
        }
    }

    private async Task RefreshJavaEnvironmentAsync(string selectedPath, bool showFeedback)
    {
        var request = ++_javaEnvironmentRequest;
        var gameDirectory = GameDirectoryBox.Text.Trim();
        var javaPath = selectedPath.Trim().Length == 0 ? "java" : selectedPath.Trim();
        JavaVersionText.Text = "正在检测 Java...";
        if (showFeedback)
        {
            UpdateLaunchStatus("正在扫描 Java 运行时...", 0, "当前步骤：检测 Java");
        }

        var candidatesTask = Task.Run(() => FindJavaCandidates(gameDirectory, javaPath));
        var majorTask = Task.Run(() => JavaRuntimeService.GetJavaMajorVersion(javaPath));
        try
        {
            await Task.WhenAll(candidatesTask, majorTask);
        }
        catch (Exception exc)
        {
            if (request == _javaEnvironmentRequest)
            {
                JavaVersionText.Text = "Java 检测失败";
                SetJavaDownloadStatus("Java 扫描失败：" + exc.Message, 0);
            }

            return;
        }

        if (request != _javaEnvironmentRequest)
        {
            return;
        }

        _loadingJavaCandidates = true;
        try
        {
            var candidates = await candidatesTask;
            JavaCandidatesBox.ItemsSource = candidates;
            if (candidates.Count > 0)
            {
                var explicitMatch = candidates.FirstOrDefault(candidate => SamePathOrText(candidate, javaPath));
                JavaCandidatesBox.SelectedItem = explicitMatch ?? PickJavaForSelectedVersion(candidates) ?? candidates[0];
            }
        }
        finally
        {
            _loadingJavaCandidates = false;
        }

        UpdateJavaVersionStatus(javaPath, await majorTask);
    }

    /// <summary>
    /// 按当前选中版本需要的 Java 大版本，从候选里挑最合适的一个。
    /// 规则：
    /// 1) 有大版本与需求完全一致的，优先选它（启动成功率最高）；
    /// 2) 否则选第一个「不低于」需求的（Java 向后兼容，新版能跑旧版本）；
    /// 3) 都低于需求时返回 null，让调用方回退到原逻辑，同时在状态里提示用户。
    /// </summary>
    private string? PickJavaForSelectedVersion(IReadOnlyList<string> candidates)
    {
        var versionId = SelectedVersionId;
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return null;
        }

        var required = _launcher.InferRequiredJavaVersion(GameDirectoryBox.Text.Trim(), versionId);
        if (required <= 0)
        {
            return null;
        }

        var detected = new List<(string Path, int Major)>();
        foreach (var candidate in candidates)
        {
            var major = JavaRuntimeService.GetJavaMajorVersion(candidate);
            if (major > 0)
            {
                detected.Add((candidate, major));
            }
        }

        if (detected.Count == 0)
        {
            return null;
        }

        var exact = detected.FirstOrDefault(item => item.Major == required);
        if (exact.Path is not null)
        {
            return exact.Path;
        }

        // 不做「高版本也能跑」的推断：Java 的向后兼容在实际游戏里很差，
        // 例如 Java 21 跑 1.12.2 的 Forge 基本必崩。版本对不上就明确提示，
        // 让用户装对的运行时，而不是选一个看起来能启动、实际会炸的。
        var found = string.Join("、", detected.Select(item => $"Java {item.Major}").Distinct());
        SetJavaDownloadStatus(
            $"当前版本需要 Java {required}，但只找到 {found}。Java 的向后兼容不可靠，请点「下载 Java」安装匹配的运行时。",
            0);
        return null;
    }

    private async Task<int> DetectJavaVersionAsync(string path)
    {
        var request = ++_javaEnvironmentRequest;
        var javaPath = path.Trim().Length == 0 ? "java" : path.Trim();
        JavaVersionText.Text = "正在检测 Java...";
        var major = await Task.Run(() => JavaRuntimeService.GetJavaMajorVersion(javaPath));
        if (request == _javaEnvironmentRequest)
        {
            UpdateJavaVersionStatus(javaPath, major);
        }

        return major;
    }

    private async void SaveVersionSettings(object sender, RoutedEventArgs e)
    {
        if (SelectedVersionId is not string versionId)
        {
            OutputBox.Text = "请先选择一个版本。";
            return;
        }

        var entry = VersionSettingsService.VersionSettingsEntry(_versionSettings, versionId);
        entry["alias"] = VersionAliasBox.Text.Trim();
        entry["quick_play_singleplayer"] = CurrentQuickPlayWorld();
        entry["jvm_args"] = ExtraJvmArgsBox.Text.Trim();
        entry["game_args"] = ExtraGameArgsBox.Text.Trim();
        entry["pre_launch_command"] = PreLaunchCommandBox.Text.Trim();
        entry["use_isolated_directory"] = UseIsolationCheckBox.IsChecked == true;
        entry["favorite"] = VersionFavoriteCheckBox.IsChecked == true;
        entry["hidden"] = VersionHiddenCheckBox.IsChecked == true;
        entry["manual_memory"] = ManualMemoryCheckBox.IsChecked == true;
        entry["max_memory_mb"] = ManualMemoryCheckBox.IsChecked == true ? NumberValue(MemorySlider, 1024, 65536, AppSettings.RecommendedMemoryMb()) : 0;
        entry["min_memory_mb"] = NumberValue(MinMemoryBox, 0, 65536, 0);
        entry["window_width"] = NumberValue(WindowWidthBox, 0, 16384, 0);
        entry["window_height"] = NumberValue(WindowHeightBox, 0, 16384, 0);
        entry["gc_strategy"] = ComboText(GcStrategyBox, "G1GC");
        entry["icon"] = ComboText(VersionIconBox, "自动");
        entry["runtime_directory"] = RuntimeDirectoryBox.Text.Trim();
        entry["mods_directory"] = CustomModsDirectoryBox.Text.Trim();
        _versionSettingsService.SaveVersionSettings(_versionSettings);
        await RefreshVersionListAsync();
        UpdateVersionSummary(versionId);
        UpdateLaunchStatus("版本设置已保存。", 0, "当前步骤：保存设置");
        OutputBox.Text = "版本设置已保存。";
    }

    private async void CheckJavaRuntime(object sender, RoutedEventArgs e)
    {
        var javaPath = JavaPathBox.Text.Trim().Length == 0 ? "java" : JavaPathBox.Text.Trim();
        var major = await DetectJavaVersionAsync(javaPath);
        var required = SelectedVersionId is string versionId
            ? _launcher.InferRequiredJavaVersion(GameDirectoryBox.Text.Trim(), versionId)
            : 0;
        UpdateJavaVersionStatus(javaPath, major);
        SetJavaDownloadStatus(major == 0
            ? $"无法识别 Java：{javaPath}"
            : required > 0 && major < required
                ? $"当前 Java {major} 低于所需 Java {required}，可下载推荐运行时"
                : $"当前 Java {major} 可用于启动", 0);
        UpdateLaunchStatus(major == 0
            ? "无法识别当前 Java"
            : required > 0 && major < required
                ? $"当前 Java {major} 低于所需 Java {required}"
                : "Java 版本满足要求", 0, "当前步骤：检测 Java");
        OutputBox.Text = major == 0
            ? $"无法识别 Java 版本：{javaPath}"
            : required > 0
                ? $"当前 Java：{major}{Environment.NewLine}所选版本需要：Java {required}{Environment.NewLine}{(major >= required ? "Java 版本满足要求。" : "Java 版本过低，请下载推荐 Java 或更换路径。")}"
                : $"当前 Java：{major}";
    }

    private void DownloadRecommendedJava(object sender, RoutedEventArgs e)
    {
        if (SelectedVersionId is not string versionId)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        var required = _launcher.InferRequiredJavaVersion(GameDirectoryBox.Text.Trim(), versionId);
        var installRoot = Path.Combine(GameDirectoryBox.Text.Trim(), "java_runtimes");
        UpdateLaunchStatus($"Java {required} 已加入全局队列", 0, "当前步骤：等待下载 Java");
        DownloadTaskManager.Current.Enqueue("java", $"下载 Java {required}", (record, token) =>
            DownloadRecommendedJavaAsync(record, required, installRoot, token));
    }

    private async Task<string> DownloadRecommendedJavaAsync(DownloadTaskRecord record, int required, string installRoot, CancellationToken cancellationToken)
    {
        try
        {
            UpdateLaunchStatus($"正在下载 Java {required}...", 0, "当前步骤：下载 Java");
            SetJavaDownloadStatus($"准备下载 Java {required}...", 0);
            OutputBox.Text = $"正在下载 Java {required}...";
            var progress = new Progress<JavaRuntimeDownloadProgress>(snapshot =>
            {
                var total = snapshot.TotalBytes > 0 ? AppFormatter.FormatBytes(snapshot.TotalBytes) : "未知大小";
                var percent = snapshot.TotalBytes > 0 ? (int)Math.Clamp(snapshot.DownloadedBytes * 100 / snapshot.TotalBytes, 0, 100) : 0;
                SetLaunchProgress(percent, $"当前步骤：{snapshot.Phase}");
                SetJavaDownloadStatus($"{snapshot.Phase}：{snapshot.CurrentItem}", percent);
                LaunchStatusText.Text = $"{snapshot.Phase}：{snapshot.CurrentItem}";
                OutputBox.Text = $"{snapshot.Phase}：{snapshot.CurrentItem}{Environment.NewLine}{AppFormatter.FormatBytes(snapshot.DownloadedBytes)} / {total}";
                DownloadTaskManager.Current.Update(record, item =>
                {
                    item.Progress = percent;
                    item.DownloadedBytes = snapshot.DownloadedBytes;
                    item.Detail = $"{snapshot.Phase}：{snapshot.CurrentItem}";
                });
            });
            var result = await JavaRuntimeService.DownloadJavaRuntimeAsync(required, installRoot, progress: progress, cancellationToken: cancellationToken);
            JavaPathBox.Text = result.JavaPath;
            _settings.Config.Set("JAVA", "path", result.JavaPath);
            _settings.SaveConfig();
            UpdateJavaVersionStatus(result.JavaPath, required);
            SetJavaDownloadStatus(result.Reused ? $"Java {required} 已安装：{result.JavaPath}" : $"Java {required} 下载完成：{result.JavaPath}", 100);
            UpdateLaunchStatus(result.Reused ? $"已复用 Java {required}" : $"Java {required} 下载完成", 100, "当前步骤：Java 就绪");
            OutputBox.Text = string.Join(Environment.NewLine, [
                result.Reused ? $"已复用 Java {required}" : $"Java {required} 下载完成",
                $"路径：{result.JavaPath}",
                $"安装目录：{result.InstallDirectory}",
            ]);
            return result.Reused ? $"已复用 Java {required}" : $"Java {required} 下载完成";
        }
        catch (OperationCanceledException)
        {
            UpdateLaunchStatus("Java 下载已取消", 0, "当前步骤：已取消");
            SetJavaDownloadStatus("Java 下载已取消", 0);
            OutputBox.Text = "Java 下载已取消。";
            throw;
        }
        catch (Exception exc)
        {
            UpdateLaunchStatus("下载 Java 失败", 0, "当前步骤：下载 Java");
            SetJavaDownloadStatus("Java 下载失败：" + AppSettings.ConciseDownloadError(exc.Message), 0);
            OutputBox.Text = "下载 Java 失败：" + AppSettings.ConciseDownloadError(exc.Message);
            throw;
        }
    }

    private async void PreviewCommand(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateLaunchStatus("正在生成启动命令...", 10, "当前步骤：生成命令");
            var account = await RefreshSelectedAccountForLaunchAsync();
            UpdateLaunchMethod(account);
            OutputBox.Text = string.Join(Environment.NewLine, BuildSelectedLaunchCommand(account));
            UpdateLaunchStatus("启动命令已生成。", 100, "当前步骤：生成命令");
        }
        catch (Exception exc)
        {
            UpdateLaunchStatus("生成命令失败", 0, "当前步骤：生成命令");
            OutputBox.Text = exc.Message;
        }
    }

    /// <summary>
    /// 单一主按钮：按当前缺口决定动作，让第一次使用的用户不需要理解页面结构。
    /// 缺账号 → 去加账号；缺版本 → 去下载；都齐了 → 启动游戏。
    /// </summary>
    private void PrimaryLaunchAction(object sender, RoutedEventArgs e)
    {
        switch (_nextStep)
        {
            case LaunchNextStep.Account:
                NavigateToTag("accounts");
                return;
            case LaunchNextStep.Version:
                NavigateToTag("download");
                return;
            default:
                LaunchGame(sender, e);
                return;
        }
    }

    private void NavigateToTag(string tag)
    {
        if (App.MainWindow is MainWindow window)
        {
            window.NavigateToTag(tag);
        }
    }

    /// <summary>当前选中的版本 ID；列表项是 VersionListItem，统一从这里取。</summary>
    private string? SelectedVersionId => (VersionsList?.SelectedItem as VersionListItem)?.VersionId;

    private enum LaunchNextStep
    {
        Account,
        Version,
        Launch,
    }

    private LaunchNextStep _nextStep = LaunchNextStep.Version;

    /// <summary>
    /// 刷新左侧身份区与主按钮。
    /// 主按钮本身承载“现在能不能玩”：缺账号→去加账号，缺版本→去下载，齐了→启动。
    /// </summary>
    private void UpdateLaunchReadiness()
    {
        if (PrimaryLaunchText is null)
        {
            return;
        }

        var account = SelectedAccount();
        var hasAccount = account is not null;
        var versionId = SelectedVersionId;
        var hasVersion = !string.IsNullOrWhiteSpace(versionId);
        var javaPath = JavaPathBox.Text.Trim();
        var javaMajor = JavaRuntimeService.GetJavaMajorVersion(javaPath.Length == 0 ? "java" : javaPath);

        // 身份区：头像用名字首字，符合 Minecraft 玩家习惯
        var name = SelectedAccountName();
        AccountNameText.Text = hasAccount ? name ?? "未命名" : "未登录";
        AccountTypeText.Text = hasAccount
            ? AccountTypeLabel(account!) + (javaMajor > 0 ? $" · Java {javaMajor}" : "")
            : "还没有账号";
        AvatarText.Text = string.IsNullOrWhiteSpace(name)
            ? "?"
            : name.Trim()[..1].ToUpperInvariant();

        if (!hasAccount)
        {
            _nextStep = LaunchNextStep.Account;
            PrimaryLaunchText.Text = "添加账号";
            PrimaryLaunchIcon.Symbol = Symbol.Contact;
            PrimaryLaunchSubText.Text = "离线账号只需一个用户名";
            return;
        }

        if (!hasVersion)
        {
            _nextStep = LaunchNextStep.Version;
            PrimaryLaunchText.Text = "下载版本";
            PrimaryLaunchIcon.Symbol = Symbol.Download;
            PrimaryLaunchSubText.Text = "还没有可启动的版本";
            return;
        }

        _nextStep = LaunchNextStep.Launch;
        PrimaryLaunchText.Text = "启动游戏";
        PrimaryLaunchIcon.Symbol = Symbol.Play;
        PrimaryLaunchSubText.Text = versionId!;
    }

    /// <summary>左侧的“版本选择 / 版本设置”把注意力带到右侧对应分段。</summary>
    private void ShowVersionSelectorTab(object sender, RoutedEventArgs e)
    {
        VersionSectionBar.SelectedItem = VersionSectionBar.Items[0];
        VersionCenterPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void ShowVersionSettingsTab(object sender, RoutedEventArgs e)
    {
        VersionSectionBar.SelectedItem = VersionSectionBar.Items[1];
        VersionCenterPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void GoAccounts(object sender, RoutedEventArgs e) => NavigateToTag("accounts");

    private async void LaunchGame(object sender, RoutedEventArgs e)
    {
        try
        {
            var preflight = ValidateSelectedLaunchPreflight();
            if (!preflight.IsOk)
            {
                UpdateLaunchStatus("启动前检查未通过，请先修复版本文件", 0, "当前步骤：启动前检查");
                var lines = new List<string> { "启动前检查未通过：" };
                lines.AddRange(preflight.Issues.Take(8).Select(issue => issue.Message));
                if (preflight.Issues.Count > 8)
                {
                    lines.Add($"还有 {preflight.Issues.Count - 8} 项未列出。");
                }

                lines.Add("");
                lines.Add("请先执行“补全/校验文件”，或更换满足要求的 Java。");
                OutputBox.Text = string.Join(Environment.NewLine, lines);
                return;
            }

            UpdateLaunchStatus("正在刷新账号并准备启动...", 15, "当前步骤：准备账号");
            var account = await RefreshSelectedAccountForLaunchAsync();
            UpdateLaunchMethod(account);
            SetLaunchProgress(35, "当前步骤：启动前命令");
            RunPreLaunchCommand();
            SetLaunchProgress(65, "当前步骤：生成命令");
            var command = BuildSelectedLaunchCommand(account);
            var startInfo = new ProcessStartInfo(command[0]) { UseShellExecute = false };
            foreach (var argument in command.Skip(1))
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            MusicPlaybackService.PauseForLaunchIfConfigured();
            UpdateLaunchStatus("Minecraft 已启动。", 100, "当前步骤：已启动");
            OutputBox.Text = "Minecraft 已启动。";
        }
        catch (Exception exc)
        {
            UpdateLaunchStatus("启动失败：" + exc.Message, 0, "当前步骤：启动失败");
            OutputBox.Text = "启动失败：" + exc.Message;
        }
    }

    private void RunPreLaunchCommand()
    {
        if (SelectedVersionId is not string versionId)
        {
            return;
        }

        var options = VersionSettingsService.LaunchOptionsForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
        if (string.IsNullOrWhiteSpace(options.PreLaunchCommand))
        {
            return;
        }

        Directory.CreateDirectory(options.RuntimeDirectory);
        var shell = Environment.GetEnvironmentVariable("COMSPEC");
        var startInfo = OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(shell)
            ? new ProcessStartInfo(shell, "/c " + options.PreLaunchCommand)
            : new ProcessStartInfo("/bin/sh", "-c " + options.PreLaunchCommand);
        startInfo.WorkingDirectory = options.RuntimeDirectory;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("启动前命令无法执行。");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"启动前命令失败，退出码 {process.ExitCode}。");
        }
    }

    private LaunchPreflightResult ValidateSelectedLaunchPreflight()
    {
        if (SelectedVersionId is not string versionId)
        {
            return new LaunchPreflightResult([new LaunchPreflightIssue("version", "请先选择本地游戏版本。")], 0, 1);
        }

        return _launchPreflight.Validate(GameDirectoryBox.Text.Trim(), versionId, JavaPathBox.Text.Trim().Length == 0 ? "java" : JavaPathBox.Text.Trim(), includeAssets: true);
    }

    private void RepairSelectedVersion(object sender, RoutedEventArgs e)
    {
        if (SelectedVersionId is not string versionId)
        {
            UpdateLaunchStatus("请先选择一个本地版本。", 0, "当前步骤：补全/校验");
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        var gameDirectory = GameDirectoryBox.Text.Trim();
        var versionJsonPath = Path.Combine(gameDirectory, "versions", versionId, $"{versionId}.json");
        if (!File.Exists(versionJsonPath))
        {
            UpdateLaunchStatus("版本 JSON 不存在，无法补全。", 0, "当前步骤：补全/校验");
            OutputBox.Text = $"版本 JSON 不存在：{versionJsonPath}";
            return;
        }

        UpdateLaunchStatus($"补全 {versionId} 已加入全局队列", 0, "当前步骤：等待补全/校验");
        DownloadTaskManager.Current.Enqueue("repair", $"补全游戏 {versionId}", (record, token) =>
            RepairSelectedVersionAsync(record, gameDirectory, versionId, versionJsonPath, token));
    }

    private async Task<string> RepairSelectedVersionAsync(
        DownloadTaskRecord record,
        string gameDirectory,
        string versionId,
        string versionJsonPath,
        CancellationToken cancellationToken)
    {
        try
        {
            UpdateLaunchStatus($"准备校验 {versionId}...", 0, "当前步骤：补全/校验");
            OutputBox.Text = $"准备校验 {versionId}...";
            var versionJson = JsonNode.Parse(await File.ReadAllTextAsync(versionJsonPath)) as JsonObject
                ?? throw new InvalidOperationException("版本 JSON 无法解析。");
            var progress = new Progress<DownloadProgress>(snapshot =>
            {
                var percent = snapshot.TotalJobs > 0 ? (int)Math.Clamp(snapshot.CompletedJobs * 100 / snapshot.TotalJobs, 0, 100) : 0;
                SetLaunchProgress(percent, $"当前步骤：{snapshot.Phase}");
                LaunchStatusText.Text = $"{snapshot.Phase}：{snapshot.CompletedJobs}/{snapshot.TotalJobs}";
                OutputBox.Text = $"[{snapshot.Phase}] {snapshot.CompletedJobs}/{snapshot.TotalJobs} reuse={snapshot.ReusedJobs} {Path.GetFileName(snapshot.CurrentFile)}";
                DownloadTaskManager.Current.Update(record, item =>
                {
                    item.Progress = percent;
                    item.CompletedFiles = snapshot.CompletedJobs;
                    item.TotalFiles = snapshot.TotalJobs;
                    item.ReusedFiles = snapshot.ReusedJobs;
                    item.DownloadedBytes = snapshot.DownloadedBytes;
                    item.Detail = $"{snapshot.Phase}：{snapshot.CompletedJobs}/{snapshot.TotalJobs}";
                });
            });
            var summary = await _downloader.RepairGameFilesAsync(versionJson, gameDirectory, versionId, "official", includeAssets: true, progress, cancellationToken);
            UpdateLaunchStatus($"补全完成：{versionId}", 100, "当前步骤：补全完成");
            OutputBox.Text = string.Join(Environment.NewLine, [
                $"补全完成：{versionId}",
                $"校验/补齐：{summary.CompletedJobs}/{summary.TotalJobs}",
                $"复用：{summary.ReusedJobs}",
                $"写入：{AppFormatter.FormatBytes(summary.DownloadedBytes)}",
                $"natives：解压 {summary.NativeFilesExtracted} 个文件到 {summary.NativesDirectory}",
            ]);
            return $"补全完成：{versionId}";
        }
        catch (OperationCanceledException)
        {
            UpdateLaunchStatus("补全已取消", 0, "当前步骤：已取消");
            OutputBox.Text = "游戏补全已取消。";
            throw;
        }
        catch (Exception exc)
        {
            UpdateLaunchStatus("补全失败：" + AppSettings.ConciseDownloadError(exc.Message), 0, "当前步骤：补全失败");
            OutputBox.Text = "补全失败：" + AppSettings.ConciseDownloadError(exc.Message);
            throw;
        }
    }

    private void OpenVersionFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedVersionId is not string versionId)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        OpenDirectory(Path.Combine(GameDirectoryBox.Text.Trim(), "versions", versionId), create: false);
    }

    private void OpenSavesFolder(object sender, RoutedEventArgs e) => OpenVersionRuntimeSubfolder("saves");
    private void OpenModsFolder(object sender, RoutedEventArgs e) => OpenDirectory(CurrentModsDirectory(), create: true);

    private void RefreshVersionMods()
    {
        _versionModsByLabel.Clear();
        if (SelectedVersionId is not string versionId)
        {
            VersionModsList.ItemsSource = null;
            SetVersionModControls(false, "请先选择一个本地版本。");
            return;
        }

        if (!VersionSupportsModManagement(versionId))
        {
            VersionModsList.ItemsSource = null;
            SetVersionModControls(false, "当前版本不支持 Mod 管理。请选择 Fabric、Forge、NeoForge 或 OptiFine 版本。");
            return;
        }

        var mods = ResourceMarketService.ListLocalResources("", "mod", CurrentModsDirectory());
        var labels = new List<string>();
        foreach (var mod in mods)
        {
            var label = $"{mod.Title}  [{mod.Status}]  {AppFormatter.FormatBytes(mod.SizeBytes)}";
            labels.Add(label);
            _versionModsByLabel[label] = mod;
        }

        VersionModsList.ItemsSource = labels;
        SetVersionModControls(true, mods.Count == 0 ? "mods 目录中没有 jar 文件。" : $"共 {mods.Count} 个 Mod。双击不会修改文件，请使用下方按钮操作。");
    }

    private void ToggleSelectedVersionMod(object sender, RoutedEventArgs e)
    {
        var mod = SelectedVersionMod();
        if (mod is null)
        {
            return;
        }

        try
        {
            var enabled = string.Equals(mod.Status, "禁用", StringComparison.Ordinal);
            var targetPath = ResourceMarketService.SetLocalResourceEnabled(mod.Path, enabled);
            OutputBox.Text = (enabled ? "已启用 Mod：" : "已禁用 Mod：") + targetPath;
            RefreshVersionMods();
        }
        catch (Exception exc)
        {
            VersionModsStatusText.Text = "更新 Mod 状态失败：" + exc.Message;
            OutputBox.Text = VersionModsStatusText.Text;
        }
    }

    private async void DeleteSelectedVersionMod(object sender, RoutedEventArgs e)
    {
        var mod = SelectedVersionMod();
        if (mod is null)
        {
            return;
        }

        var dialog = AppDialog.Create(this, "删除 Mod", $"确定要永久删除 {mod.Title} 吗？", "删除", "取消");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            ResourceMarketService.DeleteLocalResource(mod.Path);
            OutputBox.Text = "Mod 已删除：" + mod.Path;
            RefreshVersionMods();
        }
        catch (Exception exc)
        {
            VersionModsStatusText.Text = "删除 Mod 失败：" + exc.Message;
            OutputBox.Text = VersionModsStatusText.Text;
        }
    }

    private void RepairVersionModDependencies(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId || !VersionSupportsModManagement(versionId))
        {
            VersionModsStatusText.Text = "请先选择支持 Mod 的本地版本。";
            return;
        }

        var gameVersion = _versionSettingsService.ResolveBaseMinecraftVersion(GameDirectoryBox.Text.Trim(), versionId);
        var loader = ModrinthLoaderForVersion(versionId);
        var targetDirectory = CurrentModsDirectory();
        SetVersionModBusy(true, "已加入全局队列，等待补齐 Mod 前置...");
        DownloadTaskManager.Current.Enqueue("resource_dependencies", $"补齐 {versionId} 的 Mod 前置", (record, token) =>
            RepairVersionModDependenciesAsync(record, gameVersion, loader, targetDirectory, token));
    }

    private async Task<string> RepairVersionModDependenciesAsync(
        DownloadTaskRecord record,
        string gameVersion,
        string loader,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<ResourceDownloadProgress>(snapshot =>
            {
                VersionModsProgressBar.Value = Math.Clamp(snapshot.Progress * 100, 0, 100);
                VersionModsStatusText.Text = $"{snapshot.Phase} {snapshot.Progress:P0} {snapshot.CurrentFile}";
                DownloadTaskManager.Current.Update(record, item =>
                {
                    item.Progress = (int)Math.Round(snapshot.Progress * 100);
                    item.Detail = VersionModsStatusText.Text;
                });
            });
            var result = await _resourceMarket.InstallMissingModrinthDependenciesAsync(
                gameVersion,
                loader,
                targetDirectory,
                message =>
                {
                    VersionModsStatusText.Text = message;
                    DownloadTaskManager.Current.Update(record, item => item.Detail = message);
                },
                progress,
                cancellationToken);
            VersionModsStatusText.Text = result.DependenciesInstalled.Count == 0
                ? "没有发现缺失的 Modrinth 必需前置。"
                : $"已安装 {result.DependenciesInstalled.Count} 个缺失前置。";
            OutputBox.Text = result.DependenciesInstalled.Count == 0
                ? VersionModsStatusText.Text
                : VersionModsStatusText.Text + Environment.NewLine + string.Join(Environment.NewLine, result.DependenciesInstalled);
            RefreshVersionMods();
            return result.DependenciesInstalled.Count == 0
                ? "没有缺失的 Mod 前置"
                : $"已安装 {result.DependenciesInstalled.Count} 个 Mod 前置";
        }
        catch (OperationCanceledException)
        {
            VersionModsStatusText.Text = "补齐 Mod 前置已取消。";
            OutputBox.Text = VersionModsStatusText.Text;
            throw;
        }
        catch (Exception exc)
        {
            VersionModsStatusText.Text = "补齐 Mod 前置失败：" + exc.Message;
            OutputBox.Text = VersionModsStatusText.Text;
            throw;
        }
        finally
        {
            SetVersionModBusy(false);
        }
    }

    private LocalResourceInfo? SelectedVersionMod()
    {
        if (VersionModsList.SelectedItem is string label && _versionModsByLabel.TryGetValue(label, out var mod))
        {
            return mod;
        }

        VersionModsStatusText.Text = "请先选择一个 Mod。";
        return null;
    }

    private bool VersionSupportsModManagement(string versionId)
        => _versionSettingsService.DetectVersionType(GameDirectoryBox.Text.Trim(), versionId) is "Fabric" or "Forge" or "NeoForge" or "OptiFine";

    private string ModrinthLoaderForVersion(string versionId)
        => _versionSettingsService.DetectVersionType(GameDirectoryBox.Text.Trim(), versionId) switch
        {
            "Fabric" => "fabric",
            "Forge" => "forge",
            "NeoForge" => "neoforge",
            _ => "",
        };

    private void SetVersionModControls(bool enabled, string status)
    {
        ToggleVersionModButton.IsEnabled = enabled;
        DeleteVersionModButton.IsEnabled = enabled;
        RepairVersionModDependenciesButton.IsEnabled = enabled;
        VersionModsStatusText.Text = status;
    }

    private void SetVersionModBusy(bool busy, string status = "")
    {
        var enabled = !busy && SelectedVersionId is string versionId && VersionSupportsModManagement(versionId);
        ToggleVersionModButton.IsEnabled = enabled;
        DeleteVersionModButton.IsEnabled = enabled;
        RepairVersionModDependenciesButton.IsEnabled = enabled;
        VersionModsProgressBar.IsIndeterminate = busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            VersionModsStatusText.Text = status;
        }
    }

    private void ApplyAdvancedVisibility()
    {
        var visibility = _settings.Config.GetBool("UI", "advanced_mode", false) ? Visibility.Visible : Visibility.Collapsed;
        MinimumMemoryPanel.Visibility = visibility;
        GcStrategyPanel.Visibility = visibility;
        WindowWidthPanel.Visibility = visibility;
        WindowHeightPanel.Visibility = visibility;
        AdvancedLaunchSettingsCard.Visibility = visibility;
    }

    internal void RunSmokeChecks()
    {
        var pageWidth = ViewportWidth;
        ApplyResponsiveLayout(pageWidth);
        var wide = pageWidth >= 900;

        if (Grid.GetRow(VersionCenterPanel) != (wide ? 0 : 1) || Grid.GetColumn(VersionCenterPanel) != (wide ? 1 : 0))
        {
            throw new InvalidOperationException("启动页响应式布局状态不正确。");
        }

        if ((wide && LaunchVersionCenterColumn.Width.GridUnitType != GridUnitType.Star) ||
            (!wide && LaunchVersionCenterColumn.Width.Value != 0))
        {
            throw new InvalidOperationException("启动页响应式列宽状态不正确。");
        }

        if (VersionSectionBar.Items.Count != 2)
        {
            throw new InvalidOperationException("启动页版本中心缺少选择版本或版本设置分段。");
        }

        UpdateLaunchReadiness();
        if (AccountNameText is null || AvatarText is null || PrimaryLaunchSubText is null)
        {
            throw new InvalidOperationException("启动页缺少身份区控件。");
        }

        if (PrimaryLaunchText.Text.Length == 0 || PrimaryLaunchSubText.Text.Length == 0)
        {
            throw new InvalidOperationException("启动页主按钮没有说明当前状态。");
        }

        VersionSectionBar.SelectedItem = VersionSectionBar.Items[1];
        if (VersionSettingsPanel.Visibility != Visibility.Visible || VersionSelectorPanel.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("启动页版本设置分段状态不正确。");
        }

        VersionSectionBar.SelectedItem = VersionSectionBar.Items[0];
        if (VersionSelectorPanel.Visibility != Visibility.Visible || VersionSettingsPanel.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("启动页版本选择分段状态不正确。");
        }

        if (VersionModsList is null || ToggleVersionModButton is null || DeleteVersionModButton is null || RepairVersionModDependenciesButton is null)
        {
            throw new InvalidOperationException("启动页缺少 Python 版 Mod 管理控件。");
        }

        if (VersionsList.SelectedItem is null && (ToggleVersionModButton.IsEnabled || DeleteVersionModButton.IsEnabled))
        {
            throw new InvalidOperationException("未选择版本时不应允许修改 Mod。");
        }

        var advancedVisibility = _settings.Config.GetBool("UI", "advanced_mode", false) ? Visibility.Visible : Visibility.Collapsed;
        if (MinimumMemoryPanel.Visibility != advancedVisibility || AdvancedLaunchSettingsCard.Visibility != advancedVisibility)
        {
            throw new InvalidOperationException("版本高级设置可见性与高级模式不一致。");
        }

        LaunchPageHeader.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 0,
        });
    }

    internal void SelectVersionSettingsForSmoke(bool settings)
    {
        VersionSectionBar.SelectedItem = VersionSectionBar.Items[settings ? 1 : 0];
        if (settings)
        {
            VersionCenterPanel.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
        else
        {
            ResponsiveLayoutRoot.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private void OpenResourcepacksFolder(object sender, RoutedEventArgs e) => OpenVersionRuntimeSubfolder("resourcepacks");
    private void OpenShaderpacksFolder(object sender, RoutedEventArgs e) => OpenVersionRuntimeSubfolder("shaderpacks");
    private void OpenScreenshotsFolder(object sender, RoutedEventArgs e) => OpenVersionRuntimeSubfolder("screenshots");

    private void OpenModpackExport(object sender, RoutedEventArgs e) => OpenModpackTask("export");
    private void OpenCrashAnalysis(object sender, RoutedEventArgs e) => OpenModpackTask("crash");

    private void OpenModpackTask(string section)
    {
        if (SelectedVersionId is not string versionId)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        if (App.MainWindow is not MainWindow window)
        {
            return;
        }

        var runtimeDirectory = CurrentRuntimeDirectory();
        window.NavigateToTag("modpacks");
        if (window.CurrentPage is ModpacksPage modpacksPage)
        {
            modpacksPage.OpenTask(section, versionId, runtimeDirectory);
        }
    }

    private void OpenVersionRuntimeSubfolder(string name)
    {
        var runtime = CurrentRuntimeDirectory();
        if (runtime.Length == 0)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        OpenDirectory(Path.Combine(runtime, name), create: true);
    }

    private async void ExportLaunchScript(object sender, RoutedEventArgs e)
    {
        if (SelectedVersionId is not string versionId)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        try
        {
            var account = await RefreshSelectedAccountForLaunchAsync();
            var command = BuildSelectedLaunchCommand(account);
            var options = VersionSettingsService.LaunchOptionsForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            var scriptDir = Path.Combine(GameDirectoryBox.Text.Trim(), "mcgo_scripts");
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, $"launch-{LaunchScriptService.SafeScriptName(versionId)}.bat");
            var script = LaunchScriptService.BuildWindowsBatchScript(command, options.PreLaunchCommand);
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            UpdateLaunchStatus("启动脚本已导出。", 100, "当前步骤：导出脚本");
            OutputBox.Text = $"启动脚本已导出：{scriptPath}";
        }
        catch (Exception exc)
        {
            UpdateLaunchStatus("导出启动脚本失败", 0, "当前步骤：导出脚本");
            OutputBox.Text = "导出启动脚本失败：" + exc.Message;
        }
    }

    private async void DeleteSelectedVersion(object sender, RoutedEventArgs e)
    {
        if (SelectedVersionId is not string versionId)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        var dialog = AppDialog.Create(this, "删除版本", $"确定要删除版本 {VersionSettingsService.VersionDisplayName(_versionSettings, versionId)} 吗？", "删除", "取消");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            var result = VersionManagementService.DeleteVersion(GameDirectoryBox.Text.Trim(), versionId);
            _versionSettings.Remove(versionId);
            _versionSettingsService.SaveVersionSettings(_versionSettings);
            await RefreshVersionListAsync();
            UpdateLaunchStatus("版本已删除。", 0, "当前步骤：删除版本");
            OutputBox.Text = $"版本已删除：{result.DeletedPath}";
        }
        catch (Exception exc)
        {
            UpdateLaunchStatus("删除版本失败", 0, "当前步骤：删除版本");
            OutputBox.Text = "删除版本失败：" + exc.Message;
        }
    }

    private string CurrentRuntimeDirectory()
        => SelectedVersionId is string versionId
            ? VersionSettingsService.RuntimeDirectoryForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true))
            : "";

    private string CurrentModsDirectory()
        => SelectedVersionId is string versionId
            ? VersionSettingsService.ModsDirectoryForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true))
            : "";

    private void OpenDirectory(string path, bool create)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        if (create)
        {
            Directory.CreateDirectory(path);
        }

        if (!Directory.Exists(path))
        {
            OutputBox.Text = "目录不存在：" + path;
            return;
        }

        Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
        OutputBox.Text = "已打开目录：" + path;
    }

    private List<string> BuildSelectedLaunchCommand(JsonObject? selectedAccount = null)
    {
        if (SelectedVersionId is not string versionId)
        {
            throw new InvalidOperationException("请先选择一个版本。");
        }

        selectedAccount ??= SelectedAccount();
        var options = VersionSettingsService.LaunchOptionsForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
        var maxMemory = ManualMemoryCheckBox.IsChecked == true
            ? NumberValue(MemorySlider, 1024, 65536, AppSettings.RecommendedMemoryMb())
            : options.MaxMemoryMb;
        var extraJvmArgs = new List<string>();
        if (selectedAccount is not null)
        {
            extraJvmArgs.AddRange(ExternalAuthService.AuthlibInjectorArgs(selectedAccount));
        }

        extraJvmArgs.AddRange(options.ExtraJvmArgs);
        return _launcher.BuildLaunchCommand(new LaunchOptions(
            JavaPathBox.Text.Trim().Length == 0 ? "java" : JavaPathBox.Text.Trim(),
            versionId,
            GameDirectoryBox.Text.Trim(),
            MinecraftAccessToken: selectedAccount?["access_token"]?.GetValue<string>(),
            Username: selectedAccount?["display_name"]?.GetValue<string>() ?? selectedAccount?["username"]?.GetValue<string>() ?? "Player",
            Uuid: selectedAccount?["uuid"]?.GetValue<string>(),
            RuntimeDirectory: options.RuntimeDirectory,
            ExtraJvmArgs: extraJvmArgs,
            ExtraGameArgs: options.ExtraGameArgs,
            MinMemoryMb: options.MinMemoryMb,
            MaxMemoryMb: maxMemory,
            WindowWidth: options.WindowWidth,
            WindowHeight: options.WindowHeight,
            GcStrategy: ComboText(GcStrategyBox, options.GcStrategy)));
    }

    private void RefreshQuickPlayWorlds(object sender, RoutedEventArgs e)
    {
        RefreshQuickPlayWorlds(CurrentQuickPlayWorld());
        OutputBox.Text = "Quick Play 存档列表已刷新。";
    }

    private void RefreshQuickPlayWorlds(string selectedWorld)
    {
        _quickPlayWorldIds.Clear();
        _quickPlayWorldIds.Add("");
        var labels = new List<string> { "不快速进入存档" };
        var savesDir = Path.Combine(CurrentRuntimeDirectory(), "saves");
        if (Directory.Exists(savesDir))
        {
            foreach (var dir in Directory.GetDirectories(savesDir)
                         .OrderByDescending(Directory.GetLastWriteTime)
                         .Take(100))
            {
                var id = Path.GetFileName(dir);
                _quickPlayWorldIds.Add(id);
                labels.Add(id);
            }
        }

        QuickPlayBox.ItemsSource = labels;
        var index = string.IsNullOrWhiteSpace(selectedWorld) ? 0 : _quickPlayWorldIds.FindIndex(item => string.Equals(item, selectedWorld, StringComparison.OrdinalIgnoreCase));
        QuickPlayBox.SelectedIndex = index >= 0 ? index : 0;
        UpdateVersionSummary(SelectedVersionId);
    }

    private string CurrentQuickPlayWorld()
    {
        var index = QuickPlayBox.SelectedIndex;
        return index >= 0 && index < _quickPlayWorldIds.Count ? _quickPlayWorldIds[index] : "";
    }

    private void QuickPlaySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingVersion)
        {
            UpdateVersionSummary(SelectedVersionId);
        }
    }

    private void AccountSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedAccount() is { } account)
        {
            _settings.Config.Set("ACCOUNTS", "selected_account_id", account["id"]?.GetValue<string>() ?? "");
            _settings.SaveConfig();
        }

        UpdateLaunchMethod();
    }

    private void ManualMemoryChanged(object sender, RoutedEventArgs e)
    {
        UpdateMemoryControls();
        if (!_loadingVersion && ManualMemoryCheckBox.IsChecked == true && MemorySlider.Value < 1024)
        {
            MemorySlider.Value = ClampDouble(AppSettings.RecommendedMemoryMb(), 1024, 65536);
        }
    }

    private void MemorySliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateMemoryControls();
    }

    private void UpdateMemoryControls()
    {
        if (MemorySlider is null || MemoryLabel is null || ManualMemoryCheckBox is null)
        {
            return;
        }

        var manual = ManualMemoryCheckBox.IsChecked == true;
        MemorySlider.IsEnabled = manual;
        MemoryLabel.Text = manual
            ? $"最大内存：{NumberValue(MemorySlider, 1024, 65536, AppSettings.RecommendedMemoryMb())} MB"
            : "最大内存：自动";
    }

    private void UpdateJavaVersionStatus(string? path, int knownMajor)
    {
        if (JavaVersionText is null)
        {
            return;
        }

        var value = (path ?? "").Trim();
        if (value.Length == 0)
        {
            JavaVersionText.Text = "未选择 Java";
            return;
        }

        JavaVersionText.Text = knownMajor > 0 ? $"Java {knownMajor}" : "无法获取 Java 版本";
    }

    private void SetJavaDownloadStatus(string message, int progress)
    {
        if (JavaDownloadStatusText is not null)
        {
            JavaDownloadStatusText.Text = message;
        }

        if (JavaDownloadProgressBar is not null)
        {
            JavaDownloadProgressBar.Value = Math.Clamp(progress, 0, 100);
        }
    }

    private void UpdateVersionSummary(string? versionId)
    {
        if (VersionSummaryText is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(versionId))
        {
            SetVersionSummaryText("未选择版本");
            return;
        }

        var gameDir = GameDirectoryBox.Text.Trim();
        var displayName = VersionSettingsService.VersionDisplayName(_versionSettings, versionId);
        var type = _versionSettingsService.DetectVersionType(gameDir, versionId);
        var requiredJava = _launcher.InferRequiredJavaVersion(gameDir, versionId);
        var quickPlay = CurrentQuickPlayWorld();
        var runtime = VersionSettingsService.RuntimeDirectoryForVersion(
            gameDir,
            _versionSettings,
            versionId,
            _settings.Config.GetBool("GAME", "enable_resource_isolation", true));

        // 列表里已经显示版本名与类型，这里补上列表看不到、但决定能不能启动的信息
        var parts = new List<string>();
        var actualJava = JavaRuntimeService.GetJavaMajorVersion(JavaPathBox.Text.Trim());
        if (requiredJava > 0)
        {
            // Java 需求是关键信息，且版本不匹配时必须当场说清楚，不能等点了启动才报错
            if (actualJava > 0 && actualJava != requiredJava)
            {
                var direction = actualJava < requiredJava ? "过低" : "过高";
                parts.Add($"⚠ 需要 Java {requiredJava}，当前 Java {actualJava} {direction}");
            }
            else
            {
                parts.Add($"需要 Java {requiredJava}");
            }
        }

        parts.Add("运行目录：" + runtime);
        SetVersionSummaryText(string.Join(" | ", parts));
    }

    private void SetVersionSummaryText(string text)
    {
        VersionSummaryText.Text = text;
    }

    private void UpdateLaunchStatus(string message, int progress, string stage)
    {
        if (LaunchStatusText is not null)
        {
            LaunchStatusText.Text = message;
        }

        SetLaunchProgress(progress, stage);
        UpdateLaunchMethod();
    }

    private void SetLaunchProgress(int progress, string stage)
    {
        var clamped = Math.Clamp(progress, 0, 100);
        if (LaunchProgressBar is not null)
        {
            LaunchProgressBar.Value = clamped;
        }

        if (LaunchStageText is not null)
        {
            LaunchStageText.Text = stage;
        }

        if (LaunchProgressText is not null)
        {
            LaunchProgressText.Text = $"启动进度：{clamped}%";
        }
    }

    private void UpdateLaunchMethod(JsonObject? account = null)
    {
        if (LaunchMethodText is null)
        {
            return;
        }

        account ??= SelectedAccount();
        LaunchMethodText.Text = account is null
            ? "登录方式：未选择"
            : $"登录方式：{AccountTypeLabel(account)}";
        UpdateLaunchReadiness();
    }

    private static string AccountTypeLabel(JsonObject account)
        => (account["type"]?.GetValue<string>() ?? "offline") switch
        {
            "microsoft" => "Microsoft",
            "external" => "外置登录",
            _ => "离线",
        };

    private async Task<JsonObject?> RefreshSelectedAccountForLaunchAsync()
    {
        var account = SelectedAccount();
        if (account is null)
        {
            return null;
        }

        var type = account["type"]?.GetValue<string>() ?? "offline";
        if (type == "offline")
        {
            return account;
        }

        JsonObject refreshed;
        if (type == "microsoft")
        {
            var refreshToken = account["refresh_token"]?.GetValue<string>() ?? "";
            if (refreshToken.Length == 0)
            {
                return account;
            }

            await _microsoftAuthenticator.RefreshAccessTokenAsync(refreshToken);
            refreshed = JsonNode.Parse(account.ToJsonString())!.AsObject();
            refreshed["access_token"] = _microsoftAuthenticator.MinecraftAccessToken ?? account["access_token"]?.GetValue<string>() ?? "";
            refreshed["refresh_token"] = _microsoftAuthenticator.RefreshToken ?? refreshToken;
        }
        else if (type == "external")
        {
            var service = new ExternalAuthService();
            refreshed = await service.RefreshExternalAccountAsync(account);
            refreshed["type"] = "external";
            refreshed["auth_server"] ??= account["auth_server"]?.GetValue<string>() ?? account["server"]?.GetValue<string>() ?? "";
            refreshed["authlib_injector_path"] ??= account["authlib_injector_path"]?.GetValue<string>() ?? "";
        }
        else
        {
            return account;
        }

        refreshed["id"] ??= account["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
        ReplaceAccount(account, refreshed);
        _settings.SaveAccounts(_accounts);
        RefreshAccountsList(refreshed["id"]?.GetValue<string>());
        return refreshed;
    }

    private void RefreshAccountsList(string? selectedAccountId = null)
    {
        selectedAccountId ??= SelectedAccount()?["id"]?.GetValue<string>()
                              ?? _settings.Config.Get("ACCOUNTS", "selected_account_id", "");
        _accountsByLabel.Clear();
        var labels = new List<string>();
        foreach (var account in _accounts)
        {
            var label = AccountLabel(account);
            labels.Add(label);
            _accountsByLabel[label] = account;
        }

        AccountComboBox.ItemsSource = labels;
        if (!string.IsNullOrWhiteSpace(selectedAccountId))
        {
            AccountComboBox.SelectedItem = labels.FirstOrDefault(label => _accountsByLabel[label]["id"]?.GetValue<string>() == selectedAccountId);
        }

        if (AccountComboBox.SelectedItem is null && labels.Count > 0)
        {
            AccountComboBox.SelectedIndex = 0;
        }
    }

    private void ReplaceAccount(JsonObject oldAccount, JsonObject newAccount)
    {
        var oldId = oldAccount["id"]?.GetValue<string>() ?? "";
        var index = _accounts.FindIndex(account => oldId.Length > 0 && account["id"]?.GetValue<string>() == oldId);
        if (index < 0)
        {
            index = _accounts.IndexOf(oldAccount);
        }

        if (index >= 0)
        {
            _accounts[index] = newAccount;
        }
    }

    private JsonObject? SelectedAccount()
        => AccountComboBox.SelectedItem is string label && _accountsByLabel.TryGetValue(label, out var account) ? account : null;

    private string? SelectedAccountName()
        => SelectedAccount()?["display_name"]?.GetValue<string>() ?? SelectedAccount()?["username"]?.GetValue<string>();

    private static string AccountLabel(JsonObject account)
    {
        var type = account["type"]?.GetValue<string>() ?? "offline";
        var name = account["display_name"]?.GetValue<string>() ?? account["username"]?.GetValue<string>() ?? "(未命名)";
        return $"{name} ({type})";
    }

    private static string ComboText(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? fallback;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }

    private static void SetComboText(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static IReadOnlyList<string> FindJavaCandidates(string gameDirectory, string configuredPath)
    {
        var candidates = new List<string>();
        AddJavaCandidate(candidates, configuredPath);
        AddJavaCandidate(candidates, "java");
        AddJavaCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(Path.Combine(gameDirectory, "java_runtimes")));
        // 官方启动器自带的运行时（.minecraft\runtime\...），用户装了正版一般都有
        foreach (var bundled in JavaRuntimeService.FindBundledRuntimeCandidates())
        {
            AddJavaCandidate(candidates, bundled);
        }

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

    private static int PositiveInt(JsonNode? value, int fallback)
        => int.TryParse(value?.ToString(), out var parsed) && parsed >= 0 ? parsed : fallback;

    private static int NumberValue(NumberBox box, int min, int max, int fallback)
    {
        if (double.IsNaN(box.Value))
        {
            return fallback;
        }

        return Math.Clamp((int)Math.Round(box.Value), min, max);
    }

    private static int NumberValue(Slider slider, int min, int max, int fallback)
    {
        if (double.IsNaN(slider.Value))
        {
            return fallback;
        }

        return Math.Clamp((int)Math.Round(slider.Value / 256) * 256, min, max);
    }

    private static double ClampDouble(int value, int min, int max) => Math.Clamp(value, min, max);

    private sealed record LocalVersionCandidate(string VersionId, string DetectedType);

    // 断点必须按页面实际宽度计算：StackPanel 会按内容收缩，
    // 用它自己的 ActualWidth 会在宽屏上误判成窄屏。
    private void ResponsiveLayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(ViewportWidth);
    }

    private void ResponsiveLayoutRoot_LayoutUpdated(object? sender, object e)
    {
        // XamlRoot.Size 在首次 Loaded 之前可能为 0，这里持续校正到真实视口宽度。
        ApplyResponsiveLayout(ViewportWidth);
    }

    /// <summary>视口宽度：Page/容器可能按内容收缩，XamlRoot.Size 才是真实窗口客户区。</summary>
    private double ViewportWidth
    {
        get
        {
            var root = XamlRoot?.Size.Width ?? 0;
            if (root > 0)
            {
                return root;
            }

            return XamlRoot?.Size.Width ?? ResponsiveLayoutRoot.ActualWidth;
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var wide = width >= 900;
        if (_responsiveLayoutInitialized && _wideLayout == wide)
        {
            return;
        }

        _responsiveLayoutInitialized = true;
        _wideLayout = wide;
        LaunchActionColumn.Width = wide ? new GridLength(5, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        LaunchVersionCenterColumn.Width = wide ? new GridLength(6, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(VersionCenterPanel, wide ? 0 : 1);
        Grid.SetColumn(VersionCenterPanel, wide ? 1 : 0);
    }
}
