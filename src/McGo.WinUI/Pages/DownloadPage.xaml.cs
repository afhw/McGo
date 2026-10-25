using McGo.Core.Java;
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
    private bool _autoLoadAttempted;
    private bool _autoLoadingVersions;
    private bool _initialized;
    private int _localVersionScanRequest;
    private IReadOnlyList<string>? _renderedTaskRecords;
    private readonly Dictionary<LoaderPanel, string> _loaderSupportSignatures = [];
    private readonly Dictionary<(string Version, string Mirror), IReadOnlySet<string>> _loaderProbeCache = [];
    private IReadOnlyList<string> _localInstallVersions = [];
    private int _loaderProbeRequest;
    private bool _loadingLocalTargets;

    /// <summary>两个面板各有一份加载器下拉框，可用性要分别计算。</summary>
    private enum LoaderPanel
    {
        Vanilla,
        Addon,
    }

    /// <summary>
    /// 冒烟测试模式下跳过联网探测：截图必须是确定性的，
    /// 而且规则层已经覆盖了「确定不支持」的硬事实。
    /// </summary>
    private static bool IsSmokeMode
        => Environment.GetCommandLineArgs().Contains("--smoke-pages", StringComparer.OrdinalIgnoreCase);

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
        DownloadLogExpander.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, DownloadLogExpanderChanged);
        _initialized = true;
        DownloadSectionBar.SelectedItem = DownloadSectionBar.Items[0];
        LoadSettings();
        Loaded += DownloadPage_Loaded;
        SizeChanged += (_, args) => DownloadRootSizeChanged(this, args);
    }

    private void DownloadTaskManagerStateChanged(object? sender, EventArgs e)
        => UpdateFromAnyThread(UpdateTaskCenter);

    private async void DownloadPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DownloadPage_Loaded;
        await RefreshLocalInstallTargetsAsync();
        await AutoLoadRemoteVersionsAsync();
    }

    /// <summary>
    /// 进入下载页就静默拉取版本列表，用户不必先点一次“获取列表”。
    /// 失败不弹窗打扰：只写日志并在按钮副标题提示，可手动刷新重试。
    /// </summary>
    private async Task AutoLoadRemoteVersionsAsync()
    {
        if (_autoLoadAttempted)
        {
            return;
        }

        _autoLoadAttempted = true;
        if (RemoteVersionsBox.Items.Count > 0)
        {
            return;
        }

        _autoLoadingVersions = true;
        UpdateDownloadButtonState();
        try
        {
            var versions = await _installServices.GetRemoteVersionsAsync(UiHelpers.ComboText(VersionTypeBox, "release"), UiHelpers.ComboText(MirrorBox, "official"));
            if (versions.Count > 0)
            {
                var previous = RemoteVersionsBox.SelectedItem?.ToString();
                RemoteVersionsBox.ItemsSource = versions;
                var index = previous is null ? 0 : versions.ToList().IndexOf(previous);
                RemoteVersionsBox.SelectedIndex = index >= 0 ? index : 0;
            }

            AppendDownload($"已自动获取 {versions.Count} 个远程版本。");
        }
        catch (Exception exc)
        {
            AppendDownload("自动获取版本列表失败：" + exc.Message + "（可点“刷新”重试）");
        }
        finally
        {
            _autoLoadingVersions = false;
            UpdateDownloadButtonState();
        }
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
        UiHelpers.SetComboText(DownloadPresetBox, _settings.Config.Get("DOWNLOAD", "download_preset", "均衡"));
        UiHelpers.SetComboTag(InstallTypeBox, "");
        UiHelpers.SetComboText(CacheStrategyBox, _settings.Config.Get("DOWNLOAD", "cache_strategy", "reuse"));
        CoreThreadsBox.Value = UiHelpers.ClampDouble(_settings.Config.GetInt("DOWNLOAD", "max_core_threads", 12), 1, 64);
        AssetThreadsBox.Value = UiHelpers.ClampDouble(_settings.Config.GetInt("DOWNLOAD", "max_asset_threads", 24), 1, 96);
        SpeedLimitBox.Value = UiHelpers.ClampDouble(_settings.Config.GetInt("DOWNLOAD", "speed_limit_kbps", 0), 0, 1_048_576);
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
        UpdateDownloadButtonState();
        UpdateSettingsSummary();
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

        SelectNoLoader(InstallTypeBox);
        UpdateInstallAddonControls();
        if (InstallVersionPanel.Visibility != Visibility.Collapsed || InstallFabricApiCheckBox.IsEnabled)
        {
            throw new InvalidOperationException("未选择加载器时安装选项状态不正确。");
        }

        UiHelpers.SetComboTag(InstallTypeBox, "fabric");
        UpdateInstallAddonControls();
        if (InstallVersionPanel.Visibility != Visibility.Visible || !InstallFabricApiCheckBox.IsEnabled)
        {
            throw new InvalidOperationException("选择 Fabric 后安装选项状态不正确。");
        }

        // 主按钮必须写出当前将要安装的版本与加载器
        RemoteVersionsBox.ItemsSource = new[] { "1.20.1" };
        RemoteVersionsBox.SelectedIndex = 0;
        UpdateDownloadButtonState();
        if (!DownloadButtonText.Text.Contains("1.20.1", StringComparison.Ordinal) ||
            !DownloadButtonText.Text.Contains("Fabric", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("下载主按钮没有说明将要安装的版本与加载器。");
        }

        CheckLoaderAvailability();

        RemoteVersionsBox.ItemsSource = null;
        SelectNoLoader(InstallTypeBox);
        UpdateInstallAddonControls();
    }

    /// <summary>
    /// 「加载器 × 版本」禁用规则的确定性检查（不联网，只用规则层）。
    /// 1.21.1 四个加载器都真实存在 → 一个都不许灰；
    /// 1.12.2 只有 Forge/OptiFine（Fabric 1.14 起、NeoForge 1.20.1 起）→ 这两个必须灰；
    /// 「只装原版」永远不许灰。
    /// </summary>
    private void CheckLoaderAvailability()
    {
        ApplyLoaderAvailability(LoaderPanel.Vanilla, "1.21.1", LoaderSupportRules.UnavailableLoaders("1.21.1"));
        if (IsLoaderDisabled(InstallTypeBox, item => item.Tag?.ToString() ?? "", "fabric") ||
            IsLoaderDisabled(InstallTypeBox, item => item.Tag?.ToString() ?? "", "forge") ||
            IsLoaderDisabled(InstallTypeBox, item => item.Tag?.ToString() ?? "", "neoforge") ||
            IsLoaderDisabled(InstallTypeBox, item => item.Tag?.ToString() ?? "", "optifine"))
        {
            throw new InvalidOperationException("1.21.1 上不应禁用任何加载器。");
        }

        // 「只装原版」是合法默认值，任何情况下都必须可用。
        var vanillaItem = InstallTypeBox.Items.OfType<ComboBoxItem>().First();
        if (vanillaItem.IsEnabled == false || (vanillaItem.Tag?.ToString() ?? "") != "")
        {
            throw new InvalidOperationException("「只装原版」选项不能失能或改标签。");
        }

        ApplyLoaderAvailability(LoaderPanel.Addon, "1.12.2", LoaderSupportRules.UnavailableLoaders("1.12.2"));
        if (!IsLoaderDisabled(AddonInstallTypeBox, item => item.Content?.ToString() ?? "", "fabric") ||
            !IsLoaderDisabled(AddonInstallTypeBox, item => item.Content?.ToString() ?? "", "neoforge") ||
            IsLoaderDisabled(AddonInstallTypeBox, item => item.Content?.ToString() ?? "", "forge") ||
            IsLoaderDisabled(AddonInstallTypeBox, item => item.Content?.ToString() ?? "", "optifine"))
        {
            throw new InvalidOperationException("1.12.2 上加载器禁用状态不正确。");
        }

        // Fabric 不可用时 Fabric API 必须一起禁用（它依赖 Fabric）。
        if (!IsLoaderDisabled(AddonInstallTypeBox, item => item.Content?.ToString() ?? "", "fabric_api"))
        {
            throw new InvalidOperationException("Fabric 不可用时 Fabric API 仍可选。");
        }

        // 选中项被禁用时必须退回一个仍然可用项，不能留下灰色选中态。
        if (AddonInstallTypeBox.SelectedItem is ComboBoxItem selectedAddon && selectedAddon.IsEnabled == false)
        {
            throw new InvalidOperationException("加载器被禁用后仍停留在灰色选中项。");
        }

        // 回到正常状态，避免把测试用的禁用状态留给后续截图。
        ApplyLoaderAvailability(LoaderPanel.Addon, "1.21.1", LoaderSupportRules.UnavailableLoaders("1.21.1"));
    }

    private static bool IsLoaderDisabled(ComboBox combo, Func<ComboBoxItem, string> typeOf, string type)
        => combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(typeOf(item), type, StringComparison.OrdinalIgnoreCase))
            ?.IsEnabled == false;

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
        FrameworkElement target = section switch
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

    /// <summary>
    /// 窄窗口把右栏（进度/日志）下沉到主内容下方并占满整行；
    /// 否则两列会在 <900 DIP 时重叠。
    /// </summary>
    private void DownloadRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = XamlRoot?.Size.Width ?? e.NewSize.Width;
        if (width <= 0)
        {
            return;
        }

        var wide = width >= 900;
        DownloadSideColumn.Width = wide ? new GridLength(320) : new GridLength(0);
        Grid.SetRow(DownloadSidePanel, wide ? 2 : 3);
        Grid.SetColumn(DownloadSidePanel, wide ? 1 : 0);
        Grid.SetColumnSpan(DownloadSidePanel, wide ? 1 : 2);
    }

    private static void Navigate(string tag)
    {
        if (App.MainWindow is MainWindow window)
        {
            window.NavigateToTag(tag);
        }
    }

    /// <summary>
    /// 窄窗口下右栏会下沉到任务卡下方，展开后的日志区可能整个落在视口之外，
    /// 用户看到的就是"点了没反应"。展开后把自己滚进视野，保证日志真的出现在眼前。
    /// 用属性变化回调而不是 XAML 的 Expanded 事件：后者会让 XamlCompiler 静默崩溃。
    /// </summary>
    private static void DownloadLogExpanderChanged(DependencyObject sender, DependencyProperty args)
    {
        if (sender is not Expander expander || !expander.IsExpanded)
        {
            return;
        }

        expander.DispatcherQueue.TryEnqueue(() => expander.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0.25,
        }));
    }

    private void StayOnDownloads(object sender, RoutedEventArgs e) => AppendDownload("已停留在下载任务页。");
    private void OpenResources(object sender, RoutedEventArgs e) => Navigate("resources");
    private void OpenModpacks(object sender, RoutedEventArgs e) => Navigate("modpacks");

    private async void RefreshRemoteVersions(object sender, RoutedEventArgs e)
    {
        if (_autoLoadingVersions)
        {
            return;
        }

        try
        {
            DownloadButton.IsEnabled = false;
            AppendDownload("正在刷新远程版本...");
            var versions = await _installServices.GetRemoteVersionsAsync(UiHelpers.ComboText(VersionTypeBox, "release"), UiHelpers.ComboText(MirrorBox, "official"));
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

    private void RemoteVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDownloadButtonState();
        // 选定版本变化后必须重新算「加载器 × 版本」可用性：
        // 老版本/快照上 Fabric、NeoForge 往往根本不存在，提前置灰比让用户选完再报错好。
        RefreshLoaderAvailability(LoaderPanel.Vanilla);
    }

    /// <summary>本地目标版本变化时，重新计算「安装扩展」面板里的加载器可用性。</summary>
    private void InstallTargetVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _loadingLocalTargets)
        {
            return;
        }

        RefreshLoaderAvailability(LoaderPanel.Addon);
    }

    /// <summary>当前面板对应的原版 Minecraft 版本：远程版本直接用，本地版本要反推原版。</summary>
    private string LoaderVersionFor(LoaderPanel panel)
    {
        if (panel == LoaderPanel.Vanilla)
        {
            return RemoteVersionsBox.SelectedItem?.ToString() ?? "";
        }

        var selected = InstallTargetVersionBox.SelectedItem?.ToString() ?? "";
        if (selected.Length == 0 || !_localInstallVersions.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            return "";
        }

        return _versionSettingsService.ResolveBaseMinecraftVersion(GameDirectoryBox.Text.Trim(), selected);
    }

    /// <summary>
    /// 刷新对应面板的加载器可用性。
    /// 先用纯规则立刻出结果（不联网、不闪烁），再用真实元数据补一次确认。
    /// 任何网络失败都保持可用：错误置灰一个本来能装的加载器，比让用户点一下再看到失败更糟。
    /// </summary>
    private async void RefreshLoaderAvailability(LoaderPanel panel)
    {
        var versionId = LoaderVersionFor(panel);
        ApplyLoaderAvailability(panel, versionId, LoaderSupportRules.UnavailableLoaders(versionId));

        // 冒烟测试跑在无网络/真实网络的混合环境里，联网探测会让截图不稳定；
        // 规则层已经覆盖了「确定不支持」的硬事实，这里直接跳过在线确认。
        if (versionId.Length == 0 || IsSmokeMode)
        {
            return;
        }

        // 结果按「版本 + 镜像」缓存：用户在版本列表里上下滚动时，
        // 每个版本都要打 3~4 个网络请求会明显卡顿，而同一个版本的答案是稳定的。
        var cacheKey = (versionId, UiHelpers.ComboText(MirrorBox, "official"));
        if (_loaderProbeCache.TryGetValue(cacheKey, out var cached))
        {
            ApplyLoaderAvailability(panel, versionId, cached);
            return;
        }

        var request = ++_loaderProbeRequest;
        var mirror = cacheKey.Item2;
        IReadOnlySet<string> confirmed;
        try
        {
            confirmed = await Task.Run(() => ProbeUnavailableLoadersAsync(versionId, mirror));
        }
        catch
        {
            return;
        }

        if (request != _loaderProbeRequest)
        {
            return;
        }

        _loaderProbeCache[cacheKey] = confirmed;
        ApplyLoaderAvailability(panel, versionId, confirmed);
    }

    /// <summary>
    /// 用真实元数据确认哪些加载器在该版本上确实不存在。
    /// 只有"查询成功但结果为空"才算不存在；查询抛异常一律视为不确定（保持可用）。
    /// </summary>
    private async Task<IReadOnlySet<string>> ProbeUnavailableLoadersAsync(string versionId, string mirror)
    {
        var unavailable = new HashSet<string>(LoaderSupportRules.UnavailableLoaders(versionId), StringComparer.OrdinalIgnoreCase);
        var checks = new List<Task>();

        if (!unavailable.Contains("fabric"))
        {
            checks.Add(CountAsync("fabric", async () => (await _installServices.GetFabricLoaderVersionsAsync(versionId, mirror)).Count));
        }

        if (!unavailable.Contains("neoforge"))
        {
            checks.Add(CountAsync("neoforge", async () => (await _installServices.GetNeoForgeListAsync(versionId, mirror)).Count));
        }

        if (!unavailable.Contains("forge"))
        {
            checks.Add(CountAsync("forge", async () => (await _installServices.GetForgeMavenVersionsForMcAsync(versionId, mirror)).Count));
        }

        if (!unavailable.Contains("optifine"))
        {
            checks.Add(CountAsync("optifine", async () =>
            {
                var list = await _installServices.GetOptifineListAsync(mirror);
                return list.OfType<JsonObject>().Count(item =>
                    string.Equals(item["mcversion"]?.GetValue<string>() ?? "", versionId, StringComparison.OrdinalIgnoreCase));
            }));
        }

        await Task.WhenAll(checks).ConfigureAwait(false);
        return unavailable;

        async Task CountAsync(string type, Func<Task<int>> load)
        {
            try
            {
                if (await load().ConfigureAwait(false) == 0)
                {
                    lock (unavailable)
                    {
                        unavailable.Add(type);
                    }
                }
            }
            catch
            {
                // 网络/解析失败：不确定，保持可用。
            }
        }
    }

    private void ApplyLoaderAvailability(LoaderPanel panel, string versionId, IReadOnlySet<string> unavailable)
    {
        var signature = versionId + "|" + string.Join(',', unavailable.OrderBy(value => value, StringComparer.Ordinal));
        if (string.Equals(_loaderSupportSignatures.GetValueOrDefault(panel), signature, StringComparison.Ordinal))
        {
            return;
        }

        _loaderSupportSignatures[panel] = signature;
        if (panel == LoaderPanel.Vanilla)
        {
            // InstallTypeBox 用 Tag 标识加载器，「只装原版」的 Tag 为空串，永远不会被禁用。
            ApplyToCombo(InstallTypeBox, item => item.Tag?.ToString() ?? "", unavailable);
        }
        else
        {
            ApplyToCombo(AddonInstallTypeBox, item => item.Content?.ToString() ?? "", unavailable);
        }
    }

    /// <summary>
    /// 只禁用「确定不存在」的加载器项，并保留「只装原版」（Tag=""）这类无加载器选项始终可用。
    /// 已选中的项被禁用时退回第一个仍可用的项：InstallTypeBox 的第一项是「只装原版」，
    /// 而 AddonInstallTypeBox 的第一项就是 fabric，所以不能无脑 SelectedIndex=0
    /// （否则 1.12.2 这种没有 Fabric 的版本会"退回"到一个灰掉的 Fabric）。
    /// Fabric API 依赖 Fabric，所以 Fabric 不可用时它同样不可用。
    /// </summary>
    private static void ApplyToCombo(ComboBox combo, Func<ComboBoxItem, string> typeOf, IReadOnlySet<string> unavailable)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            var type = typeOf(item);
            if (type.Length == 0)
            {
                continue;
            }

            var blocked = IsBlocked(type, unavailable);
            if (item.IsEnabled == !blocked)
            {
                continue;
            }

            item.IsEnabled = !blocked;
        }

        if (combo.SelectedItem is ComboBoxItem selected && IsBlocked(typeOf(selected), unavailable))
        {
            var fallback = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.IsEnabled);
            if (fallback is not null)
            {
                combo.SelectedItem = fallback;
            }
        }
    }

    private static bool IsBlocked(string loaderType, IReadOnlySet<string> unavailable)
        => loaderType.Length > 0 &&
           (loaderType == "fabric_api" ? unavailable.Contains("fabric") : unavailable.Contains(loaderType));

    /// <summary>
    /// 刷新安装器版本后顺手更新下拉框的置灰状态：
    /// 查到版本 → 恢复可用，查不到 → 置灰。只影响刚刚查过的那个加载器。
    /// </summary>
    private void MarkLoaderAvailability(LoaderPanel panel, string loaderType, bool available)
    {
        if (loaderType.Length == 0)
        {
            return;
        }

        var combo = panel == LoaderPanel.Vanilla ? InstallTypeBox : AddonInstallTypeBox;
        var typeOf = panel == LoaderPanel.Vanilla
            ? (Func<ComboBoxItem, string>)(item => item.Tag?.ToString() ?? "")
            : item => item.Content?.ToString() ?? "";
        var item = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(typeOf(candidate), loaderType, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.IsEnabled == available)
        {
            return;
        }

        item.IsEnabled = available;
        if (!available)
        {
            // 手动结论优先于缓存签名，下次版本变化时重新判断。
            _loaderSupportSignatures.Remove(panel);
            if (ReferenceEquals(combo.SelectedItem, item))
            {
                combo.SelectedIndex = 0;
            }
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

        var mirror = UiHelpers.ComboText(MirrorBox, "official");
        var gameDir = GameDirectoryBox.Text.Trim();
        var javaPath = PickInstallerJava(JavaPathBox.Text.Trim());
        var installTypes = SelectedInstallTypes();
        if (installTypes.Any(type => type is "forge" or "neoforge") && string.IsNullOrWhiteSpace(javaPath))
        {
            AppendDownload("自动安装 Forge 或 NeoForge 需要可用的 Java 路径。");
            return;
        }

        _settings.Config.Set("GAME", "directory", gameDir);
        _settings.Config.Set("JAVA", "path", javaPath);
        _settings.Config.Set("DOWNLOAD", "mirror_source", mirror);
        _settings.Config.Set("DOWNLOAD", "download_preset", UiHelpers.ComboText(DownloadPresetBox, "均衡"));
        _settings.Config.Set("DOWNLOAD", "max_core_threads", UiHelpers.NumberValue(CoreThreadsBox, 1, 64, 12).ToString());
        _settings.Config.Set("DOWNLOAD", "max_asset_threads", UiHelpers.NumberValue(AssetThreadsBox, 1, 96, 24).ToString());
        _settings.Config.Set("DOWNLOAD", "speed_limit_kbps", UiHelpers.NumberValue(SpeedLimitBox, 0, 1_048_576, 0).ToString());
        _settings.Config.Set("DOWNLOAD", "cache_strategy", UiHelpers.ComboText(CacheStrategyBox, "reuse"));
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
        var installType = UiHelpers.ComboTag(InstallTypeBox, "");
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
        var selected = UiHelpers.ComboText(InstallVersionBox, "自动选择最新");
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

        var presetName = UiHelpers.ComboText(DownloadPresetBox, "均衡");
        if (!DownloadPresets.TryGetValue(presetName, out var preset))
        {
            return;
        }

        CoreThreadsBox.Value = preset.CoreThreads;
        AssetThreadsBox.Value = preset.AssetThreads;
        SpeedLimitBox.Value = preset.SpeedLimitKbps;
        UiHelpers.SetComboText(CacheStrategyBox, preset.CacheStrategy);
        AppendDownload($"已应用下载预设：{presetName}。");
    }

    private void InstallTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInstallAddonControls();
        ResetInstallVersionChoices("点击刷新安装器版本");
    }

    private async void RefreshInstallVersions(object sender, RoutedEventArgs e)
    {
        var installType = UiHelpers.ComboTag(InstallTypeBox, "");
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
            var mirror = UiHelpers.ComboText(MirrorBox, "official");
            var versions = await LoadInstallVersionsAsync(installType, versionId, mirror);
            InstallVersionBox.ItemsSource = new[] { "自动选择最新" }.Concat(versions).ToList();
            InstallVersionBox.SelectedIndex = 0;
            if (versions.Count == 0)
            {
                AppendDownload($"未找到 {InstallTypeLabel(installType)} 适用于 {versionId} 的安装器版本，将使用自动选择。");
            }
            else
            {
                AppendDownload($"已获取 {InstallTypeLabel(installType)} 安装器版本 {versions.Count} 个。");
            }

            // 刷新结果本身就是最可靠的可用性证据：查不到就是该版本真的没有这个加载器。
            MarkLoaderAvailability(LoaderPanel.Vanilla, installType, versions.Count > 0);
        }
        catch (Exception exc)
        {
            ResetInstallVersionChoices("自动选择最新");
            AppendDownload($"刷新{InstallTypeLabel(installType)}安装器版本失败：{AppSettings.ConciseDownloadError(exc.Message)}");
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
        _loadingLocalTargets = true;
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
                _localInstallVersions = [];
                UpdateInstallStatus("本地版本扫描失败", exc.Message);
            }

            return;
        }
        finally
        {
            _loadingLocalTargets = false;
        }

        if (request != _localVersionScanRequest)
        {
            return;
        }

        // 先记下版本清单再设 ItemsSource/SelectedIndex：
        // 赋值会同步触发 SelectionChanged，而它要靠 _localInstallVersions 才能反推出原版版本号。
        _localInstallVersions = versions;
        InstallTargetVersionBox.ItemsSource = versions.Count == 0 ? new[] { "请先下载本地版本" } : versions;
        InstallTargetVersionBox.SelectedIndex = 0;
        UpdateInstallStatus(versions.Count == 0 ? "没有可安装扩展的本地版本" : "等待安装任务", versions.Count == 0 ? "请先下载原版或选择已有版本目录" : $"发现 {versions.Count} 个本地版本");
        RefreshLoaderAvailability(LoaderPanel.Addon);
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
        var installType = UiHelpers.ComboText(AddonInstallTypeBox, "fabric");
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
            var mirror = UiHelpers.ComboText(MirrorBox, "official");
            var versions = await LoadInstallVersionsAsync(installType, minecraftVersion, mirror);
            AddonInstallVersionBox.ItemsSource = new[] { "自动选择最新" }.Concat(versions).ToList();
            AddonInstallVersionBox.SelectedIndex = 0;
            UpdateInstallStatus("安装器版本已刷新", versions.Count == 0 ? "未找到可选版本，将使用自动选择" : $"已获取 {versions.Count} 个版本");
            AppendDownload(versions.Count == 0
                ? $"未找到 {InstallTypeLabel(installType)} 适用于 {minecraftVersion} 的安装器版本，将使用自动选择。"
                : $"已获取 {InstallTypeLabel(installType)} 安装器版本 {versions.Count} 个。");
            MarkLoaderAvailability(LoaderPanel.Addon, installType, versions.Count > 0);
        }
        catch (Exception exc)
        {
            ResetAddonInstallVersionChoices("自动选择最新");
            var message = AppSettings.ConciseDownloadError(exc.Message);
            UpdateInstallStatus("刷新安装器版本失败", message);
            AppendDownload($"刷新{InstallTypeLabel(installType)}安装器版本失败：{message}");
        }
    }

    private async void InstallAddonToLocalVersion(object sender, RoutedEventArgs e)
    {
        var installType = UiHelpers.ComboText(AddonInstallTypeBox, "fabric");
        var targetVersion = InstallTargetVersionBox.SelectedItem?.ToString() ?? "";
        var gameDir = GameDirectoryBox.Text.Trim();
        var javaPath = PickInstallerJava(JavaPathBox.Text.Trim());
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
        _settings.Config.Set("DOWNLOAD", "mirror_source", UiHelpers.ComboText(MirrorBox, "official"));
        _settings.SaveConfig();

        var requestedVersion = SelectedAddonInstallVersion();
        var mirror = UiHelpers.ComboText(MirrorBox, "official");
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
        var installType = UiHelpers.ComboTag(InstallTypeBox, "");
        var isFabric = installType == "fabric";
        var hasInstaller = installType.Length > 0;
        InstallVersionPanel.Visibility = hasInstaller ? Visibility.Visible : Visibility.Collapsed;
        InstallFabricApiCheckBox.IsEnabled = isFabric;
        if (!isFabric)
        {
            InstallFabricApiCheckBox.IsChecked = false;
        }

        UpdateDownloadButtonState();
    }

    /// <summary>
    /// 主按钮直接说明将要发生什么：没列表时提示获取，选好版本后把版本名印在按钮上。
    /// </summary>
    private void UpdateDownloadButtonState()
    {
        if (DownloadButtonText is null)
        {
            return;
        }

        var versionId = RemoteVersionsBox?.SelectedItem?.ToString() ?? "";
        var installType = UiHelpers.ComboTag(InstallTypeBox, "");
        var loader = installType.Length == 0 ? "" : " + " + InstallTypeLabel(installType);
        if (string.IsNullOrWhiteSpace(versionId))
        {
            DownloadButtonText.Text = "下载所选版本";
            DownloadButtonSubText.Text = RemoteVersionsBox?.Items.Count > 0 ? "请选择一个版本" : "版本列表加载中，可点刷新重试";
            return;
        }

        DownloadButtonText.Text = $"下载 {versionId}{loader}";
        DownloadButtonSubText.Text = $"{UiHelpers.ComboText(MirrorBox, "official")} · {GameDirectoryBox?.Text.Trim()}".Trim();
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
        var selected = UiHelpers.ComboText(AddonInstallVersionBox, "自动选择最新");
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
        if (AddonButtonText is not null)
        {
            AddonButtonText.Text = $"安装 {InstallTypeLabel(UiHelpers.ComboText(AddonInstallTypeBox, "fabric"))}";
        }
    }

    /// <summary>折叠面板标题只写最关键的三个设置，避免用户为了确认而展开。</summary>
    private void UpdateSettingsSummary()
    {
        if (DownloadSettingsSummary is null)
        {
            return;
        }

        var mirror = UiHelpers.ComboText(MirrorBox, "official") == "bmclapi" ? "BMCLAPI" : "官方源";
        var preset = UiHelpers.ComboText(DownloadPresetBox, "均衡");
        var dir = GameDirectoryBox?.Text.Trim() ?? "";
        DownloadSettingsSummary.Text = $"{mirror} · {preset} · {dir}";
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
        else
        {
            // 没有正在运行的任务时必须清掉上一轮的实时指标，
            // 否则「正在取消…」这类旧文案会一直挂着，和「当前任务：无」自相矛盾。
            var lastRecord = _taskManager.Records.LastOrDefault();
            DownloadProgressBar.Value = _taskManager.QueuedCount == 0 ? lastRecord?.Progress ?? 0 : 0;
            UpdateDownloadMetrics(lastRecord is null ? "等待下载" : "已结束：" + StatusLabel(lastRecord.Status));
        }
        RefreshTaskRecords();
    }

    /// <summary>
    /// 任务列表只在内容真正变化时重设 ItemsSource。
    /// 进度回调一秒能来几十次，而每次重设 ItemsSource 都会把 ListView 的容器全部丢弃重建，
    /// 表现为整块列表肉眼可见的闪烁跳动；内容一模一样时刷新纯属浪费。
    /// </summary>
    private void RefreshTaskRecords()
    {
        var records = RenderTaskRecords();
        if (_renderedTaskRecords is not null && _renderedTaskRecords.SequenceEqual(records, StringComparer.Ordinal))
        {
            return;
        }

        _renderedTaskRecords = records;
        TaskRecordsList.ItemsSource = records;
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
            // 列表只放稳定的信息（状态 + 标题）。进度、速度、文件计数每秒都在变，
            // 放进来会让 ItemsSource 每秒重设一次，ListView 不停重建容器导致整块不显示。
            // 实时进度统一在右侧「进度」卡片展示。
            var builder = new System.Text.StringBuilder();
            builder.Append('[').Append(StatusLabel(record.Status)).Append("] ")
                .Append(record.Title);
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

                /// <summary>选中“只装原版”（即不装加载器）。</summary>
    private static void SelectNoLoader(ComboBox combo) => combo.SelectedIndex = 0;

        /// <summary>选中“只装原版”（即不装加载器）。</summary>

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

    /// <summary>
    /// 为加载器安装器挑选合适的 Java。
    /// Forge/NeoForge 的安装器与目标 MC 版本强绑定：给 1.8.9 的安装器配 Java 21 会直接失败
    /// （installer 内部用旧版 ASM/编译器，无法在高版本 JVM 上运行）。
    /// 策略：优先用能匹配目标版本的 Java，找不到就明确报错而不是硬用当前选中的 Java。
    /// </summary>
    private string PickInstallerJava(string configuredJava)
    {
        var target = InstallTargetVersionBox.SelectedItem?.ToString() ?? RemoteVersionsBox.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(target))
        {
            return configuredJava;
        }

        var needed = _launcher.InferRequiredJavaVersion(GameDirectoryBox.Text.Trim(), target);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredJava))
        {
            candidates.Add(configuredJava);
        }

        // 加上官方启动器自带的运行时（常见于 .minecraft\runtime），旧版 Forge 会用到 Java 8
        candidates.AddRange(JavaRuntimeService.FindBundledRuntimeCandidates());
        foreach (var pathDir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(pathDir, OperatingSystem.IsWindows() ? "java.exe" : "java"));
        }

        foreach (var candidate in candidates.Distinct())
        {
            if (JavaRuntimeService.GetJavaMajorVersion(candidate) == needed)
            {
                return candidate;
            }
        }

        // 没有匹配的运行时：把当前 Java 交给上层，让安装器报出明确错误，
        // 同时写日志提示用户应该装哪个版本，避免"莫名其妙装失败"。
        var actual = JavaRuntimeService.GetJavaMajorVersion(configuredJava);
        AppendDownload(
            $"提示：安装 {target} 需要 Java {needed}，当前选择的是 Java {(actual > 0 ? actual.ToString() : "未知")}。" +
            "若安装失败，请在启动页「账号与运行环境」里换成匹配的 Java。");
        return configuredJava;
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
