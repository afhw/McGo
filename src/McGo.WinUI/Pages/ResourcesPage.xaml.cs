using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Formatting;
using McGo.Core.Minecraft;
using McGo.Core.Resources;

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
    private List<JsonObject> _resourceSearchHits = [];
    private int _resourceSearchGeneration;
    private bool _resourceDetailRunning;
    private bool _initialized;

    public ResourcesPage()
    {
        InitializeComponent();
        _versionSettingsService = new VersionSettingsService(_launcher);
        LoadState();
        _initialized = true;
    }

    private void LoadState()
    {
        _settings.LoadConfig();
        _versionSettings = _versionSettingsService.LoadVersionSettings();
        var gameDir = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        var versionId = _launcher.GetLocalVersions(gameDir).FirstOrDefault() ?? "";
        ResourceGameVersionBox.Text = versionId.Length > 0 ? _versionSettingsService.ResolveBaseMinecraftVersion(gameDir, versionId) : "";
        ResourceLoaderBox.Text = "";
        TargetDirBox.Text = CurrentResourceTargetDir();
    }

    private async void SearchResources(object sender, RoutedEventArgs e)
    {
        try
        {
            var source = ComboText(ResourceSourceBox, "modrinth");
            ResourceOutputBox.Text = source == "local" ? "正在扫描本地资源..." : "正在搜索资源...";
            ResourceStatusText.Text = ResourceOutputBox.Text;
            ResourceDetailBox.Text = "";
            ResourceScreenshotsList.ItemsSource = null;
            _resourceSearchGeneration++;

            if (source == "local")
            {
                var localHits = ResourceMarketService.ListLocalResources(ResourceQueryBox.Text.Trim(), ComboText(ResourceTypeBox, "mod"), CurrentResourceTargetDir())
                    .Select(LocalResourceToHit)
                    .ToList();
                SetResourceResults(localHits);
                ResourceOutputBox.Text = $"找到 {localHits.Count} 个本地资源。";
                ResourceStatusText.Text = ResourceOutputBox.Text;
                return;
            }

            if (string.IsNullOrWhiteSpace(ResourceQueryBox.Text))
            {
                ResourceOutputBox.Text = "请输入要搜索的资源名称。";
                ResourceStatusText.Text = ResourceOutputBox.Text;
                return;
            }

            var hits = await _resourceMarket.SearchModrinthResourcesAsync(
                ResourceQueryBox.Text.Trim(),
                ComboText(ResourceTypeBox, "mod"),
                ResourceGameVersionBox.Text.Trim(),
                ResourceLoaderBox.Text.Trim(),
                index: ComboText(ResourceSortBox, "relevance"));
            var hitObjects = hits.OfType<JsonObject>().ToList();
            SetResourceResults(hitObjects);
            var relaxed = hitObjects.Any(hit => hit["relaxed_search"]?.GetValue<bool>() == true);
            ResourceOutputBox.Text = $"找到 {hitObjects.Count} 个结果{(relaxed ? "（已放宽搜索）" : "")}。";
            ResourceStatusText.Text = hitObjects.Count == 0 ? "没有找到匹配资源。" : $"已显示 {hitObjects.Count} 个结果，正在后台验证兼容文件...";
            if (hitObjects.Count > 0)
            {
                var generation = _resourceSearchGeneration;
                _ = VerifyResourceCompatibilityAsync(generation, ComboText(ResourceTypeBox, "mod"), ResourceGameVersionBox.Text.Trim(), ResourceLoaderBox.Text.Trim());
            }
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = "资源搜索失败：" + exc.Message;
            ResourceStatusText.Text = "资源搜索失败。";
        }
    }

    private async void InstallSelectedResource(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ResourceResultsList.SelectedItem is not string label || !_resourceHitsByLabel.TryGetValue(label, out var hit))
            {
                ResourceOutputBox.Text = "请先选择一个资源。";
                return;
            }

            if ((hit["source"]?.GetValue<string>() ?? "modrinth") != "modrinth")
            {
                ResourceOutputBox.Text = "当前只支持从 Modrinth 一键安装；本地来源请在本地资源列表管理。";
                return;
            }

            if (hit["compatible"]?.GetValue<bool>() == false)
            {
                ResourceOutputBox.Text = "这个资源没有适配当前安装目标的可下载文件，请换一个版本或资源。";
                return;
            }

            var projectId = hit["project_id"]?.GetValue<string>() ?? hit["slug"]?.GetValue<string>() ?? "";
            if (projectId.Length == 0)
            {
                ResourceOutputBox.Text = "选中资源缺少 project_id。";
                return;
            }

            var targetDir = CurrentResourceTargetDir();
            TargetDirBox.Text = targetDir;
            var progress = new Progress<ResourceDownloadProgress>(snapshot =>
            {
                ResourceOutputBox.Text = $"{snapshot.Phase} {snapshot.Progress:P0} {snapshot.CurrentFile}";
            });
            var result = await _resourceMarket.InstallModrinthResourceAsync(
                projectId,
                ComboText(ResourceTypeBox, "mod"),
                ResourceGameVersionBox.Text.Trim(),
                ResourceLoaderBox.Text.Trim(),
                targetDir,
                InstallResourceDependenciesCheckBox.IsChecked == true,
                status: message => ResourceOutputBox.Text = message,
                progress: progress);
            ResourceOutputBox.Text = $"已安装：{result.Filename}\n版本：{result.Version}\n路径：{result.Path}\n依赖：{string.Join(", ", result.DependenciesInstalled)}";
            RefreshLocalResources(sender, e);
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = "资源安装失败：" + exc.Message;
        }
    }

    private void ShowSelectedResourceDetail(object sender, RoutedEventArgs e)
    {
        if (ResourceResultsList.SelectedItem is not string label || !_resourceHitsByLabel.TryGetValue(label, out var hit))
        {
            ResourceOutputBox.Text = "请先选择一个搜索结果。";
            return;
        }

        _resourceDetailQueue.Enqueue(hit);
        _ = DrainResourceDetailQueueAsync();
    }

    private async void ResolveDependencies(object sender, RoutedEventArgs e)
    {
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

            ResourceOutputBox.Text = builder.ToString();
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = "依赖解析失败：" + exc.Message;
        }
    }

    private async void InstallMissingDependencies(object sender, RoutedEventArgs e)
    {
        try
        {
            var progress = new Progress<ResourceDownloadProgress>(snapshot =>
            {
                ResourceOutputBox.Text = $"{snapshot.Phase} {snapshot.Progress:P0} {snapshot.CurrentFile}";
            });
            var result = await _resourceMarket.InstallMissingModrinthDependenciesAsync(
                ResourceGameVersionBox.Text.Trim(),
                ResourceLoaderBox.Text.Trim(),
                TargetDirBox.Text.Trim(),
                message => ResourceOutputBox.Text = message,
                progress);
            ResourceOutputBox.Text = result.DependenciesInstalled.Count == 0
                ? "没有缺失前置。"
                : "已安装缺失前置：\n" + string.Join(Environment.NewLine, result.DependenciesInstalled);
            RefreshLocalResources(sender, e);
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = "前置安装失败：" + exc.Message;
        }
    }

    private void RefreshLocalResources(object sender, RoutedEventArgs e)
    {
        try
        {
            var targetDir = CurrentResourceTargetDir();
            TargetDirBox.Text = targetDir;
            var items = ResourceMarketService.ListLocalResources(ResourceQueryBox.Text.Trim(), ComboText(ResourceTypeBox, "mod"), targetDir);
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
                        ComboText(ResourceTypeBox, "mod"),
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

            ResourceOutputBox.Text = builder.ToString();
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = "读取本地资源失败：" + exc.Message;
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

            var dialog = new ContentDialog
            {
                Title = "删除本地资源",
                Content = $"删除 {item.Title}？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
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
            ResourceOutputBox.Text = "删除本地资源失败：" + exc.Message;
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
            ResourceOutputBox.Text = "更新本地资源状态失败：" + exc.Message;
        }
    }

    private LocalResourceInfo? SelectedLocalResource()
    {
        if (LocalResourcesList.SelectedItem is string label && _localResourcesByLabel.TryGetValue(label, out var item))
        {
            return item;
        }

        ResourceOutputBox.Text = "请先选择一个本地资源。";
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
        return ResourceMarketService.ResourceDirectoryForType(gameDir, _versionSettings, versionId, ComboText(ResourceTypeBox, "mod"), _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
    }

    private void ResourceSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || ResourceSourceBox is null || ResourceSortBox is null || InstallResourceDependenciesCheckBox is null || InstallResourceButton is null || ResourceStatusText is null)
        {
            return;
        }

        var source = ComboText(ResourceSourceBox, "modrinth");
        var isModrinth = source == "modrinth";
        ResourceSortBox.IsEnabled = isModrinth;
        InstallResourceDependenciesCheckBox.IsEnabled = isModrinth;
        InstallResourceButton.IsEnabled = isModrinth;
        ResourceStatusText.Text = source == "local" ? "本地来源会扫描当前目标目录。" : "等待搜索";
    }

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

    private static string ComboText(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? fallback;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }
}
