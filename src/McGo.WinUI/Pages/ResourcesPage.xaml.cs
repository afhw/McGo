using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Formatting;
using McGo.Core.Minecraft;
using McGo.Core.Resources;
using McGo.WinUI.Dialogs;

namespace McGo.WinUI.Pages;

public sealed partial class ResourcesPage : Page
{
    private readonly AppSettings _settings = new();
    private readonly LauncherService _launcher = new();
    private readonly VersionSettingsService _versionSettingsService;
    private readonly ResourceMarketService _resourceMarket = new();
    private JsonObject _versionSettings = [];
    private readonly Dictionary<string, JsonObject> _resourceHitsByLabel = [];
    private readonly Dictionary<string, LocalResourceInfo> _localResourcesByLabel = [];
    private readonly Queue<JsonObject> _resourceDetailQueue = new();
    private List<string> _allLocalVersions = [];
    private List<JsonObject> _resourceSearchHits = [];
    private int _resourceSearchGeneration;
    private bool _resourceDetailRunning;
    private bool _resourceBusy;
    private bool _initialized;
    private bool _wideLayout;
    private bool _responsiveLayoutInitialized;

    public ResourcesPage()
    {
        InitializeComponent();
        _versionSettingsService = new VersionSettingsService(_launcher);
        LoadState();
        _initialized = true;
        ResourceSectionBar.SelectedItem = ResourceSectionBar.Items[0];
    }

    /// <summary>视口宽度：XamlRoot.Size 才是真实客户区，容器宽度可能按内容收缩。</summary>
    private double ViewportWidth
    {
        get
        {
            var root = XamlRoot?.Size.Width ?? 0;
            return root > 0 ? root : (XamlRoot?.Size.Width ?? 0);
        }
    }

    private void ResponsiveLayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout(ViewportWidth);

