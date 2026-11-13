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
    private LaunchPreflightService _launchPreflight = new();
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
    private bool _unloaded;
    private CancellationTokenSource? _launchCancellation;
    internal bool IsLaunching => _launchCancellation is not null;
    private int _javaEnvironmentRequest;
    private int _javaSelectionRequest;
    private Task<IReadOnlyList<string>>? _javaCandidatesTask;
    private string _javaCandidatesDirectory = "";
    private Task<bool> _javaSelectionTask = Task.FromResult(false);
    private int _versionRefreshRequest;
    private int _modsRefreshRequest;
    private int _worldsRefreshRequest;
    private readonly Dictionary<string, Task<int>> _javaProbes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LocalVersionCandidate> _localVersions = new(StringComparer.OrdinalIgnoreCase);
    private string _versionsDirectory = "";
    private readonly TaskCompletionSource _initializationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LaunchPage()
    {
        InitializeComponent();
        _versionSettingsService = new VersionSettingsService(_launcher);
        VersionSectionBar.SelectedItem = VersionSectionBar.Items[0];
        IsEnabled = false;
        UpdateLaunchReadiness();
        RefreshVersionMods();
        Loaded += LaunchPage_Loaded;
        Unloaded += (_, _) =>
        {
            _launchCancellation?.Cancel();
            _unloaded = true;
            ++_javaEnvironmentRequest;
            ++_javaSelectionRequest;
            ++_versionRefreshRequest;
            ++_modsRefreshRequest;
            ++_worldsRefreshRequest;
        };
    }

    private async void LaunchPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= LaunchPage_Loaded;
        try
        {
            var state = await Task.Run(() =>
            {
                _settings.LoadConfig(persistChanges: false);
                return (Versions: _versionSettingsService.LoadForGameDirectory(_settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory)), Accounts: _settings.LoadAccounts(persistChanges: false));
            });
            if (_unloaded)
            {
                return;
            }

            _versionSettings = state.Versions;
            _accounts = state.Accounts;
            LoadState();
            _initialized = true;
            IsEnabled = true;
            await Task.WhenAll(
                RefreshJavaEnvironmentAsync(JavaPathBox.Text, showFeedback: false),
                RefreshVersionListAsync());
        }
        catch (Exception exc)
        {
            if (!_unloaded)
            {
                OutputBox.Text = "加载启动设置失败：" + exc.Message;
            }
        }
        finally
        {
            _initializationCompleted.TrySetResult();
        }
    }

    private void VersionSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        var section = (sender.SelectedItem as SelectorBarItem)?.Tag?.ToString() ?? "selector";
        VersionSelectorPanel.Visibility = section == "selector" ? Visibility.Visible : Visibility.Collapsed;
        VersionSettingsPanel.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadState()
    {
        ApplyAdvancedVisibility();
        _resourceMarket.ModrinthApiBase = _settings.Config.Get("DOWNLOAD", "modrinth_api_base", "");
        if (string.IsNullOrWhiteSpace(_resourceMarket.ModrinthApiBase))
        {
            _resourceMarket.ModrinthApiBase = "https://api.modrinth.com/v2";
        }

        _resourceMarket.ModrinthCdnBase = _settings.Config.Get("DOWNLOAD", "modrinth_cdn_base", "");
        GameDirectoryBox.Text = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        JavaPathBox.Text = _settings.Config.Get("JAVA", "path", "java");
        AutoJavaCheckBox.IsOn = _settings.Config.GetBool("JAVA", "auto_select", true);
        UpdateJavaModeControls();
        MemorySlider.Value = UiHelpers.ClampDouble(AppSettings.RecommendedMemoryMb(), 1024, 65536);
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
        var category = UiHelpers.ComboText(VersionCategoryBox, "全部版本");
        var request = ++_versionRefreshRequest;
        IReadOnlyList<LocalVersionCandidate> candidates;
        try
        {
            candidates = await Task.Run(() => _launcher.GetLocalVersions(gameDir)
                .Select(versionId => ReadLocalVersion(gameDir, versionId))
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

        _versionsDirectory = gameDir;
        _localVersions = candidates.ToDictionary(candidate => candidate.VersionId, StringComparer.OrdinalIgnoreCase);
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

        if (SelectedVersionId is null)
        {
            RefreshVersionMods();
        }
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

    private LocalVersionCandidate ReadLocalVersion(string gameDir, string versionId)
    {
        var type = "未知类型";
        try
        {
            type = _versionSettingsService.DetectVersionType(gameDir, versionId);
            return new LocalVersionCandidate(versionId, type, _launcher.InferRequiredJavaVersion(gameDir, versionId));
        }
        catch (Exception exc) when (exc is System.Text.Json.JsonException or InvalidOperationException
            or FormatException or IOException or UnauthorizedAccessException)
        {
            // A broken manifest must not hide other installed versions; launch preflight still validates it.
            return new LocalVersionCandidate(versionId, type, 0);
        }
    }

    private void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedVersionId is not string versionId)
        {
            ++_javaSelectionRequest;
            ++_worldsRefreshRequest;
            _quickPlayWorldIds.Clear();
            QuickPlayBox.ItemsSource = null;
            RefreshVersionMods();
            UpdateVersionSummary(null);
            UpdateLaunchReadiness();
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
        MemorySlider.Value = UiHelpers.ClampDouble(maxMemory > 0 ? maxMemory : AppSettings.RecommendedMemoryMb(), 1024, 65536);
        MinMemoryBox.Value = UiHelpers.ClampDouble(PositiveInt(entry["min_memory_mb"], 0), 0, 65536);
        WindowWidthBox.Value = UiHelpers.ClampDouble(PositiveInt(entry["window_width"], 0), 0, 16384);
        WindowHeightBox.Value = UiHelpers.ClampDouble(PositiveInt(entry["window_height"], 0), 0, 16384);
        RuntimeDirectoryBox.Text = entry["runtime_directory"]?.GetValue<string>() ?? "";
        CustomModsDirectoryBox.Text = entry["mods_directory"]?.GetValue<string>() ?? "";
        UiHelpers.SetComboText(GcStrategyBox, entry["gc_strategy"]?.GetValue<string>() ?? "G1GC");
        UiHelpers.SetComboText(VersionIconBox, entry["icon"]?.GetValue<string>() ?? "自动");
        UseIsolationCheckBox.IsChecked = entry["use_isolated_directory"]?.GetValue<bool>() ?? false;
        VersionFavoriteCheckBox.IsChecked = entry["favorite"]?.GetValue<bool>() ?? false;
        VersionHiddenCheckBox.IsChecked = entry["hidden"]?.GetValue<bool>() ?? false;
        RefreshQuickPlayWorlds(entry["quick_play_singleplayer"]?.GetValue<string>() ?? "");
        UpdateMemoryControls();
        UpdateVersionSummary(versionId);
        RefreshVersionMods();
        UpdateLaunchStatus("已选择版本，等待启动", 0, "当前步骤：等待启动");
        _loadingVersion = false;
        if (AutoJavaCheckBox.IsOn)
        {
            _javaSelectionTask = SelectJavaForVersionAsync();
        }
    }


    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        _settings.Config.Set("USER", "username", SelectedAccountName() ?? "");
        _settings.Config.Set("JAVA", "path", JavaPathBox.Text.Trim());
        _settings.Config.Set("JAVA", "auto_select", AutoJavaCheckBox.IsOn ? "True" : "False");
        _settings.SaveConfig();
        await RefreshVersionListAsync();
        await RefreshJavaEnvironmentAsync(JavaPathBox.Text, showFeedback: false);
        UpdateLaunchMethod();
        OutputBox.Text = "设置已保存。";
    }

    private async void RefreshJavaCandidates(object sender, RoutedEventArgs e)
    {
        _javaCandidatesTask = null;
        foreach (var path in _javaProbes.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToList())
        {
            _javaProbes.Remove(path);
        }
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
        var javaPath = selectedPath.Trim().Length == 0 ? "java" : selectedPath.Trim();
        JavaVersionText.Text = "正在检测 Java...";
        if (showFeedback)
        {
            UpdateLaunchStatus("正在扫描 Java 运行时...", 0, "当前步骤：检测 Java");
        }

        try
        {
            var candidates = await GetJavaCandidatesAsync();
            if (request != _javaEnvironmentRequest)
            {
                return;
            }

            SetJavaCandidates(candidates, javaPath);
            if (AutoJavaCheckBox.IsOn && SelectedVersionId is not null)
            {
                _javaSelectionTask = SelectJavaForVersionAsync();
                await _javaSelectionTask;
            }
            else
            {
                var major = await ProbeJavaAsync(javaPath);
                if (request == _javaEnvironmentRequest && SamePathOrText(javaPath, JavaPathBox.Text.Trim())
                    && (!AutoJavaCheckBox.IsOn || SelectedVersionId is null))
                {
                    UpdateJavaVersionStatus(javaPath, major);
                }
            }
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

    }

    private Task<IReadOnlyList<string>> GetJavaCandidatesAsync()
    {
        var directory = GameDirectoryBox.Text.Trim();
        var path = JavaPathBox.Text.Trim();
        if (_javaCandidatesTask is null || !SamePathOrText(directory, _javaCandidatesDirectory))
        {
            _javaCandidatesDirectory = directory;
            _javaCandidatesTask = Task.Run(() => JavaDiscovery.FindCandidates(directory, path));
        }
        return _javaCandidatesTask;
    }

    private void SetJavaCandidates(IReadOnlyList<string> candidates, string path)
    {
        _loadingJavaCandidates = true;
        try
        {
            var displayed = candidates.Contains(path, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(path)
                ? candidates : new[] { path }.Concat(candidates).ToList();
            JavaCandidatesBox.ItemsSource = displayed;
            JavaCandidatesBox.SelectedItem = displayed.FirstOrDefault(candidate => SamePathOrText(candidate, path));
        }
        finally
        {
            _loadingJavaCandidates = false;
        }

    }

    private void UpdateJavaModeControls()
    {
        var automatic = AutoJavaCheckBox.IsOn;
        JavaPathBox.IsEnabled = !automatic;
        JavaCandidatesBox.IsEnabled = !automatic;
    }

    private async void AutoJavaChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        ++_javaSelectionRequest;
        ++_javaEnvironmentRequest;
        UpdateJavaModeControls();
        if (AutoJavaCheckBox.IsOn)
        {
            _javaCandidatesTask = null;
            _javaSelectionTask = SelectJavaForVersionAsync();
            await _javaSelectionTask;
        }
        else
        {
            await DetectJavaVersionAsync(JavaPathBox.Text);
        }
    }

    private async Task<bool> SelectJavaForVersionAsync()
    {
        var request = ++_javaSelectionRequest;
        var versionId = SelectedVersionId;
        var directory = GameDirectoryBox.Text.Trim();
        var required = versionId is null ? 0 : KnownVersion(versionId)?.RequiredJava ?? 0;
        bool IsCurrent() => !_unloaded && request == _javaSelectionRequest
            && AutoJavaCheckBox.IsOn && SelectedVersionId == versionId
            && SamePathOrText(directory, GameDirectoryBox.Text.Trim());
        if (required <= 0 || !IsCurrent())
        {
            return false;
        }

        JavaVersionText.Text = $"正在匹配 Java {required}...";
        try
        {
            var candidates = await GetJavaCandidatesAsync();
            if (!IsCurrent()) return false;
            var current = JavaPathBox.Text.Trim();
            foreach (var candidate in new[] { current }.Concat(candidates)
                         .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var major = await ProbeJavaAsync(candidate);
                if (!IsCurrent()) return false;
                if (major != required) continue;

                JavaPathBox.Text = candidate;
                SetJavaCandidates(candidates, candidate);
                UpdateJavaVersionStatus(candidate, major);
                SetJavaDownloadStatus($"已自动选择 Java {major}", 0);
                return true;
            }

            if (IsCurrent())
            {
                JavaVersionText.Text = $"未找到 Java {required}";
                SetJavaDownloadStatus($"当前版本需要 Java {required}，请下载匹配的 Java 运行时。", 0);
                UpdateVersionSummary(versionId);
            }
        }
        catch (Exception exc)
        {
            if (IsCurrent()) SetJavaDownloadStatus("自动选择 Java 失败：" + exc.Message, 0);
        }
        return false;
    }

    private async Task<int> DetectJavaVersionAsync(string path)
    {
        var request = ++_javaEnvironmentRequest;
        var javaPath = path.Trim().Length == 0 ? "java" : path.Trim();
        JavaVersionText.Text = "正在检测 Java...";
        var major = await ProbeJavaAsync(javaPath, refresh: true);
        if (request == _javaEnvironmentRequest)
        {
            UpdateJavaVersionStatus(javaPath, major);
        }

        return major;
    }

    private Task<int> ProbeJavaAsync(string path, bool refresh = false)
    {
        var key = string.IsNullOrWhiteSpace(path) ? "java" : path.Trim();
        if (!_javaProbes.TryGetValue(key, out var probe) || (refresh && probe.IsCompleted))
        {
            probe = Task.Run(() => JavaRuntimeService.GetJavaMajorVersion(key));
            _javaProbes[key] = probe;
        }

        return probe;
    }

    // UI updates only consume completed probes; they must never start a process or wait for one.
    private int KnownJavaMajor(string path)
    {
        var key = string.IsNullOrWhiteSpace(path) ? "java" : path.Trim();
        return _javaProbes.TryGetValue(key, out var probe) && probe.IsCompletedSuccessfully ? probe.Result : 0;
    }

    private LocalVersionCandidate? KnownVersion(string versionId)
        => SamePathOrText(_versionsDirectory, GameDirectoryBox.Text.Trim())
            && _localVersions.TryGetValue(versionId, out var version) ? version : null;

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
        entry["max_memory_mb"] = ManualMemoryCheckBox.IsChecked == true ? UiHelpers.NumberValue(MemorySlider, 1024, 65536, AppSettings.RecommendedMemoryMb()) : 0;
        entry["min_memory_mb"] = UiHelpers.NumberValue(MinMemoryBox, 0, 65536, 0);
        entry["window_width"] = UiHelpers.NumberValue(WindowWidthBox, 0, 16384, 0);
        entry["window_height"] = UiHelpers.NumberValue(WindowHeightBox, 0, 16384, 0);
        entry["gc_strategy"] = UiHelpers.ComboText(GcStrategyBox, "G1GC");
        entry["icon"] = UiHelpers.ComboText(VersionIconBox, "自动");
        entry["runtime_directory"] = RuntimeDirectoryBox.Text.Trim();
        entry["mods_directory"] = CustomModsDirectoryBox.Text.Trim();
        _versionSettingsService.SaveForGameDirectory(_versionSettings, GameDirectoryBox.Text.Trim());
        await RefreshVersionListAsync();
        UpdateVersionSummary(versionId);
        UpdateLaunchStatus("版本设置已保存。", 0, "当前步骤：保存设置");
        OutputBox.Text = "版本设置已保存。";
    }

    private async void CheckJavaRuntime(object sender, RoutedEventArgs e)
    {
        if (AutoJavaCheckBox.IsOn && SelectedVersionId is not null)
        {
            _javaSelectionTask = SelectJavaForVersionAsync();
            await _javaSelectionTask;
            return;
        }
        var javaPath = JavaPathBox.Text.Trim().Length == 0 ? "java" : JavaPathBox.Text.Trim();
        var versionId = SelectedVersionId;
        var major = await DetectJavaVersionAsync(javaPath);
        if (_unloaded || JavaPathBox.Text.Trim() != javaPath || SelectedVersionId != versionId) return;
        var required = versionId is null ? 0 : KnownVersion(versionId)?.RequiredJava ?? 0;
        UpdateJavaVersionStatus(javaPath, major);
        SetJavaDownloadStatus(major == 0
            ? $"无法识别 Java：{javaPath}"
            : required > 0 && major != required
                ? $"当前 Java {major} 与所需 Java {required} 不匹配，可下载推荐运行时"
                : $"当前 Java {major} 可用于启动", 0);
        UpdateLaunchStatus(major == 0
            ? "无法识别当前 Java"
            : required > 0 && major != required
                ? $"当前 Java {major} 与所需 Java {required} 不匹配"
                : "Java 版本满足要求", 0, "当前步骤：检测 Java");
        OutputBox.Text = major == 0
            ? $"无法识别 Java 版本：{javaPath}"
            : required > 0
                ? $"当前 Java：{major}{Environment.NewLine}所选版本需要：Java {required}{Environment.NewLine}" +
                    (major == required ? "Java 版本满足要求。" : "Java 版本不匹配，请下载推荐 Java 或更换路径。")
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
            DownloadRecommendedJavaAsync(record, required, installRoot, token), outputDirectory: installRoot);
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
            ++_javaSelectionRequest;
            ++_javaEnvironmentRequest;
            _javaCandidatesTask = null;
            JavaPathBox.Text = result.JavaPath;
            _settings.Config.Set("JAVA", "path", result.JavaPath);
            _settings.SaveConfig();
            UpdateJavaVersionStatus(result.JavaPath, required);
            await RefreshJavaEnvironmentAsync(result.JavaPath, showFeedback: false);
            SetJavaDownloadStatus(result.Reused ? $"Java {required} 已安装：{result.JavaPath}" : $"Java {required} 下载完成：{result.JavaPath}", 100);
            UpdateLaunchStatus(result.Reused ? $"已复用 Java {required}" : $"Java {required} 下载完成", 100, "当前步骤：Java 就绪");
            OutputBox.Text = string.Join(Environment.NewLine, [
                result.Reused ? $"已复用 Java {required}" : $"Java {required} 下载完成",
                $"路径：{result.JavaPath}",
                $"安装目录：{result.InstallDirectory}",
            ]);
            return result.Reused ? $"已复用 Java {required}" : $"Java {required} 下载完成";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            if (!await PrepareJavaForLaunchAsync()) return;
            var selection = CaptureLaunchSelection();
            UpdateLaunchStatus("正在生成启动命令...", 10, "当前步骤：生成命令");
            var account = await RefreshSelectedAccountForLaunchAsync();
            EnsureLaunchSelection(selection);
            UpdateLaunchMethod(account);
            var command = await BuildSelectedLaunchCommandAsync(account);
            EnsureLaunchSelection(selection);
            OutputBox.Text = string.Join(Environment.NewLine, command);
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
        if (_launchCancellation is not null)
        {
            _launchCancellation.Cancel();
            PrimaryLaunchText.Text = "正在取消";
            return;
        }
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

        if (_launchCancellation is not null)
        {
            PrimaryLaunchText.Text = _launchCancellation.IsCancellationRequested ? "正在取消" : "取消启动";
            PrimaryLaunchIcon.Symbol = Symbol.Cancel;
            PrimaryLaunchSubText.Text = SelectedVersionId ?? "";
            return;
        }

        var account = SelectedAccount();
        var hasAccount = account is not null;
        var versionId = SelectedVersionId;
        var hasVersion = !string.IsNullOrWhiteSpace(versionId);
        var javaPath = JavaPathBox.Text.Trim();
        var javaMajor = KnownJavaMajor(javaPath);

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

    private void GoAccounts(object sender, RoutedEventArgs e) => NavigateToTag("accounts");

    private async void LaunchGame(object sender, RoutedEventArgs e) => await LaunchGameAsync();

    private async Task LaunchGameAsync()
    {
        if (_launchCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _launchCancellation = cancellation;
        var token = cancellation.Token;
        VersionCenterPanel.IsEnabled = false;
        LaunchEnvironmentExpander.IsEnabled = false;
        UpdateLaunchStatus("正在准备启动...", 0, "当前步骤：匹配 Java");
        try
        {
            if (!await PrepareJavaForLaunchAsync(token)) return;
            var selection = CaptureLaunchSelection();
            if (selection.Version is null) return;
            UpdateLaunchStatus("正在校验游戏文件...", 5, "当前步骤：启动前检查");
            var preflight = await _launchPreflight.ValidateAsync(selection.Directory, selection.Version,
                string.IsNullOrWhiteSpace(selection.Java) ? "java" : selection.Java, cancellationToken: token);
            token.ThrowIfCancellationRequested();
            EnsureLaunchSelection(selection);
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
            var account = await RefreshSelectedAccountForLaunchAsync(token);
            token.ThrowIfCancellationRequested();
            EnsureLaunchSelection(selection);
            UpdateLaunchMethod(account);
            SetLaunchProgress(35, "当前步骤：启动前命令");
            var options = VersionSettingsService.LaunchOptionsForVersion(selection.Directory, _versionSettings,
                selection.Version, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            await LaunchProcessService.RunPreLaunchCommandAsync(options.PreLaunchCommand, options.RuntimeDirectory, token);
            token.ThrowIfCancellationRequested();
            EnsureLaunchSelection(selection);
            SetLaunchProgress(65, "当前步骤：生成命令");
            var command = await BuildSelectedLaunchCommandAsync(account, token);
            token.ThrowIfCancellationRequested();
            EnsureLaunchSelection(selection);
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_unloaded)
            {
                UpdateLaunchStatus("启动已取消", 0, "当前步骤：已取消");
                OutputBox.Text = "启动已取消。";
            }
        }
        catch (Exception exc)
        {
            UpdateLaunchStatus("启动失败：" + exc.Message, 0, "当前步骤：启动失败");
            OutputBox.Text = "启动失败：" + exc.Message;
        }
        finally
        {
            _launchCancellation = null;
            ++_javaSelectionRequest;
            VersionCenterPanel.IsEnabled = true;
            LaunchEnvironmentExpander.IsEnabled = true;
            LaunchProgressBar.Visibility = Visibility.Collapsed;
            LaunchProgressDetails.Visibility = Visibility.Collapsed;
            UpdateLaunchReadiness();
        }
    }

    private async Task<bool> PrepareJavaForLaunchAsync(CancellationToken cancellationToken = default)
    {
        if (!AutoJavaCheckBox.IsOn) return true;
        _javaSelectionTask = SelectJavaForVersionAsync();
        if (await _javaSelectionTask.WaitAsync(cancellationToken)) return true;
        if (!_unloaded)
        {
            OutputBox.Text = "未找到适合当前版本的 Java，请先下载匹配的运行时，或关闭自动选择后指定 Java 路径。";
        }
        return false;
    }

    private (string? Version, string Directory, string Java) CaptureLaunchSelection()
        => (SelectedVersionId, GameDirectoryBox.Text.Trim(), JavaPathBox.Text.Trim());

    private void EnsureLaunchSelection((string? Version, string Directory, string Java) selection)
    {
        if (_unloaded || CaptureLaunchSelection() != selection)
            throw new InvalidOperationException("启动配置已改变，请重新启动。");
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
            RepairSelectedVersionAsync(record, gameDirectory, versionId, versionJsonPath, token), outputDirectory: gameDirectory);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

    private async void RefreshVersionMods()
    {
        var request = ++_modsRefreshRequest;
        _versionModsByLabel.Clear();
        VersionModsList.ItemsSource = null;
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

        var directory = CurrentModsDirectory();
        SetVersionModControls(false, "正在扫描 Mod...");
        IReadOnlyList<LocalResourceInfo> mods;
        try
        {
            mods = await Task.Run(() => ResourceMarketService.ListLocalResources("", "mod", directory));
        }
        catch (Exception exc)
        {
            if (request == _modsRefreshRequest)
            {
                SetVersionModControls(false, "扫描 Mod 失败：" + exc.Message);
            }
            return;
        }

        if (request != _modsRefreshRequest || SelectedVersionId != versionId || directory != CurrentModsDirectory())
        {
            return;
        }

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
        if (SelectedModVersionId is not string versionId)
        {
            VersionModsStatusText.Text = "请先选择支持 Mod 的本地版本。";
            return;
        }

        var gameVersion = _versionSettingsService.ResolveBaseMinecraftVersion(GameDirectoryBox.Text.Trim(), versionId);
        var loader = ModrinthLoaderForVersion(versionId);
        var targetDirectory = CurrentModsDirectory();
        SetVersionModBusy(true, "已加入全局队列，等待补齐 Mod 前置...");
        DownloadTaskManager.Current.Enqueue("resource_dependencies", $"补齐 {versionId} 的 Mod 前置", (record, token) =>
            RepairVersionModDependenciesAsync(record, gameVersion, loader, targetDirectory, token), outputDirectory: targetDirectory);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        => KnownVersion(versionId)?.DetectedType is "Fabric" or "Forge" or "NeoForge" or "OptiFine";

    private string? SelectedModVersionId
        => SelectedVersionId is string id && VersionSupportsModManagement(id) ? id : null;

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

    internal async Task RunDataSmokeChecksAsync()
    {
        await _initializationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await _javaSelectionTask.WaitAsync(TimeSpan.FromSeconds(30));
        if (!_initialized)
        {
            throw new InvalidOperationException("启动页初始化未完成：" + OutputBox.Text);
        }

        var gameDir = GameDirectoryBox.Text.Trim();
        var installed = await Task.Run(() => _launcher.GetLocalVersions(gameDir));
        if (!installed.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_localVersions.Keys))
        {
            throw new InvalidOperationException("启动页未能列出全部已安装版本：" + OutputBox.Text);
        }
    }

    internal async Task RunJavaSelectionSmokeChecksAsync()
    {
        await RunDataSmokeChecksAsync();
        ++_javaEnvironmentRequest;
        ++_javaSelectionRequest;
        _javaProbes.Clear();
        var paths = new[] { "smoke-java-21.exe", "smoke-java-17.exe", "smoke-java-8.exe" }
            .Select(name => Path.Combine(Path.GetTempPath(), name)).ToArray();
        var majors = new[] { 21, 17, 8 };
        for (var i = 0; i < paths.Length; i++) _javaProbes[paths[i]] = Task.FromResult(majors[i]);
        _javaCandidatesDirectory = GameDirectoryBox.Text.Trim();
        _javaCandidatesTask = Task.FromResult<IReadOnlyList<string>>(paths);
        _versionsDirectory = GameDirectoryBox.Text.Trim();
        _localVersions = new Dictionary<string, LocalVersionCandidate>
        {
            ["smoke-8"] = new("smoke-8", "正式版", 8),
            ["smoke-17"] = new("smoke-17", "正式版", 17),
            ["smoke-21"] = new("smoke-21", "正式版", 21),
            ["smoke-missing"] = new("smoke-missing", "正式版", 99),
        };
        var versions = _localVersions.Values.Select(v => new VersionListItem(v.VersionId, v.DetectedType, null, false)).ToList();
        VersionsList.ItemsSource = versions;
        JavaPathBox.Text = paths[0];
        AutoJavaCheckBox.IsOn = true;
        foreach (var required in new[] { 8, 17, 21 })
        {
            VersionsList.SelectedItem = versions.Single(v => v.VersionId == $"smoke-{required}");
            if (!await _javaSelectionTask || KnownJavaMajor(JavaPathBox.Text) != required
                || JavaCandidatesBox.SelectedItem as string != JavaPathBox.Text)
            {
                throw new InvalidOperationException($"Automatic Java {required} selection did not update the launch path and candidate.");
            }
        }

        var slowProbe = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _javaProbes[paths[1]] = slowProbe.Task;
        VersionsList.SelectedItem = versions.Single(v => v.VersionId == "smoke-17");
        var staleSelection = _javaSelectionTask;
        VersionsList.SelectedItem = versions.Single(v => v.VersionId == "smoke-21");
        await _javaSelectionTask;
        slowProbe.SetResult(17);
        await staleSelection;
        if (KnownJavaMajor(JavaPathBox.Text) != 21)
            throw new InvalidOperationException("An obsolete Java probe overwrote the latest version selection.");

        VersionsList.SelectedItem = versions.Single(v => v.VersionId == "smoke-missing");
        if (await _javaSelectionTask || !JavaVersionText.Text.Contains("99"))
            throw new InvalidOperationException("Missing Java was not reported.");

        AutoJavaCheckBox.IsOn = false;
        JavaPathBox.Text = paths[0];
        VersionsList.SelectedItem = versions.Single(v => v.VersionId == "smoke-8");
        if (JavaPathBox.Text != paths[0] || !JavaPathBox.IsEnabled || !JavaCandidatesBox.IsEnabled)
            throw new InvalidOperationException("Manual Java selection was overwritten.");
    }

    internal void RunSmokeChecks()
    {
        var pageWidth = ViewportWidth;
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

    internal async Task RunLaunchCancellationSmokeAsync()
    {
        var gameDir = Path.Combine(Directory.GetCurrentDirectory(), "launch-workflow-fixture");
        var versionDir = Path.Combine(gameDir, "versions", "smoke-forge");
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, "smoke-forge.json"),
            "{\"id\":\"smoke-forge\",\"mainClass\":\"net.minecraftforge.Main\",\"javaVersion\":{\"majorVersion\":8},\"libraries\":[]}");
        GameDirectoryBox.Text = gameDir;
        AutoJavaCheckBox.IsOn = false;
        JavaPathBox.Text = Path.Combine(gameDir, "no-game-should-start.exe");
        _launchPreflight = new LaunchPreflightService(javaMajorVersionProvider: _ => 8);
        await RefreshVersionListAsync();
        if (SelectedModVersionId != "smoke-forge")
            throw new InvalidOperationException("Mod dependency repair rejected the selected version object.");
        var entry = VersionSettingsService.VersionSettingsEntry(_versionSettings, "smoke-forge");
        entry["pre_launch_command"] = "ping -n 30 127.0.0.1 > nul";
        var operation = LaunchGameAsync();
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!operation.IsCompleted && LaunchStageText.Text != "当前步骤：启动前命令"
                   && timeout.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(25);
            if (operation.IsCompleted || LaunchStageText.Text != "当前步骤：启动前命令")
                throw new InvalidOperationException("Launch did not reach the asynchronous prelaunch command: " + OutputBox.Text);
            if (VersionCenterPanel.IsEnabled || PrimaryLaunchText.Text != "取消启动")
                throw new InvalidOperationException("Launch controls did not enter the cancelable busy state.");
            var owner = _launchCancellation;
            await LaunchGameAsync();
            if (!ReferenceEquals(owner, _launchCancellation))
                throw new InvalidOperationException("A second launch replaced the active operation.");
            PrimaryLaunchAction(this, new RoutedEventArgs());
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
            if (OutputBox.Text != "启动已取消。" || _launchCancellation is not null
                || !VersionCenterPanel.IsEnabled || LaunchProgressBar.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Canceled launch did not restore its idle state.");
        }
        finally
        {
            _launchCancellation?.Cancel();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
        }
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
            if (!await PrepareJavaForLaunchAsync()) return;
            var selection = CaptureLaunchSelection();
            var account = await RefreshSelectedAccountForLaunchAsync();
            EnsureLaunchSelection(selection);
            var command = await BuildSelectedLaunchCommandAsync(account);
            EnsureLaunchSelection(selection);
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
            _versionSettingsService.SaveForGameDirectory(_versionSettings, GameDirectoryBox.Text.Trim());
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

    private Task<List<string>> BuildSelectedLaunchCommandAsync(JsonObject? selectedAccount = null,
        CancellationToken cancellationToken = default)
    {
        var options = CaptureSelectedLaunchOptions(selectedAccount);
        return Task.Run(() => _launcher.BuildLaunchCommand(options), cancellationToken);
    }

    private LaunchOptions CaptureSelectedLaunchOptions(JsonObject? selectedAccount = null)
    {
        if (SelectedVersionId is not string versionId)
        {
            throw new InvalidOperationException("请先选择一个版本。");
        }

        selectedAccount ??= SelectedAccount();
        var options = VersionSettingsService.LaunchOptionsForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
        var maxMemory = ManualMemoryCheckBox.IsChecked == true
            ? UiHelpers.NumberValue(MemorySlider, 1024, 65536, AppSettings.RecommendedMemoryMb())
            : options.MaxMemoryMb;
        var extraJvmArgs = new List<string>();
        if (selectedAccount is not null)
        {
            extraJvmArgs.AddRange(ExternalAuthService.AuthlibInjectorArgs(selectedAccount));
        }

        extraJvmArgs.AddRange(options.ExtraJvmArgs);
        return new LaunchOptions(
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
            GcStrategy: UiHelpers.ComboText(GcStrategyBox, options.GcStrategy));
    }

    private async void RefreshQuickPlayWorlds(object sender, RoutedEventArgs e)
    {
        await RefreshQuickPlayWorldsAsync(CurrentQuickPlayWorld());
    }

    private void RefreshQuickPlayWorlds(string selectedWorld)
        => _ = RefreshQuickPlayWorldsAsync(selectedWorld);

    private async Task RefreshQuickPlayWorldsAsync(string selectedWorld)
    {
        var request = ++_worldsRefreshRequest;
        var savesDir = Path.Combine(CurrentRuntimeDirectory(), "saves");
        QuickPlayBox.IsEnabled = false;
        // Preserve the saved selection while the directory scan is running.
        _quickPlayWorldIds.Clear();
        _quickPlayWorldIds.Add("");
        if (!string.IsNullOrWhiteSpace(selectedWorld))
        {
            _quickPlayWorldIds.Add(selectedWorld);
        }
        QuickPlayBox.ItemsSource = _quickPlayWorldIds.Select(id => id.Length == 0 ? "不快速进入存档" : id).ToList();
        QuickPlayBox.SelectedIndex = _quickPlayWorldIds.Count - 1;
        try
        {
            var worlds = await Task.Run(() => Directory.Exists(savesDir)
                ? Directory.EnumerateDirectories(savesDir)
                    .OrderByDescending(Directory.GetLastWriteTime)
                    .Take(100).Select(Path.GetFileName).OfType<string>().ToList()
                : new List<string>());
            if (request != _worldsRefreshRequest || savesDir != Path.Combine(CurrentRuntimeDirectory(), "saves"))
            {
                return;
            }

            _quickPlayWorldIds.Clear();
            _quickPlayWorldIds.Add("");
            _quickPlayWorldIds.AddRange(worlds);
            QuickPlayBox.ItemsSource = new[] { "不快速进入存档" }.Concat(worlds).ToList();
            var index = _quickPlayWorldIds.FindIndex(item => string.Equals(item, selectedWorld, StringComparison.OrdinalIgnoreCase));
            QuickPlayBox.SelectedIndex = index >= 0 ? index : 0;
            UpdateVersionSummary(SelectedVersionId);
        }
        catch (Exception exc)
        {
            if (request == _worldsRefreshRequest)
            {
                OutputBox.Text = "扫描存档失败：" + exc.Message;
            }
        }
        finally
        {
            if (request == _worldsRefreshRequest)
            {
                QuickPlayBox.IsEnabled = true;
            }
        }
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
        if (_initialized && SelectedAccount() is { } account)
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
            MemorySlider.Value = UiHelpers.ClampDouble(AppSettings.RecommendedMemoryMb(), 1024, 65536);
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
            ? $"最大内存：{UiHelpers.NumberValue(MemorySlider, 1024, 65536, AppSettings.RecommendedMemoryMb())} MB"
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
        _javaProbes[value] = Task.FromResult(knownMajor);
        UpdateLaunchReadiness();
        UpdateVersionSummary(SelectedVersionId);
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
        var requiredJava = KnownVersion(versionId)?.RequiredJava ?? 0;
        var runtime = VersionSettingsService.RuntimeDirectoryForVersion(
            gameDir,
            _versionSettings,
            versionId,
            _settings.Config.GetBool("GAME", "enable_resource_isolation", true));

        // 列表里已经显示版本名与类型，这里补上列表看不到、但决定能不能启动的信息
        var parts = new List<string>();
        var actualJava = KnownJavaMajor(JavaPathBox.Text);
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
        else
        {
            parts.Add("无法读取 Java 需求，请检查版本清单");
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
        var visibility = _launchCancellation is not null || (clamped > 0 && clamped < 100)
            ? Visibility.Visible : Visibility.Collapsed;
        if (LaunchProgressDetails is not null) LaunchProgressDetails.Visibility = visibility;
        if (LaunchProgressBar is not null)
        {
            LaunchProgressBar.Visibility = visibility;
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

    private async Task<JsonObject?> RefreshSelectedAccountForLaunchAsync(CancellationToken cancellationToken = default)
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

            await _microsoftAuthenticator.RefreshAccessTokenAsync(refreshToken, cancellationToken);
            refreshed = JsonNode.Parse(account.ToJsonString())!.AsObject();
            refreshed["access_token"] = _microsoftAuthenticator.MinecraftAccessToken ?? account["access_token"]?.GetValue<string>() ?? "";
            refreshed["refresh_token"] = _microsoftAuthenticator.RefreshToken ?? refreshToken;
        }
        else if (type == "external")
        {
            var service = new ExternalAuthService();
            refreshed = await service.RefreshExternalAccountAsync(account, cancellationToken);
            refreshed["type"] = "external";
            refreshed["auth_server"] ??= account["auth_server"]?.GetValue<string>() ?? account["server"]?.GetValue<string>() ?? "";
            refreshed["authlib_injector_path"] ??= account["authlib_injector_path"]?.GetValue<string>() ?? "";
        }
        else
        {
            return account;
        }

        cancellationToken.ThrowIfCancellationRequested();
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

    private sealed record LocalVersionCandidate(string VersionId, string DetectedType, int RequiredJava);

    private void ResponsiveLayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(ViewportWidth);
    }

    private double ViewportWidth => ActualWidth;

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0 || LaunchActionColumn is null || LaunchIdentityPanel is null)
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
        LaunchActionColumn.Width = wide ? new GridLength(320) : new GridLength(1, GridUnitType.Star);
        LaunchVersionCenterColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(VersionCenterPanel, wide ? 0 : 1);
        Grid.SetColumn(VersionCenterPanel, wide ? 1 : 0);
        LaunchIdentityPanel.Orientation = wide ? Orientation.Vertical : Orientation.Horizontal;
        LaunchIdentityPanel.HorizontalAlignment = wide ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        LaunchIdentityPanel.Spacing = wide ? 6 : 12;
        LaunchAvatarBorder.Width = LaunchAvatarBorder.Height = wide ? 76 : 48;
        LaunchAvatarBorder.CornerRadius = new CornerRadius(wide ? 18 : 8);
        AvatarText.FontSize = wide ? 32 : 24;
        AccountNameText.HorizontalAlignment = AccountTypeText.HorizontalAlignment =
            wide ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        LaunchIdentityCard.Padding = wide ? new Thickness(20, 22, 20, 22) : new Thickness(16);
        LaunchIdentityContent.Spacing = wide ? 16 : 10;
        PrimaryLaunchButton.MinHeight = wide ? 78 : 64;
    }
}