    /// <summary>宽窗口左主右辅；窄窗口单列堆叠，主要搜索区始终排在最前。</summary>
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
        ResourcePrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        ResourceSideColumn.Width = wide ? new GridLength(320) : new GridLength(0);
        Grid.SetRow(ResourceSidePanel, wide ? 2 : 3);
        Grid.SetColumn(ResourceSidePanel, wide ? 1 : 0);
        Grid.SetColumnSpan(ResourceSidePanel, wide ? 1 : 2);
    }

    /// <summary>回车即搜索，省掉一次鼠标移动。</summary>
    private void ResourceQueryKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        SearchResources(sender, new RoutedEventArgs());
    }

    /// <summary>日志折叠时把最后一条消息显示在标题上，不用展开也能看到结果。</summary>
    private void ResourceOutputChanged(object sender, TextChangedEventArgs e)
    {
        if (ResourceOutputPreview is null)
        {
            return;
        }

        var text = ResourceOutputBox.Text ?? "";
        var preview = string.Join(
            " · ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
        if (preview.Length > 72)
        {
            preview = preview[..72] + "…";
        }

        ResourceOutputPreview.Text = preview.Length == 0 ? "安装、依赖与状态消息" : preview;
        if (preview.Length > 0)
        {
            ToolTipService.SetToolTip(ResourceOutputPreview, preview);
        }
    }

    private void RevealResourceDetail() => ResourceDetailExpander.IsExpanded = true;

    private void RevealResourceLog() => ResourceOutputExpander.IsExpanded = true;

    /// <summary>写日志并展开折叠面板：失败原因不该藏在收起的区域里。</summary>
    private void ShowResourceMessage(string message)
    {
        ResourceOutputBox.Text = message;
        RevealResourceLog();
    }

    private void ResourceSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        var section = (sender.SelectedItem as SelectorBarItem)?.Tag?.ToString() ?? "market";
        var market = section == "market";
        ResourceSearchPanel.Visibility = market ? Visibility.Visible : Visibility.Collapsed;
        ResourceResultsPanel.Visibility = market ? Visibility.Visible : Visibility.Collapsed;
        LocalResourcesPanel.Visibility = market ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadState()
    {
        _settings.LoadConfig();
        _versionSettings = _versionSettingsService.LoadVersionSettings();
        _resourceMarket.ModrinthApiBase = _settings.Config.Get("DOWNLOAD", "modrinth_api_base", "");
        if (string.IsNullOrWhiteSpace(_resourceMarket.ModrinthApiBase))
        {
            _resourceMarket.ModrinthApiBase = "https://api.modrinth.com/v2";
        }

        _resourceMarket.ModrinthCdnBase = _settings.Config.Get("DOWNLOAD", "modrinth_cdn_base", "");
        var gameDir = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        _allLocalVersions = _launcher.GetLocalVersions(gameDir).ToList();
        RefreshResourceTargetVersions();
    }

    private async void SearchResources(object sender, RoutedEventArgs e)
    {
        SetResourceBusy(true, "正在搜索资源...");
        try
        {
            var source = UiHelpers.ComboText(ResourceSourceBox, "modrinth");
            if (UiHelpers.ComboText(ResourceTypeBox, "mod") == "mod" && !VersionSupportsModManagement(CurrentResourceVersion()))
            {
                ShowResourceMessage("Mod 安装目标需要选择 Fabric、Forge、NeoForge 或 OptiFine 版本。");
                ResourceStatusText.Text = "当前目标版本不支持 Mod。";
                return;
            }

            ResourceOutputBox.Text = source == "local" ? "正在扫描本地资源..." : "正在搜索资源...";
            ResourceStatusText.Text = ResourceOutputBox.Text;
            ResourceDetailBox.Text = "";
            ResourceScreenshotsList.ItemsSource = null;
            _resourceSearchGeneration++;

            if (source == "local")
            {
                var localHits = ResourceMarketService.ListLocalResources(ResourceQueryBox.Text.Trim(), UiHelpers.ComboText(ResourceTypeBox, "mod"), CurrentResourceTargetDir())
                    .Select(LocalResourceToHit)
                    .ToList();
                SetResourceResults(localHits);
                ResourceOutputBox.Text = $"找到 {localHits.Count} 个本地资源。";
                ResourceStatusText.Text = ResourceOutputBox.Text;
                return;
            }

            if (string.IsNullOrWhiteSpace(ResourceQueryBox.Text))
            {
                ShowResourceMessage("请输入要搜索的资源名称。");
                ResourceStatusText.Text = ResourceOutputBox.Text;
                ResourceQueryBox.Focus(FocusState.Programmatic);
                return;
            }

            var hits = await _resourceMarket.SearchModrinthResourcesAsync(
                ResourceQueryBox.Text.Trim(),
                UiHelpers.ComboText(ResourceTypeBox, "mod"),
                ResourceGameVersionBox.Text.Trim(),
                ResourceLoaderBox.Text.Trim(),
                index: UiHelpers.ComboText(ResourceSortBox, "relevance"));
            var hitObjects = hits.OfType<JsonObject>().ToList();
            SetResourceResults(hitObjects);
            var relaxed = hitObjects.Any(hit => hit["relaxed_search"]?.GetValue<bool>() == true);
            ResourceOutputBox.Text = $"找到 {hitObjects.Count} 个结果{(relaxed ? "（已放宽搜索）" : "")}。";
            ResourceStatusText.Text = hitObjects.Count == 0 ? "没有找到匹配资源。" : $"已显示 {hitObjects.Count} 个结果，正在后台验证兼容文件...";
            if (hitObjects.Count > 0)
            {
                var generation = _resourceSearchGeneration;
                _ = VerifyResourceCompatibilityAsync(generation, UiHelpers.ComboText(ResourceTypeBox, "mod"), ResourceGameVersionBox.Text.Trim(), ResourceLoaderBox.Text.Trim());
            }
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = "资源搜索失败：" + exc.Message;
            ResourceStatusText.Text = "资源搜索失败。";
        }
        finally
        {
            SetResourceBusy(false);
        }
    }

    private void InstallSelectedResource(object sender, RoutedEventArgs e)
    {
        if (ResourceResultsList.SelectedItem is not string label || !_resourceHitsByLabel.TryGetValue(label, out var hit))
        {
            ShowResourceMessage("请先选择一个资源。");
            return;
        }

        var resourceType = UiHelpers.ComboText(ResourceTypeBox, "mod");
        if (resourceType == "mod" && !VersionSupportsModManagement(CurrentResourceVersion()))
        {
            ShowResourceMessage("当前目标版本不支持 Mod 管理，请切换到 Fabric、Forge、NeoForge 或 OptiFine 版本。");
            return;
        }

        if ((hit["source"]?.GetValue<string>() ?? "modrinth") != "modrinth")
        {
            ShowResourceMessage("当前只支持从 Modrinth 一键安装；本地来源请在本地资源列表管理。");
            return;
        }

        if (hit["compatible"]?.GetValue<bool>() == false)
        {
            ShowResourceMessage("这个资源没有适配当前安装目标的可下载文件，请换一个版本或资源。");
            return;
        }

        var projectId = hit["project_id"]?.GetValue<string>() ?? hit["slug"]?.GetValue<string>() ?? "";
        if (projectId.Length == 0)
        {
            ShowResourceMessage("选中资源缺少 project_id。");
            return;
        }

        var title = hit["title"]?.GetValue<string>() ?? projectId;
        var targetDir = CurrentResourceTargetDir();
        var gameVersion = ResourceGameVersionBox.Text.Trim();
        var loader = ResourceLoaderBox.Text.Trim();
        var includeDependencies = InstallResourceDependenciesCheckBox.IsChecked == true;
        TargetDirBox.Text = targetDir;
        SetResourceBusy(true, "已加入全局队列，等待安装资源...", indeterminate: false);
        DownloadTaskManager.Current.Enqueue("resource", $"安装资源 {title}", async (record, token) =>
        {
            try
            {
                var progress = new Progress<ResourceDownloadProgress>(snapshot =>
                {
                    var detail = $"{snapshot.Phase} {snapshot.Progress:P0} {snapshot.CurrentFile}";
                    ResourceOutputBox.Text = detail;
                    ResourceProgressBar.Value = Math.Clamp(snapshot.Progress * 100, 0, 100);
                    DownloadTaskManager.Current.Update(record, item =>
                    {
                        item.Progress = (int)Math.Round(snapshot.Progress * 100);
                        item.Detail = detail;
                    });
                });
                var result = await _resourceMarket.InstallModrinthResourceAsync(
                    projectId, resourceType, gameVersion, loader, targetDir, includeDependencies,
                    status: message =>
                    {
                        ResourceOutputBox.Text = message;
                        DownloadTaskManager.Current.Update(record, item => item.Detail = message);
                    },
                    progress: progress,
                    cancellationToken: token);
                ResourceOutputBox.Text = $"已安装：{result.Filename}\n版本：{result.Version}\n路径：{result.Path}\n依赖：{string.Join(", ", result.DependenciesInstalled)}";
                RefreshLocalResources(sender, e);
                return $"已安装 {result.Filename}";
            }
            catch (OperationCanceledException)
            {
                ShowResourceMessage("资源安装已取消。");
                throw;
            }
            catch (Exception exc)
            {
                ShowResourceMessage("资源安装失败：" + exc.Message);
                throw;
            }
            finally
            {
                SetResourceBusy(false);
            }
        });
    }

    private void ShowSelectedResourceDetail(object sender, RoutedEventArgs e)
    {
        if (ResourceResultsList.SelectedItem is not string label || !_resourceHitsByLabel.TryGetValue(label, out var hit))
        {
            ShowResourceMessage("请先选择一个搜索结果。");
            return;
        }

        RevealResourceDetail();
        _resourceDetailQueue.Enqueue(hit);
        _ = DrainResourceDetailQueueAsync();
    }

    private async void ResolveDependencies(object sender, RoutedEventArgs e)
    {
        SetResourceBusy(true, "正在解析 Mod 前置依赖...");
        try
        {
            var plan = await _resourceMarket.ResolveModrinthDependencyPlanAsync(TargetDirBox.Text.Trim());
            var builder = new StringBuilder();
            builder.AppendLine("已安装项目：" + string.Join(", ", plan.InstalledProjectIds));
            builder.AppendLine("缺失依赖：");
            foreach (var missing in plan.MissingDependencies)
            {
                builder.AppendLine($"- {missing.ProjectId} <- {string.Join(", ", missing.RequiredBy)}");
            }

            ShowResourceMessage(builder.ToString());
        }
        catch (Exception exc)
        {
            ShowResourceMessage("依赖解析失败：" + exc.Message);
        }
        finally
        {
            SetResourceBusy(false);
        }
    }

    private void InstallMissingDependencies(object sender, RoutedEventArgs e)
    {
        var gameVersion = ResourceGameVersionBox.Text.Trim();
        var loader = ResourceLoaderBox.Text.Trim();
        var targetDir = TargetDirBox.Text.Trim();
        SetResourceBusy(true, "已加入全局队列，等待安装缺失前置...", indeterminate: false);
        DownloadTaskManager.Current.Enqueue("resource_dependencies", "安装缺失 Mod 前置", async (record, token) =>
        {
            try
            {
                var progress = new Progress<ResourceDownloadProgress>(snapshot =>
                {
                    var detail = $"{snapshot.Phase} {snapshot.Progress:P0} {snapshot.CurrentFile}";
                    ResourceOutputBox.Text = detail;
                    ResourceProgressBar.Value = Math.Clamp(snapshot.Progress * 100, 0, 100);
                    DownloadTaskManager.Current.Update(record, item =>
                    {
                        item.Progress = (int)Math.Round(snapshot.Progress * 100);
                        item.Detail = detail;
                    });
                });
                var result = await _resourceMarket.InstallMissingModrinthDependenciesAsync(
                    gameVersion, loader, targetDir,
                    message =>
                    {
                        ResourceOutputBox.Text = message;
                        DownloadTaskManager.Current.Update(record, item => item.Detail = message);
                    },
                    progress,
                    token);
                ShowResourceMessage(result.DependenciesInstalled.Count == 0
                    ? "没有缺失前置。"
                    : "已安装缺失前置：\n" + string.Join(Environment.NewLine, result.DependenciesInstalled));
                RefreshLocalResources(sender, e);
                return result.DependenciesInstalled.Count == 0 ? "没有缺失前置" : $"已安装 {result.DependenciesInstalled.Count} 个前置";
            }
            catch (OperationCanceledException)
            {
                ShowResourceMessage("前置安装已取消。");
                throw;
            }
            catch (Exception exc)
            {
                ShowResourceMessage("前置安装失败：" + exc.Message);
                throw;
            }
            finally
            {
                SetResourceBusy(false);
            }
        });
    }

    private void RefreshLocalResources(object sender, RoutedEventArgs e)
    {
        try
        {
            var targetDir = CurrentResourceTargetDir();
            TargetDirBox.Text = targetDir;
            var items = ResourceMarketService.ListLocalResources(ResourceQueryBox.Text.Trim(), UiHelpers.ComboText(ResourceTypeBox, "mod"), targetDir);
            _localResourcesByLabel.Clear();
            var labels = new List<string>();
            foreach (var item in items)
            {
                var label = $"{item.Title}  [{item.Status}]  {AppFormatter.FormatBytes(item.SizeBytes)}{Environment.NewLine}{item.RelativePath}";
                labels.Add(label);
                _localResourcesByLabel[label] = item;
            }

            LocalResourcesList.ItemsSource = labels;
            ResourceOutputBox.Text = $"找到 {items.Count} 个本地资源。";
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = "刷新本地资源失败：" + exc.Message;
            RevealResourceLog();
        }
    }

    private async Task VerifyResourceCompatibilityAsync(int generation, string resourceType, string gameVersion, string loader)
    {
        var checkedCount = 0;
        var compatibleCount = 0;
        for (var index = 0; index < _resourceSearchHits.Count; index++)
        {
            if (generation != _resourceSearchGeneration)
            {
                return;
            }

            var hit = _resourceSearchHits[index];
            var projectId = hit["project_id"]?.GetValue<string>() ?? hit["slug"]?.GetValue<string>() ?? "";
            if ((hit["source"]?.GetValue<string>() ?? "modrinth") != "modrinth" || projectId.Length == 0)
            {
                continue;
            }

            var result = await _resourceMarket.CheckModrinthCompatibilityAsync(projectId, resourceType, gameVersion, loader);
            if (generation != _resourceSearchGeneration)
            {
                return;
            }

            checkedCount++;
            hit["compatibility_checking"] = false;
            hit["compatibility_unverified"] = false;
            hit["compatible"] = result.Compatible;
            hit["compatible_version"] = result.VersionNumber;
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                hit["compatibility_error"] = result.Error;
            }

            if (result.Compatible)
            {
                compatibleCount++;
            }

            RefreshResourceResultLabels();
            ResourceStatusText.Text = $"兼容性验证中：{compatibleCount}/{checkedCount} 个可安装";
        }

        if (generation == _resourceSearchGeneration)
        {
            ResourceStatusText.Text = $"兼容性验证完成：{compatibleCount}/{checkedCount} 个可安装";
        }
    }

    private async Task DrainResourceDetailQueueAsync()
    {
        if (_resourceDetailRunning)
        {
            return;
        }

        _resourceDetailRunning = true;
        try
        {
            while (_resourceDetailQueue.Count > 0)
            {
                var hit = _resourceDetailQueue.Dequeue();
                var key = ResourceDetailKey(hit);
                ResourceDetailBox.Text = "正在加载详情...";
                ResourceScreenshotsList.ItemsSource = null;
                try
                {
                    var source = hit["source"]?.GetValue<string>() ?? "modrinth";
                    if (source == "local")
                    {
                        var path = hit["path"]?.GetValue<string>() ?? hit["project_id"]?.GetValue<string>() ?? "";
                        var detail = ResourceMarketService.GetLocalResourceDetail(path);
                        if (key == ResourceDetailKey(SelectedResourceHit()))
                        {
                            ResourceDetailBox.Text = FormatLocalResourceDetail(detail);
                            ResourceScreenshotsList.ItemsSource = new[] { "本地资源没有市场截图。" };
                        }

                        continue;
                    }

                    var projectId = hit["project_id"]?.GetValue<string>() ?? hit["slug"]?.GetValue<string>() ?? "";
                    var detailPayload = await _resourceMarket.GetModrinthResourceDetailAsync(
                        projectId,
                        UiHelpers.ComboText(ResourceTypeBox, "mod"),
                        ResourceGameVersionBox.Text.Trim(),
                        ResourceLoaderBox.Text.Trim());
                    if (key == ResourceDetailKey(SelectedResourceHit()))
                    {
                        ResourceDetailBox.Text = FormatRemoteResourceDetail(detailPayload);
                        ResourceScreenshotsList.ItemsSource = detailPayload.Screenshots.Count == 0
                            ? new[] { "没有截图。" }
                            : detailPayload.Screenshots.Select((item, index) =>
                                $"{index + 1}. {(string.IsNullOrWhiteSpace(item.LocalPath) ? item.Url : item.LocalPath)}\n{item.Url}").ToList();
                    }
                }
                catch (Exception exc)
                {
                    if (key == ResourceDetailKey(SelectedResourceHit()))
                    {
                        ResourceDetailBox.Text = "详情加载失败：" + exc.Message;
                    }
                }
            }
        }
        finally
        {
            _resourceDetailRunning = false;
        }
    }

    private void ShowLocalResourceDetail(object sender, RoutedEventArgs e)
    {
        try
        {
            var item = SelectedLocalResource();
            if (item is null)
            {
                return;
            }

            var detail = ResourceMarketService.GetLocalResourceDetail(item.Path);
            var builder = new StringBuilder();
            builder.AppendLine(detail.Title);
            builder.AppendLine("状态：" + detail.Status);
            builder.AppendLine("大小：" + AppFormatter.FormatBytes(detail.SizeBytes));
            builder.AppendLine("修改时间：" + detail.ModifiedAt);
            builder.AppendLine("SHA1：" + detail.Sha1);
            builder.AppendLine("路径：" + detail.Path);
            if (detail.Hints.Count > 0)
            {
                builder.AppendLine("提示：");
                foreach (var hint in detail.Hints)
                {
                    builder.AppendLine("- " + hint);
                }
            }

            ShowResourceMessage(builder.ToString());
        }
        catch (Exception exc)
        {
            ShowResourceMessage("读取本地资源失败：" + exc.Message);
        }
    }

    private void EnableLocalResource(object sender, RoutedEventArgs e) => SetSelectedLocalResourceEnabled(true);

    private void DisableLocalResource(object sender, RoutedEventArgs e) => SetSelectedLocalResourceEnabled(false);

    private async void DeleteLocalResource(object sender, RoutedEventArgs e)
    {
        try
        {
            var item = SelectedLocalResource();
            if (item is null)
            {
                return;
            }

            var dialog = AppDialog.Create(this, "删除本地资源", $"删除 {item.Title}？", "删除", "取消");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            ResourceMarketService.DeleteLocalResource(item.Path);
            ResourceOutputBox.Text = "已删除：" + item.Path;
            RefreshLocalResources(sender, e);
        }
        catch (Exception exc)
        {
            ShowResourceMessage("删除本地资源失败：" + exc.Message);
        }
    }

    private void SetSelectedLocalResourceEnabled(bool enabled)
    {
        try
        {
            var item = SelectedLocalResource();
            if (item is null)
            {
                return;
            }

            var targetPath = ResourceMarketService.SetLocalResourceEnabled(item.Path, enabled);
            ResourceOutputBox.Text = (enabled ? "已启用：" : "已禁用：") + targetPath;
            RefreshLocalResources(this, new RoutedEventArgs());
        }
        catch (Exception exc)
        {
            ShowResourceMessage("更新本地资源状态失败：" + exc.Message);
        }
    }

    private LocalResourceInfo? SelectedLocalResource()
    {
        if (LocalResourcesList.SelectedItem is string label && _localResourcesByLabel.TryGetValue(label, out var item))
        {
            return item;
        }

        ShowResourceMessage("请先选择一个本地资源。");
        return null;
    }

    private JsonObject? SelectedResourceHit()
        => ResourceResultsList.SelectedItem is string label && _resourceHitsByLabel.TryGetValue(label, out var hit) ? hit : null;

    private void SetResourceResults(IReadOnlyList<JsonObject> hits)
    {
        _resourceSearchHits = hits.ToList();
        RefreshResourceResultLabels();
    }

    private void RefreshResourceResultLabels()
    {
        var selectedKey = ResourceDetailKey(SelectedResourceHit());
        _resourceHitsByLabel.Clear();
        var labels = new List<string>();
        foreach (var hit in _resourceSearchHits)
        {
            var label = AppFormatter.FormatResourceHitLabel(ToDictionary(hit));
            var uniqueLabel = label;
            var duplicateIndex = 2;
            while (_resourceHitsByLabel.ContainsKey(uniqueLabel))
            {
                uniqueLabel = $"{label}\n#{duplicateIndex++}";
            }

            labels.Add(uniqueLabel);
            _resourceHitsByLabel[uniqueLabel] = hit;
        }

        ResourceResultsList.ItemsSource = labels;
        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            ResourceResultsList.SelectedItem = labels.FirstOrDefault(label => ResourceDetailKey(_resourceHitsByLabel[label]) == selectedKey);
        }
    }

    private static string ResourceDetailKey(JsonObject? hit)
    {
        if (hit is null)
        {
            return "";
        }

        var source = hit["source"]?.GetValue<string>() ?? "modrinth";
        var identity = hit["project_id"]?.GetValue<string>()
                       ?? hit["slug"]?.GetValue<string>()
                       ?? hit["path"]?.GetValue<string>()
                       ?? hit["title"]?.GetValue<string>()
                       ?? "";
        return $"{source}:{identity}";
    }

    private static JsonObject LocalResourceToHit(LocalResourceInfo item)
        => new()
        {
            ["source"] = "local",
            ["project_id"] = item.Path,
            ["path"] = item.Path,
            ["title"] = item.Title,
            ["slug"] = item.Title,
            ["description"] = item.RelativePath,
            ["downloads"] = item.SizeBytes,
            ["follows"] = 0,
        };

    private static string FormatRemoteResourceDetail(RemoteResourceDetail detail)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{detail.Title} ({detail.Source})");
        builder.AppendLine($"下载：{detail.Downloads} | 关注：{detail.Followers}");
        if (!string.IsNullOrWhiteSpace(detail.ProjectUrl))
        {
            builder.AppendLine("链接：" + detail.ProjectUrl);
        }

        if (!string.IsNullOrWhiteSpace(detail.Description))
        {
            builder.AppendLine();
            builder.AppendLine(detail.Description);
        }

        if (detail.Dependencies.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("依赖：");
            foreach (var dependency in detail.Dependencies.Take(12))
            {
                builder.AppendLine($"- {dependency.Type}: {dependency.ProjectId} {dependency.VersionId}".TrimEnd());
            }
        }

        if (detail.Versions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("可用版本/文件：");
            foreach (var version in detail.Versions.Take(8))
            {
                builder.AppendLine($"- {version.Name} {version.ReleaseType} | {version.GameVersions} | {version.Loaders}".TrimEnd());
            }
        }

        if (detail.Screenshots.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("截图已缓存/列出在上方截图列表。");
        }

        return builder.ToString();
    }

    private static string FormatLocalResourceDetail(LocalResourceDetail detail)
    {
        var builder = new StringBuilder();
        builder.AppendLine(detail.Title);
        builder.AppendLine("状态：" + detail.Status);
        builder.AppendLine("大小：" + AppFormatter.FormatBytes(detail.SizeBytes));
        builder.AppendLine("修改时间：" + detail.ModifiedAt);
        builder.AppendLine("SHA1：" + detail.Sha1);
        builder.AppendLine("路径：" + detail.Path);
        if (detail.Hints.Count > 0)
        {
            builder.AppendLine("提示：");
            foreach (var hint in detail.Hints)
            {
                builder.AppendLine("- " + hint);
            }
        }

        return builder.ToString();
    }

    private string CurrentResourceTargetDir()
    {
        var configured = TargetDirBox.Text.Trim();
        if (configured.Length > 0)
        {
            return configured;
        }

        var gameDir = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        var versionId = _launcher.GetLocalVersions(gameDir).FirstOrDefault() ?? "";
        return ResourceMarketService.ResourceDirectoryForType(gameDir, _versionSettings, versionId, UiHelpers.ComboText(ResourceTypeBox, "mod"), _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
    }

    private void ResourceSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || ResourceSourceBox is null || ResourceSortBox is null || InstallResourceDependenciesCheckBox is null || InstallResourceButton is null || ResourceStatusText is null)
        {
            return;
        }

        var source = UiHelpers.ComboText(ResourceSourceBox, "modrinth");
        var isModrinth = source == "modrinth";
        ResourceSortBox.IsEnabled = isModrinth;
        InstallResourceDependenciesCheckBox.IsEnabled = isModrinth;
        InstallResourceButton.IsEnabled = isModrinth && !_resourceBusy;
        ResourceStatusText.Text = source == "local" ? "本地来源会扫描当前目标目录。" : "等待搜索";
    }

    private void SetResourceBusy(bool busy, string status = "", bool indeterminate = true)
    {
        _resourceBusy = busy;
        SearchResourcesButton.IsEnabled = !busy;
        ResourceDetailButton.IsEnabled = !busy;
        ResolveDependenciesButton.IsEnabled = !busy;
        InstallMissingDependenciesButton.IsEnabled = !busy;
        InstallResourceButton.IsEnabled = !busy && UiHelpers.ComboText(ResourceSourceBox, "modrinth") == "modrinth";
        ResourceProgressBar.IsIndeterminate = busy && indeterminate;
        if (!busy)
        {
            ResourceProgressBar.IsIndeterminate = false;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            ResourceStatusText.Text = status;
        }
    }

    private void ResourceVersionChanged(object sender, SelectionChangedEventArgs e) => ApplyResourceVersionSelection();

    private void ResourceTargetFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        RefreshResourceTargetSummary();
    }

    private void ResourceTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || TargetDirBox is null)
        {
            return;
        }

        RefreshResourceTargetVersions();
    }
    private void RefreshResourceTargetVersions()
    {
        if (ResourceVersionBox is null || ResourceTypeBox is null)
        {
            return;
        }

        var selected = CurrentResourceVersion();
        var resourceType = UiHelpers.ComboText(ResourceTypeBox, "mod");
        var versions = resourceType == "mod"
            ? _allLocalVersions.Where(VersionSupportsModManagement).ToList()
            : _allLocalVersions.ToList();
        ResourceVersionBox.ItemsSource = versions.Count == 0 ? new[] { "未发现兼容的本地版本" } : versions;
        ResourceVersionBox.SelectedItem = versions.Contains(selected) ? selected : null;
        if (ResourceVersionBox.SelectedItem is null)
        {
            ResourceVersionBox.SelectedIndex = 0;
        }

        ApplyResourceVersionSelection();
    }

    private void ApplyResourceVersionSelection()
    {
        if (ResourceVersionBox is null || ResourceGameVersionBox is null || ResourceLoaderBox is null || TargetDirBox is null)
        {
            return;
        }

        var versionId = CurrentResourceVersion();
        var gameDir = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        ResourceGameVersionBox.Text = versionId.Length > 0 ? _versionSettingsService.ResolveBaseMinecraftVersion(gameDir, versionId) : "";
        ResourceLoaderBox.Text = versionId.Length > 0 ? ModrinthLoaderForVersion(gameDir, versionId) : "";
        TargetDirBox.Text = DefaultResourceTargetDir(versionId);
        RefreshResourceTargetSummary();
    }

    /// <summary>折叠筛选后，右栏这张卡片就是"我要装到哪"的唯一答案。</summary>
    private void RefreshResourceTargetSummary()
    {
        if (ResourceTargetVersionText is null || ResourceTargetDirText is null || ResourceTargetHintText is null)
        {
            return;
        }

        var versionId = CurrentResourceVersion();
        var resourceType = UiHelpers.ComboText(ResourceTypeBox, "mod");
        ResourceTargetVersionText.Text = versionId.Length == 0
            ? $"未选择版本 · {resourceType}"
            : $"{versionId} · {resourceType}";

        var targetDir = TargetDirBox.Text.Trim();
        ResourceTargetDirText.Text = targetDir.Length == 0 ? "—" : targetDir;
        ToolTipService.SetToolTip(ResourceTargetDirText, targetDir);

        ResourceTargetHintText.Text = versionId.Length == 0
            ? "还没有可用的本地版本。先到“下载”装一个，或展开左侧“筛选条件”选择目标版本。"
            : resourceType == "mod" && !VersionSupportsModManagement(versionId)
                ? "当前版本不支持 Mod 管理，请在左侧“筛选条件”里换 Fabric、Forge、NeoForge 或 OptiFine 版本。"
                : string.IsNullOrWhiteSpace(ResourceGameVersionBox.Text) && string.IsNullOrWhiteSpace(ResourceLoaderBox.Text)
                    ? "游戏版本与加载器都不限制，会返回更多结果。"
                    : $"游戏版本 {Fallback(ResourceGameVersionBox.Text, "不限")} · 加载器 {Fallback(ResourceLoaderBox.Text, "不限")}";

        static string Fallback(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private string CurrentResourceVersion()
    {
        var value = ResourceVersionBox.SelectedItem?.ToString() ?? "";
        return value.StartsWith("未发现", StringComparison.Ordinal) ? "" : value;
    }

    private bool VersionSupportsModManagement(string versionId)
        => versionId.Length > 0
           && _versionSettingsService.DetectVersionType(_settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory), versionId)
               is "Fabric" or "Forge" or "NeoForge" or "OptiFine";

    private string DefaultResourceTargetDir(string versionId)
    {
        var gameDir = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        return versionId.Length == 0
            ? ResourceMarketService.ResourceDirectoryForType(gameDir, _versionSettings, "", UiHelpers.ComboText(ResourceTypeBox, "mod"), _settings.Config.GetBool("GAME", "enable_resource_isolation", true))
            : ResourceMarketService.ResourceDirectoryForType(gameDir, _versionSettings, versionId, UiHelpers.ComboText(ResourceTypeBox, "mod"), _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
    }

    private string ModrinthLoaderForVersion(string gameDir, string versionId)
        => _versionSettingsService.DetectVersionType(gameDir, versionId) switch
        {
            "Fabric" => "fabric",
            "Forge" => "forge",
            "NeoForge" => "neoforge",
            _ => "",
        };

    private static Dictionary<string, object?> ToDictionary(JsonObject obj)
        => obj.ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : JsonNodeValue(pair.Value));

    private static object? JsonNodeValue(JsonNode node)
        => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<int>(out var integer) => integer,
            JsonValue value when value.TryGetValue<long>(out var number) => number,
            JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean,
            _ => node.ToJsonString(),
        };

        internal void RunSmokeChecks()
    {
        if (ResourceSectionBar.Items.Count != 2)
        {
            throw new InvalidOperationException("资源页缺少市场与本地资源分段。");
        }

        // 信息减法：搜索框常驻、筛选与详情/日志默认折叠。
        if (ResourceQueryBox.Visibility != Visibility.Visible
            || SearchResourcesButton.Visibility != Visibility.Visible
            || ResourceResultsList.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("资源页主搜索区没有常驻显示。");
        }

        if (ResourceFilterExpander.IsExpanded
            || ResourceDetailExpander.IsExpanded
            || ResourceOutputExpander.IsExpanded)
        {
            throw new InvalidOperationException("资源页的筛选、详情与日志应默认折叠。");
        }

        // 宽屏左右分栏，窄屏单列堆叠。
        var wide = ViewportWidth >= 900;
        ApplyResponsiveLayout(ViewportWidth);
        if (wide != _wideLayout
            || Grid.GetColumn(ResourceSidePanel) != (wide ? 1 : 0)
            || Grid.GetRow(ResourceSidePanel) != (wide ? 2 : 3)
            || Grid.GetColumnSpan(ResourceSidePanel) != (wide ? 1 : 2))
        {
            throw new InvalidOperationException("资源页宽窄屏布局不正确。");
        }

        var resourceTypes = ResourceTypeBox.Items.OfType<ComboBoxItem>()
            .Select(item => item.Content?.ToString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!resourceTypes.SetEquals(["mod", "resourcepack", "shader", "datapack"]))
        {
            throw new InvalidOperationException("资源类型没有完整迁移 Mod、资源包、光影和数据包。");
        }

        var sources = ResourceSourceBox.Items.OfType<ComboBoxItem>()
            .Select(item => item.Content?.ToString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!sources.SetEquals(["modrinth", "local"]))
        {
            throw new InvalidOperationException("资源来源应只保留 Modrinth 和本地。");
        }

        ResourceSectionBar.SelectedItem = ResourceSectionBar.Items[1];
        if (LocalResourcesPanel.Visibility != Visibility.Visible
            || ResourceSearchPanel.Visibility != Visibility.Collapsed
            || ResourceResultsPanel.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("本地资源分段状态不正确。");
        }

        ResourceSectionBar.SelectedItem = ResourceSectionBar.Items[0];
        if (LocalResourcesPanel.Visibility != Visibility.Collapsed
            || ResourceSearchPanel.Visibility != Visibility.Visible
            || ResourceResultsPanel.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("资源市场分段状态不正确。");
        }
    }

    internal void SelectResourceSectionForSmoke(string section)
    {
        ResourceSectionBar.SelectedItem = ResourceSectionBar.Items.OfType<SelectorBarItem>()
            .First(item => string.Equals(item.Tag?.ToString(), section, StringComparison.Ordinal));
    }

    internal void BringResourceSectionIntoViewForSmoke(string section)
    {
        var target = section == "local" ? LocalResourcesPanel : ResourceSearchPanel;
        target.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = false,
            VerticalAlignmentRatio = 0,
        });
    }
}
