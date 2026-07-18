using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using McGo.Core.App;
using McGo.Core.Authentication;
using McGo.Core.Capsules;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Formatting;
using McGo.Core.Installation;
using McGo.Core.Java;
using McGo.Core.Minecraft;
using McGo.Core.Modpacks;
using McGo.Core.Network;
using McGo.Core.P2P;
using McGo.Core.Personalization;
using McGo.Core.Resources;
using McGo.Core.Updates;
using Microsoft.Win32;

namespace McGo.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = new();
    private readonly LauncherService _launcher = new();
    private readonly VersionSettingsService _versionSettingsService;
    private readonly InstallServices _installServices = new();
    private readonly DownloaderService _downloader = new();
    private readonly LaunchPreflightService _launchPreflight = new();
    private readonly ResourceMarketService _resourceMarket = new();
    private readonly ModpackService _modpacks;
    private readonly CapsuleService _capsules;
    private readonly UpdaterService _updater = new();
    private readonly HomeContentService _homeContent = new();
    private readonly P2PTunnel _p2pTunnel = new();
    private readonly MediaPlayer _musicPlayer = new();
    private readonly MicrosoftAuthenticator _microsoftAuthenticator = new(AppConstants.ClientId, AppConstants.RedirectUri);
    private readonly OAuthCallbackServer _oauthCallbackServer = new();
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private JsonObject _versionSettings = [];
    private List<JsonObject> _accounts = [];
    private readonly Dictionary<string, JsonObject> _resourceHitsByLabel = [];
    private readonly Dictionary<string, LocalResourceInfo> _localResourcesByLabel = [];
    private CancellationTokenSource? _p2pCts;
    private JsonObject? _lastUpdateManifest;
    private string? _lastUpdateManifestUrl;
    private string? _lastUpdatePath;

    public MainWindow()
    {
        InitializeComponent();
        _versionSettingsService = new VersionSettingsService(_launcher);
        _modpacks = new ModpackService(_launcher);
        _capsules = new CapsuleService(_launcher, _versionSettingsService);
        RegisterPages();
        LoadState();
        ApplyMusicSettings(showFeedback: false);
        ShowPage("HomePanel");
        if (UpdateAutoCheckBox.IsChecked == true)
        {
            _ = AutoCheckForUpdatesAsync();
        }
    }

    private void RegisterPages()
    {
        foreach (var name in new[]
                 {
                     "HomePanel", "LaunchPanel", "DownloadPanel", "AccountsPanel", "ResourcesPanel",
                     "ModpacksPanel", "OnlinePanel", "SettingsPanel", "LogsPanel",
                 })
        {
            if (FindName(name) is FrameworkElement element)
            {
                _pages[name] = element;
            }
        }
    }

    private void LoadState()
    {
        _settings.LoadConfig();
        _versionSettings = _versionSettingsService.LoadVersionSettings();
        _accounts = _settings.LoadAccounts();

        GameDirectoryBox.Text = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        JavaPathBox.Text = _settings.Config.Get("JAVA", "path", "java");
        MemoryBox.Text = AppSettings.RecommendedMemoryMb().ToString();
        VersionTypeBox.SelectedIndex = 0;
        MirrorBox.SelectedIndex = _settings.Config.Get("DOWNLOAD", "mirror_source", "official") == "bmclapi" ? 1 : 0;
        ResourceTypeBox.SelectedIndex = 0;
        PackFormatBox.SelectedIndex = 0;
        GcStrategyBox.SelectedIndex = 0;
        VersionCategoryBox.SelectedIndex = 0;
        CacheStrategyBox.SelectedIndex = _settings.Config.Get("DOWNLOAD", "cache_strategy", "reuse") == "network_only" ? 1 : 0;
        ThemeBox.SelectedIndex = _settings.Config.Get("UI", "theme", "dark") switch { "light" => 1, "auto" => 2, _ => 0 };
        UpdateManifestBox.Text = _settings.Config.Get("UPDATE", "manifest_url", AppConstants.DefaultUpdateManifestUrl);
        UpdateAutoCheckBox.IsChecked = _settings.Config.GetBool("UPDATE", "auto_check", true);
        HomeContentSourceBox.Text = _settings.Config.Get("HOME", "content_source", "");
        HomeNetworkCheckBox.IsChecked = _settings.Config.GetBool("HOME", "allow_network", false);
        MusicPathBox.Text = _settings.Config.Get("MUSIC", "path", "");
        MusicEnabledBox.IsChecked = _settings.Config.GetBool("MUSIC", "enabled", false);
        MusicVolumeBox.Text = _settings.Config.Get("MUSIC", "volume", "35");
        MusicPauseOnLaunchBox.IsChecked = _settings.Config.GetBool("MUSIC", "pause_on_launch", true);
        CoreThreadsBox.Text = _settings.Config.Get("DOWNLOAD", "max_core_threads", "12");
        AssetThreadsBox.Text = _settings.Config.Get("DOWNLOAD", "max_asset_threads", "24");
        SpeedLimitBox.Text = _settings.Config.Get("DOWNLOAD", "speed_limit_kbps", "0");
        ModrinthApiBaseBox.Text = _settings.Config.Get("DOWNLOAD", "modrinth_api_base", "");
        ModrinthCdnBaseBox.Text = _settings.Config.Get("DOWNLOAD", "modrinth_cdn_base", "");
        ApplyModrinthSettings();
        RelayHostBox.Text = _settings.Config.Get("P2P", "relay_host", "flyliq.cn");
        RelayPortBox.Text = _settings.Config.Get("P2P", "relay_port", "10721");
        RoomBox.Text = _settings.Config.Get("P2P", "room", "");
        SecretBox.Text = _settings.Config.Get("P2P", "secret", "");
        HostPortBox.Text = _settings.Config.Get("P2P", "host_port", "25565");
        JoinPortBox.Text = _settings.Config.Get("P2P", "join_port", "25565");
        UdpEnabledBox.IsChecked = _settings.Config.GetBool("P2P", "udp_enabled", true);
        ServerListBox.Text = DecodeServerList(_settings.Config.Get("SERVERS", "items", ""));

        RefreshAccountsList();
        RefreshVersionList();
        RefreshHome();
        _ = RefreshHomeContentAsync(showFeedback: false);
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string pageName })
        {
            ShowPage(pageName);
        }
    }

    private void ShowPage(string pageName)
    {
        foreach (var (name, page) in _pages)
        {
            page.Visibility = name == pageName ? Visibility.Visible : Visibility.Collapsed;
        }

        (PageTitle.Text, PageSubtitle.Text) = pageName switch
        {
            "HomePanel" => ("首页", "账号、版本、下载和联机状态总览。"),
            "LaunchPanel" => ("启动游戏", "选择账号、Java、本地版本和版本独立设置。"),
            "DownloadPanel" => ("下载游戏", "刷新远程版本、下载原版并串联加载器安装任务。"),
            "AccountsPanel" => ("账号", "管理离线、Microsoft 和外置登录账号。"),
            "ResourcesPanel" => ("资源", "搜索 Modrinth 资源并检查本地 Mod 依赖。"),
            "ModpacksPanel" => ("整合包", "导出 Modrinth/CurseForge 整合包并分析崩溃日志。"),
            "OnlinePanel" => ("联机", "配置 McGo P2P 中继、房间号和本地端口。"),
            "SettingsPanel" => ("设置", "下载、更新和外观配置。"),
            "LogsPanel" => ("日志", "查看本地日志和诊断信息。"),
            _ => ("McGo", ""),
        };
        StatusText.Text = $"当前页面：{PageTitle.Text}";
    }

    private void RefreshAll(object sender, RoutedEventArgs e)
    {
        _settings.LoadConfig();
        _versionSettings = _versionSettingsService.LoadVersionSettings();
        _accounts = _settings.LoadAccounts();
        RefreshAccountsList();
        RefreshVersionList();
        RefreshHome();
        _ = RefreshHomeContentAsync(showFeedback: false);
        StatusText.Text = "状态已刷新。";
    }

    private void RefreshHome()
    {
        var versions = _launcher.GetLocalVersions(GameDirectoryBox.Text.Trim());
        HomeVersionCountText.Text = versions.Count.ToString();
        HomeAccountCountText.Text = _accounts.Count.ToString();
        HomeMemoryText.Text = $"{AppSettings.RecommendedMemoryMb()} MB";
        HomeRoomText.Text = string.IsNullOrWhiteSpace(RoomBox.Text) ? "未设置" : RoomBox.Text.Trim();
        HomeSummaryText.Text = string.Join(Environment.NewLine, [
            $"游戏目录：{Path.GetFullPath(GameDirectoryBox.Text.Trim().Length == 0 ? AppConstants.DefaultGameDirectory : GameDirectoryBox.Text.Trim())}",
            $"配置文件：{Path.GetFullPath(_settings.ConfigPath)}",
            $"版本设置：{Path.GetFullPath(VersionSettingsService.VersionSettingsFile)}",
            $"下载线程：核心 {CoreThreadsBox.Text} / 资源 {AssetThreadsBox.Text}",
        ]);
    }

    private void RefreshVersions(object sender, RoutedEventArgs e) => RefreshVersionList();

    private void VersionCategoryChanged(object sender, SelectionChangedEventArgs e) => RefreshVersionList();

    private void RefreshVersionList()
    {
        var selected = VersionsList.SelectedItem?.ToString();
        var allVersions = _launcher.GetLocalVersions(GameDirectoryBox.Text.Trim()).ToList();
        var category = ComboText(VersionCategoryBox, "全部版本");
        var versions = allVersions
            .Where(versionId => VersionVisibleInCategory(versionId, category))
            .OrderBy(versionId => VersionSettingsService.VersionSettingsEntry(_versionSettings, versionId)["favorite"]?.GetValue<bool>() == true ? 0 : 1)
            .ThenBy(versionId => versionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        FillList(VersionsList, versions);
        FillCombo(PackVersionBox, allVersions);
        if (selected is not null && versions.Contains(selected))
        {
            VersionsList.SelectedItem = selected;
        }
        else if (versions.Count > 0 && VersionsList.SelectedItem is null)
        {
            VersionsList.SelectedIndex = 0;
        }

        ModsDirBox.Text = VersionsList.SelectedItem is string versionId
            ? VersionSettingsService.ModsDirectoryForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true))
            : Path.Combine(GameDirectoryBox.Text.Trim(), "mods");
        RefreshHome();
        StatusText.Text = allVersions.Count == 0 ? "没有发现本地版本。" : $"发现 {allVersions.Count} 个本地版本，当前显示 {versions.Count} 个。";
    }

    private bool VersionVisibleInCategory(string versionId, string category)
    {
        var entry = VersionSettingsService.VersionSettingsEntry(_versionSettings, versionId);
        var hidden = entry["hidden"]?.GetValue<bool>() == true;
        var favorite = entry["favorite"]?.GetValue<bool>() == true;
        return category switch
        {
            "隐藏" => hidden,
            "收藏" => favorite && !hidden,
            _ => !hidden && _versionSettingsService.VersionMatchesCategory(GameDirectoryBox.Text.Trim(), versionId, category),
        };
    }

    private void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            return;
        }

        var entry = VersionSettingsService.VersionSettingsEntry(_versionSettings, versionId);
        VersionAliasBox.Text = entry["alias"]?.GetValue<string>() ?? "";
        QuickPlayBox.Text = entry["quick_play_singleplayer"]?.GetValue<string>() ?? "";
        ExtraJvmArgsBox.Text = entry["jvm_args"]?.GetValue<string>() ?? "";
        ExtraGameArgsBox.Text = entry["game_args"]?.GetValue<string>() ?? "";
        PreLaunchCommandBox.Text = entry["pre_launch_command"]?.GetValue<string>() ?? "";
        MemoryBox.Text = entry["max_memory_mb"]?.ToString() ?? AppSettings.RecommendedMemoryMb().ToString();
        MinMemoryBox.Text = entry["min_memory_mb"]?.ToString() ?? "";
        WindowWidthBox.Text = entry["window_width"]?.ToString() ?? "";
        WindowHeightBox.Text = entry["window_height"]?.ToString() ?? "";
        RuntimeDirectoryBox.Text = entry["runtime_directory"]?.GetValue<string>() ?? "";
        CustomModsDirectoryBox.Text = entry["mods_directory"]?.GetValue<string>() ?? "";
        SetComboText(GcStrategyBox, entry["gc_strategy"]?.GetValue<string>() ?? "G1GC");
        UseIsolationCheckBox.IsChecked = entry["use_isolated_directory"]?.GetValue<bool>() ?? false;
        VersionFavoriteCheckBox.IsChecked = entry["favorite"]?.GetValue<bool>() ?? false;
        VersionHiddenCheckBox.IsChecked = entry["hidden"]?.GetValue<bool>() ?? false;
        ModsDirBox.Text = VersionSettingsService.ModsDirectoryForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
        CrashRuntimeDirBox.Text = VersionSettingsService.RuntimeDirectoryForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
        ResourceGameVersionBox.Text = _versionSettingsService.ResolveBaseMinecraftVersion(GameDirectoryBox.Text.Trim(), versionId);
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        _settings.Config.Set("GAME", "directory", GameDirectoryBox.Text.Trim());
        _settings.Config.Set("USER", "username", SelectedAccountName() ?? "");
        _settings.Config.Set("JAVA", "path", JavaPathBox.Text.Trim());
        _settings.SaveConfig();
        RefreshVersionList();
        StatusText.Text = "设置已保存。";
    }

    private void SaveVersionSettings(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            StatusText.Text = "请先选择一个版本。";
            return;
        }

        var entry = VersionSettingsService.VersionSettingsEntry(_versionSettings, versionId);
        entry["alias"] = VersionAliasBox.Text.Trim();
        entry["quick_play_singleplayer"] = QuickPlayBox.Text.Trim();
        entry["jvm_args"] = ExtraJvmArgsBox.Text.Trim();
        entry["game_args"] = ExtraGameArgsBox.Text.Trim();
        entry["pre_launch_command"] = PreLaunchCommandBox.Text.Trim();
        entry["use_isolated_directory"] = UseIsolationCheckBox.IsChecked == true;
        entry["favorite"] = VersionFavoriteCheckBox.IsChecked == true;
        entry["hidden"] = VersionHiddenCheckBox.IsChecked == true;
        entry["max_memory_mb"] = int.TryParse(MemoryBox.Text, out var memory) ? memory : 0;
        entry["min_memory_mb"] = int.TryParse(MinMemoryBox.Text, out var minMemory) ? minMemory : 0;
        entry["window_width"] = int.TryParse(WindowWidthBox.Text, out var windowWidth) ? windowWidth : 0;
        entry["window_height"] = int.TryParse(WindowHeightBox.Text, out var windowHeight) ? windowHeight : 0;
        entry["gc_strategy"] = ComboText(GcStrategyBox, "G1GC");
        entry["runtime_directory"] = RuntimeDirectoryBox.Text.Trim();
        entry["mods_directory"] = CustomModsDirectoryBox.Text.Trim();
        _versionSettingsService.SaveVersionSettings(_versionSettings);
        RefreshVersionList();
        StatusText.Text = "版本设置已保存。";
    }

    private void CheckJavaRuntime(object sender, RoutedEventArgs e)
    {
        var javaPath = JavaPathBox.Text.Trim().Length == 0 ? "java" : JavaPathBox.Text.Trim();
        var major = JavaRuntimeService.GetJavaMajorVersion(javaPath);
        var required = VersionsList.SelectedItem is string versionId
            ? _launcher.InferRequiredJavaVersion(GameDirectoryBox.Text.Trim(), versionId)
            : 0;
        CommandPreviewBox.Text = major == 0
            ? $"无法识别 Java 版本：{javaPath}"
            : required > 0
                ? $"当前 Java：{major}{Environment.NewLine}所选版本需要：Java {required}{Environment.NewLine}{(major >= required ? "Java 版本满足要求。" : "Java 版本过低，请下载推荐 Java 或更换路径。")}"
                : $"当前 Java：{major}";
        StatusText.Text = major == 0 ? "Java 检测失败。" : "Java 检测完成。";
    }

    private async void DownloadRecommendedJava(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            StatusText.Text = "请先选择一个本地版本。";
            return;
        }

        var required = _launcher.InferRequiredJavaVersion(GameDirectoryBox.Text.Trim(), versionId);
        var installRoot = Path.Combine(GameDirectoryBox.Text.Trim(), "java_runtimes");
        try
        {
            SetBusy($"正在下载 Java {required}...");
            var progress = new Progress<JavaRuntimeDownloadProgress>(snapshot =>
            {
                var total = snapshot.TotalBytes > 0 ? AppFormatter.FormatBytes(snapshot.TotalBytes) : "未知大小";
                CommandPreviewBox.Text = $"{snapshot.Phase}：{snapshot.CurrentItem}{Environment.NewLine}{AppFormatter.FormatBytes(snapshot.DownloadedBytes)} / {total}";
            });
            var result = await JavaRuntimeService.DownloadJavaRuntimeAsync(required, installRoot, progress: progress);
            JavaPathBox.Text = result.JavaPath;
            _settings.Config.Set("JAVA", "path", result.JavaPath);
            _settings.SaveConfig();
            CommandPreviewBox.Text = string.Join(Environment.NewLine, [
                result.Reused ? $"已复用 Java {required}" : $"Java {required} 下载完成",
                $"路径：{result.JavaPath}",
                $"安装目录：{result.InstallDirectory}",
            ]);
            StatusText.Text = "Java 已就绪。";
        }
        catch (Exception exc)
        {
            CommandPreviewBox.Text = "下载 Java 失败：" + AppSettings.ConciseDownloadError(exc.Message);
            StatusText.Text = "下载 Java 失败。";
        }
    }

    private async void PreviewCommand(object sender, RoutedEventArgs e)
    {
        try
        {
            var account = await RefreshSelectedAccountForLaunchAsync();
            CommandPreviewBox.Text = string.Join(Environment.NewLine, BuildSelectedLaunchCommand(account));
            StatusText.Text = "启动命令已生成。";
        }
        catch (Exception exc)
        {
            CommandPreviewBox.Text = exc.Message;
            StatusText.Text = "生成失败。";
        }
    }

    private async void LaunchGame(object sender, RoutedEventArgs e)
    {
        try
        {
            var preflight = ValidateSelectedLaunchPreflight();
            if (!preflight.IsOk)
            {
                var lines = new List<string> { "启动前检查未通过：" };
                lines.AddRange(preflight.Issues.Take(8).Select(issue => issue.Message));
                if (preflight.Issues.Count > 8)
                {
                    lines.Add($"还有 {preflight.Issues.Count - 8} 项未列出。");
                }

                lines.Add("");
                lines.Add("请先执行“补全/校验文件”，或更换满足要求的 Java。");
                CommandPreviewBox.Text = string.Join(Environment.NewLine, lines);
                StatusText.Text = "启动前检查未通过。";
                return;
            }

            var account = await RefreshSelectedAccountForLaunchAsync();
            RunPreLaunchCommand();
            var command = BuildSelectedLaunchCommand(account);
            var startInfo = new ProcessStartInfo(command[0]) { UseShellExecute = false };
            foreach (var argument in command.Skip(1))
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            if (MusicPauseOnLaunchBox.IsChecked == true)
            {
                _musicPlayer.Pause();
            }

            StatusText.Text = "Minecraft 已启动。";
        }
        catch (Exception exc)
        {
            StatusText.Text = "启动失败：" + exc.Message;
        }
    }

    private void RunPreLaunchCommand()
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            return;
        }

        var options = VersionSettingsService.LaunchOptionsForVersion(
            GameDirectoryBox.Text.Trim(),
            _versionSettings,
            versionId,
            _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
        if (string.IsNullOrWhiteSpace(options.PreLaunchCommand))
        {
            return;
        }

        Directory.CreateDirectory(options.RuntimeDirectory);
        StatusText.Text = "正在执行启动前命令...";
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
        if (VersionsList.SelectedItem is not string versionId)
        {
            return new LaunchPreflightResult([new LaunchPreflightIssue("version", "请先选择本地游戏版本。")], 0, 1);
        }

        return _launchPreflight.Validate(
            GameDirectoryBox.Text.Trim(),
            versionId,
            JavaPathBox.Text.Trim().Length == 0 ? "java" : JavaPathBox.Text.Trim(),
            includeAssets: true);
    }

    private void OpenVersionFolder(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            StatusText.Text = "请先选择一个本地版本。";
            return;
        }

        var path = Path.Combine(GameDirectoryBox.Text.Trim(), "versions", versionId);
        if (!Directory.Exists(path))
        {
            StatusText.Text = "版本目录不存在：" + path;
            return;
        }

        OpenDirectory(path, create: false);
    }

    private void OpenSavesFolder(object sender, RoutedEventArgs e) => OpenVersionRuntimeSubfolder("saves");

    private void OpenModsFolder(object sender, RoutedEventArgs e) => OpenDirectory(CurrentModsDirectory(), create: true);

    private void OpenResourcepacksFolder(object sender, RoutedEventArgs e) => OpenVersionRuntimeSubfolder("resourcepacks");

    private void OpenShaderpacksFolder(object sender, RoutedEventArgs e) => OpenVersionRuntimeSubfolder("shaderpacks");

    private void OpenScreenshotsFolder(object sender, RoutedEventArgs e) => OpenVersionRuntimeSubfolder("screenshots");

    private void OpenVersionRuntimeSubfolder(string name)
    {
        var runtime = CurrentRuntimeDirectory();
        if (runtime.Length == 0)
        {
            StatusText.Text = "请先选择一个本地版本。";
            return;
        }

        OpenDirectory(Path.Combine(runtime, name), create: true);
    }

    private async void ExportLaunchScript(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            StatusText.Text = "请先选择一个本地版本。";
            return;
        }

        try
        {
            var account = await RefreshSelectedAccountForLaunchAsync();
            var command = BuildSelectedLaunchCommand(account);
            var options = VersionSettingsService.LaunchOptionsForVersion(
                GameDirectoryBox.Text.Trim(),
                _versionSettings,
                versionId,
                _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            var scriptDir = Path.Combine(GameDirectoryBox.Text.Trim(), "mcgo_scripts");
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, $"launch-{LaunchScriptService.SafeScriptName(versionId)}.bat");
            var script = LaunchScriptService.BuildWindowsBatchScript(command, options.PreLaunchCommand);
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            CommandPreviewBox.Text = $"启动脚本已导出：{scriptPath}";
            StatusText.Text = "启动脚本已导出。";
        }
        catch (Exception exc)
        {
            CommandPreviewBox.Text = "导出启动脚本失败：" + exc.Message;
            StatusText.Text = "导出启动脚本失败。";
        }
    }

    private void DeleteSelectedVersion(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            StatusText.Text = "请先选择一个本地版本。";
            return;
        }

        var target = Path.GetFullPath(Path.Combine(GameDirectoryBox.Text.Trim(), "versions", versionId));
        var display = VersionSettingsService.VersionDisplayName(_versionSettings, versionId);
        var reply = MessageBox.Show(
            $"确定要删除版本 {display} 吗？{Environment.NewLine}{Environment.NewLine}将删除：{target}",
            "删除版本",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var result = VersionManagementService.DeleteVersion(GameDirectoryBox.Text.Trim(), versionId);
            _versionSettings.Remove(versionId);
            if (_versionSettings["_meta"] is JsonObject meta &&
                string.Equals(meta["last_launched_version"]?.GetValue<string>(), versionId, StringComparison.Ordinal))
            {
                meta["last_launched_version"] = "";
            }

            _versionSettingsService.SaveVersionSettings(_versionSettings);
            RefreshVersionList();
            CommandPreviewBox.Text = $"版本已删除：{result.DeletedPath}";
            StatusText.Text = "版本已删除。";
        }
        catch (Exception exc)
        {
            CommandPreviewBox.Text = "删除版本失败：" + exc.Message;
            StatusText.Text = "删除版本失败。";
        }
    }

    private string CurrentRuntimeDirectory()
    {
        return VersionsList.SelectedItem is string versionId
            ? VersionSettingsService.RuntimeDirectoryForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true))
            : "";
    }

    private string CurrentModsDirectory()
    {
        return VersionsList.SelectedItem is string versionId
            ? VersionSettingsService.ModsDirectoryForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true))
            : "";
    }

    private void OpenDirectory(string path, bool create)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = "请先选择一个本地版本。";
            return;
        }

        if (create)
        {
            Directory.CreateDirectory(path);
        }

        if (!Directory.Exists(path))
        {
            StatusText.Text = "目录不存在：" + path;
            return;
        }

        Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
        StatusText.Text = "已打开目录：" + path;
    }

    private List<string> BuildSelectedLaunchCommand(JsonObject? selectedAccount = null)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            throw new InvalidOperationException("请先选择一个版本。");
        }

        selectedAccount ??= SelectedAccount();
        var options = VersionSettingsService.LaunchOptionsForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
        var maxMemory = int.TryParse(MemoryBox.Text, out var parsedMemory) ? parsedMemory : options.MaxMemoryMb;
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

            StatusText.Text = "正在刷新 Microsoft 账号...";
            await _microsoftAuthenticator.RefreshAccessTokenAsync(refreshToken);
            refreshed = JsonNode.Parse(account.ToJsonString())!.AsObject();
            refreshed["access_token"] = _microsoftAuthenticator.MinecraftAccessToken ?? account["access_token"]?.GetValue<string>() ?? "";
            refreshed["refresh_token"] = _microsoftAuthenticator.RefreshToken ?? refreshToken;
        }
        else if (type == "external")
        {
            StatusText.Text = "正在刷新外置登录账号...";
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

    private async void RefreshRemoteVersions(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy("正在刷新远程版本...");
            var versions = await _installServices.GetRemoteVersionsAsync(ComboText(VersionTypeBox, "release"), ComboText(MirrorBox, "official"));
            FillCombo(RemoteVersionsBox, versions);
            DownloadLogBox.Text = $"已获取 {versions.Count} 个远程版本。";
            StatusText.Text = "远程版本已刷新。";
        }
        catch (Exception exc)
        {
            DownloadLogBox.Text = exc.Message;
            StatusText.Text = "刷新远程版本失败。";
        }
    }

    private async void DownloadSelectedVersion(object sender, RoutedEventArgs e)
    {
        if (RemoteVersionsBox.SelectedItem is null)
        {
            StatusText.Text = "请先选择远程版本。";
            return;
        }

        var versionId = RemoteVersionsBox.SelectedItem.ToString() ?? "";
        var mirror = ComboText(MirrorBox, "official");
        try
        {
            SetBusy($"正在下载 {versionId}...");
            var (metadata, usedMirror) = await _installServices.GetVersionMetadataWithFallbackAsync(versionId, mirror, message => AppendDownload(message));
            var progress = new Progress<DownloadProgress>(snapshot =>
            {
                DownloadLogBox.Text = $"[{snapshot.Phase}] {snapshot.CompletedJobs}/{snapshot.TotalJobs} reuse={snapshot.ReusedJobs} {Path.GetFileName(snapshot.CurrentFile)}";
            });
            var summary = await _downloader.DownloadGameFilesAsync(metadata, GameDirectoryBox.Text.Trim(), versionId, usedMirror, IncludeAssetsCheckBox.IsChecked == true, progress);
            AppendDownload($"下载完成：{summary.CompletedJobs}/{summary.TotalJobs}，复用 {summary.ReusedJobs}，写入 {AppFormatter.FormatBytes(summary.DownloadedBytes)}");

            var installTypes = new List<string>();
            if (InstallFabricCheckBox.IsChecked == true) installTypes.Add("fabric");
            if (InstallForgeCheckBox.IsChecked == true) installTypes.Add("forge");
            if (InstallNeoForgeCheckBox.IsChecked == true) installTypes.Add("neoforge");
            if (InstallFabricApiCheckBox.IsChecked == true) installTypes.Add("fabric_api");
            if (InstallOptifineCheckBox.IsChecked == true) installTypes.Add("optifine");
            if (installTypes.Count > 0)
            {
                var engine = new InstallerEngine(versionId, usedMirror, GameDirectoryBox.Text.Trim(), JavaPathBox.Text.Trim(), AppendDownload);
                var result = await engine.InstallSequenceAsync(installTypes);
                AppendDownload(result.Message);
            }

            RefreshVersionList();
            StatusText.Text = "下载任务完成。";
        }
        catch (Exception exc)
        {
            AppendDownload("失败：" + AppSettings.ConciseDownloadError(exc.Message));
            StatusText.Text = "下载失败。";
        }
    }

    private async void RepairSelectedVersion(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            StatusText.Text = "请先选择一个本地版本。";
            return;
        }

        var gameDirectory = GameDirectoryBox.Text.Trim();
        var versionJsonPath = Path.Combine(gameDirectory, "versions", versionId, $"{versionId}.json");
        if (!File.Exists(versionJsonPath))
        {
            CommandPreviewBox.Text = $"版本 JSON 不存在：{versionJsonPath}";
            StatusText.Text = "补全失败。";
            return;
        }

        try
        {
            SetBusy($"正在补全/校验 {versionId}...");
            CommandPreviewBox.Text = $"准备校验 {versionId}...";
            var versionJson = JsonNode.Parse(await File.ReadAllTextAsync(versionJsonPath)) as JsonObject
                ?? throw new InvalidOperationException("版本 JSON 无法解析。");
            var progress = new Progress<DownloadProgress>(snapshot =>
            {
                CommandPreviewBox.Text = $"[{snapshot.Phase}] {snapshot.CompletedJobs}/{snapshot.TotalJobs} reuse={snapshot.ReusedJobs} {Path.GetFileName(snapshot.CurrentFile)}";
            });
            var summary = await _downloader.RepairGameFilesAsync(versionJson, gameDirectory, versionId, ComboText(MirrorBox, "official"), includeAssets: true, progress);
            CommandPreviewBox.Text = string.Join(Environment.NewLine, [
                $"补全完成：{versionId}",
                $"校验/补齐：{summary.CompletedJobs}/{summary.TotalJobs}",
                $"复用：{summary.ReusedJobs}",
                $"写入：{AppFormatter.FormatBytes(summary.DownloadedBytes)}",
                $"natives：解压 {summary.NativeFilesExtracted} 个文件到 {summary.NativesDirectory}",
            ]);
            StatusText.Text = "版本文件已校验并补全。";
        }
        catch (Exception exc)
        {
            CommandPreviewBox.Text = "补全失败：" + AppSettings.ConciseDownloadError(exc.Message);
            StatusText.Text = "补全失败。";
        }
    }

    private void RefreshAccounts(object sender, RoutedEventArgs e)
    {
        _accounts = _settings.LoadAccounts();
        RefreshAccountsList();
    }

    private void RefreshAccountsList(string? selectedAccountId = null)
    {
        selectedAccountId ??= SelectedAccount()?["id"]?.GetValue<string>();
        AccountsList.Items.Clear();
        AccountComboBox.Items.Clear();
        foreach (var account in _accounts)
        {
            var label = AccountLabel(account);
            AccountsList.Items.Add(label);
            AccountComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = account });
        }

        if (!string.IsNullOrWhiteSpace(selectedAccountId))
        {
            foreach (ComboBoxItem item in AccountComboBox.Items)
            {
                if (item.Tag is JsonObject account && account["id"]?.GetValue<string>() == selectedAccountId)
                {
                    AccountComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        if (AccountComboBox.Items.Count > 0 && AccountComboBox.SelectedItem is null)
        {
            AccountComboBox.SelectedIndex = 0;
        }

        RefreshHome();
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

    private void AddOfflineAccount(object sender, RoutedEventArgs e)
    {
        var username = OfflineNameBox.Text.Trim();
        if (username.Length == 0)
        {
            StatusText.Text = "离线账号需要用户名。";
            return;
        }

        var existing = _accounts.FirstOrDefault(account => account["type"]?.GetValue<string>() == "offline" && account["username"]?.GetValue<string>() == username);
        var account = existing ?? new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["type"] = "offline" };
        account["display_name"] = username;
        account["username"] = username;
        account["uuid"] = OfflineUuidBox.Text.Trim();
        account["access_token"] = "";
        account["refresh_token"] = "";
        if (existing is null)
        {
            _accounts.Add(account);
        }

        _settings.SaveAccounts(_accounts);
        RefreshAccountsList();
        StatusText.Text = "离线账号已保存。";
    }

    private void NormalizeExternalServer(object sender, RoutedEventArgs e)
    {
        try
        {
            ExternalServerBox.Text = ExternalAuthService.NormalizeAuthServer(ExternalServerBox.Text);
            StatusText.Text = "认证服务器地址已规范化。";
        }
        catch (Exception exc)
        {
            StatusText.Text = exc.Message;
        }
    }

    private async void ProbeExternalServer(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy("正在探测认证服务器...");
            var service = new ExternalAuthService();
            var result = await service.ProbeExternalAuthServerAsync(ExternalServerBox.Text);
            ExternalServerBox.Text = result.Server;
            StatusText.Text = result.Message;
        }
        catch (Exception exc)
        {
            StatusText.Text = "认证服务器探测失败：" + exc.Message;
        }
    }

    private void ChooseAuthlibInjector(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "authlib-injector.jar|*.jar|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            AuthlibPathBox.Text = dialog.FileName;
            StatusText.Text = "authlib-injector 路径已选择。";
        }
    }

    private async void DownloadAuthlibInjector(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy("正在下载 authlib-injector...");
            var targetDir = Path.Combine(GameDirectoryBox.Text.Trim(), "authlib-injector");
            var service = new ExternalAuthService();
            var result = await service.DownloadAuthlibInjectorAsync(targetDir);
            AuthlibPathBox.Text = result.Path;
            StatusText.Text = "authlib-injector 已下载。";
        }
        catch (Exception exc)
        {
            StatusText.Text = "authlib-injector 下载失败：" + exc.Message;
        }
    }

    private void GenerateMicrosoftLogin(object sender, RoutedEventArgs e)
    {
        MicrosoftOutputBox.Text = _microsoftAuthenticator.GetLoginUrl();
        StatusText.Text = "Microsoft 登录链接已生成。";
    }

    private async void WaitMicrosoftCallback(object sender, RoutedEventArgs e)
    {
        try
        {
            MicrosoftOutputBox.Text = "正在等待浏览器回调...";
            var callback = await _oauthCallbackServer.WaitForCodeAsync(AppConstants.RedirectUri, TimeSpan.FromMinutes(5));
            _microsoftAuthenticator.AuthorizationCode = callback.Code;
            await _microsoftAuthenticator.AuthenticateAsync();
            var profile = await _microsoftAuthenticator.GetMinecraftProfileAsync();
            var account = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["type"] = "microsoft",
                ["display_name"] = profile.Name,
                ["username"] = profile.Name,
                ["uuid"] = profile.Id,
                ["access_token"] = _microsoftAuthenticator.MinecraftAccessToken ?? "",
                ["refresh_token"] = _microsoftAuthenticator.RefreshToken ?? "",
            };
            _accounts.Add(account);
            _settings.SaveAccounts(_accounts);
            RefreshAccountsList();
            MicrosoftOutputBox.Text = $"Microsoft 账号已添加：{profile.Name}";
            StatusText.Text = "Microsoft 登录完成。";
        }
        catch (Exception exc)
        {
            MicrosoftOutputBox.Text = exc.Message;
            StatusText.Text = "Microsoft 登录失败。";
        }
    }

    private async void LoginExternalAccount(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy("正在登录外置账号...");
            var service = new ExternalAuthService();
            var account = await service.AuthenticateExternalAccountAsync(ExternalServerBox.Text, ExternalUsernameBox.Text, ExternalPasswordBox.Text);
            account["id"] = Guid.NewGuid().ToString();
            account["type"] = "external";
            account["auth_server"] = ExternalAuthService.NormalizeAuthServer(ExternalServerBox.Text);
            account["authlib_injector_path"] = AuthlibPathBox.Text.Trim();
            _accounts.Add(account);
            _settings.SaveAccounts(_accounts);
            RefreshAccountsList();
            StatusText.Text = "外置账号已添加。";
        }
        catch (Exception exc)
        {
            StatusText.Text = "外置登录失败：" + exc.Message;
        }
    }

    private void PreviewAuthlibArgs(object sender, RoutedEventArgs e)
    {
        try
        {
            var args = ExternalAuthService.AuthlibInjectorArgs(new JsonObject
            {
                ["type"] = "external",
                ["auth_server"] = ExternalServerBox.Text.Trim(),
                ["authlib_injector_path"] = AuthlibPathBox.Text.Trim(),
            });
            StatusText.Text = string.Join(" ", args);
        }
        catch (Exception exc)
        {
            StatusText.Text = exc.Message;
        }
    }

    private async void SearchResources(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy("正在搜索资源...");
            var hits = await _resourceMarket.SearchModrinthResourcesAsync(ResourceQueryBox.Text.Trim(), ComboText(ResourceTypeBox, "mod"), ResourceGameVersionBox.Text.Trim(), ResourceLoaderBox.Text.Trim());
            ResourceResultsList.Items.Clear();
            _resourceHitsByLabel.Clear();
            foreach (var hit in hits.OfType<JsonObject>())
            {
                var label = AppFormatter.FormatResourceHitLabel(ToDictionary(hit));
                ResourceResultsList.Items.Add(label);
                _resourceHitsByLabel[label] = hit;
            }

            StatusText.Text = $"找到 {hits.Count} 个结果。";
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = exc.Message;
            StatusText.Text = "资源搜索失败。";
        }
    }

    private async void ResolveDependencies(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy("正在解析 Modrinth 依赖...");
            var plan = await _resourceMarket.ResolveModrinthDependencyPlanAsync(ModsDirBox.Text.Trim());
            var builder = new StringBuilder();
            builder.AppendLine("已安装项目：" + string.Join(", ", plan.InstalledProjectIds));
            builder.AppendLine("缺失依赖：");
            foreach (var missing in plan.MissingDependencies)
            {
                builder.AppendLine($"- {missing.ProjectId} <- {string.Join(", ", missing.RequiredBy)}");
            }

            ResourceOutputBox.Text = builder.ToString();
            StatusText.Text = "依赖计划已生成。";
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = exc.Message;
            StatusText.Text = "依赖解析失败。";
        }
    }

    private async void InstallSelectedResource(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ResourceResultsList.SelectedItem is not string label || !_resourceHitsByLabel.TryGetValue(label, out var hit))
            {
                StatusText.Text = "请先选择一个资源。";
                return;
            }

            var projectId = hit["project_id"]?.GetValue<string>() ?? hit["slug"]?.GetValue<string>() ?? "";
            if (projectId.Length == 0)
            {
                StatusText.Text = "选中资源缺少 project_id。";
                return;
            }

            var targetDir = CurrentResourceTargetDir();
            ModsDirBox.Text = targetDir;
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
                status: message => Dispatcher.Invoke(() => StatusText.Text = message),
                progress: progress);
            ResourceOutputBox.Text = $"已安装：{result.Filename}\n版本：{result.Version}\n路径：{result.Path}\n依赖：{string.Join(", ", result.DependenciesInstalled)}";
            StatusText.Text = "资源安装完成。";
            RefreshLocalResources(this, new RoutedEventArgs());
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = exc.Message;
            StatusText.Text = "资源安装失败。";
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
                ModsDirBox.Text.Trim(),
                message => Dispatcher.Invoke(() => StatusText.Text = message),
                progress);
            ResourceOutputBox.Text = "已安装缺失前置：\n" + string.Join(Environment.NewLine, result.DependenciesInstalled);
            StatusText.Text = result.DependenciesInstalled.Count == 0 ? "没有缺失前置。" : "缺失前置已安装。";
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = exc.Message;
            StatusText.Text = "前置安装失败。";
        }
    }

    private void RefreshLocalResources(object sender, RoutedEventArgs e)
    {
        try
        {
            var targetDir = CurrentResourceTargetDir();
            ModsDirBox.Text = targetDir;
            var items = ResourceMarketService.ListLocalResources(ResourceQueryBox.Text.Trim(), ComboText(ResourceTypeBox, "mod"), targetDir);
            LocalResourcesList.Items.Clear();
            _localResourcesByLabel.Clear();
            foreach (var item in items)
            {
                var label = $"{item.Title}  [{item.Status}]  {AppFormatter.FormatBytes(item.SizeBytes)}{Environment.NewLine}{item.RelativePath}";
                LocalResourcesList.Items.Add(label);
                _localResourcesByLabel[label] = item;
            }

            StatusText.Text = $"找到 {items.Count} 个本地资源。";
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = exc.Message;
            StatusText.Text = "刷新本地资源失败。";
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
            StatusText.Text = "本地资源详情已生成。";
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = exc.Message;
            StatusText.Text = "读取本地资源失败。";
        }
    }

    private void EnableLocalResource(object sender, RoutedEventArgs e) => SetSelectedLocalResourceEnabled(true);

    private void DisableLocalResource(object sender, RoutedEventArgs e) => SetSelectedLocalResourceEnabled(false);

    private void DeleteLocalResource(object sender, RoutedEventArgs e)
    {
        try
        {
            var item = SelectedLocalResource();
            if (item is null)
            {
                return;
            }

            if (MessageBox.Show($"删除 {item.Title}？", "删除本地资源", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            ResourceMarketService.DeleteLocalResource(item.Path);
            ResourceOutputBox.Text = "已删除：" + item.Path;
            StatusText.Text = "本地资源已删除。";
            RefreshLocalResources(this, new RoutedEventArgs());
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = exc.Message;
            StatusText.Text = "删除本地资源失败。";
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
            StatusText.Text = enabled ? "本地资源已启用。" : "本地资源已禁用。";
            RefreshLocalResources(this, new RoutedEventArgs());
        }
        catch (Exception exc)
        {
            ResourceOutputBox.Text = exc.Message;
            StatusText.Text = "更新本地资源状态失败。";
        }
    }

    private LocalResourceInfo? SelectedLocalResource()
    {
        if (LocalResourcesList.SelectedItem is string label && _localResourcesByLabel.TryGetValue(label, out var item))
        {
            return item;
        }

        StatusText.Text = "请先选择一个本地资源。";
        return null;
    }

    private string CurrentResourceTargetDir()
        => ResourceMarketService.ResourceDirectoryForType(
            GameDirectoryBox.Text.Trim(),
            _versionSettings,
            VersionsList.SelectedItem?.ToString() ?? PackVersionBox.SelectedItem?.ToString() ?? "",
            ComboText(ResourceTypeBox, "mod"),
            _settings.Config.GetBool("GAME", "enable_resource_isolation", true));

    private void ExportModpack(object sender, RoutedEventArgs e)
    {
        try
        {
            var versionId = PackVersionBox.SelectedItem?.ToString() ?? VersionsList.SelectedItem?.ToString() ?? "";
            if (versionId.Length == 0)
            {
                StatusText.Text = "请先选择要导出的版本。";
                return;
            }

            var target = PackTargetBox.Text.Trim();
            if (target.Length == 0)
            {
                target = Path.Combine(Environment.CurrentDirectory, $"{versionId}.mrpack");
                PackTargetBox.Text = target;
            }

            var added = _modpacks.ExportModpack(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, target, ComboText(PackFormatBox, "modrinth"), _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            PackOutputBox.Text = $"已导出 {added} 个文件：{target}";
            StatusText.Text = "整合包已导出。";
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = exc.Message;
            StatusText.Text = "导出失败。";
        }
    }

    private void ChooseModpackFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "整合包|*.mrpack;*.zip|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            ImportPackPathBox.Text = dialog.FileName;
        }
    }

    private async void ImportModpack(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy("正在导入整合包...");
            var result = await _modpacks.ImportModpackAsync(
                ImportPackPathBox.Text.Trim(),
                GameDirectoryBox.Text.Trim(),
                _versionSettings,
                ImportTargetVersionBox.Text.Trim(),
                _settings.Config.GetBool("GAME", "enable_resource_isolation", true),
                message => Dispatcher.Invoke(() => StatusText.Text = message));
            _versionSettingsService.SaveVersionSettings(_versionSettings);
            RefreshVersionList();
            PackVersionBox.SelectedItem = result.Version;
            VersionsList.SelectedItem = result.Version;
            PackOutputBox.Text = string.Join(Environment.NewLine, [
                $"已导入：{result.Name}",
                $"版本：{result.Version}",
                $"Minecraft：{result.Minecraft}",
                $"加载器：{result.Loader}",
                $"覆写文件：{result.OverridesCopied}",
                $"下载文件：{result.FilesDownloaded}",
                $"缺失文件：{result.MissingFiles}",
                $"运行目录：{result.RuntimeDir}",
                result.MissingReportPath.Length > 0 ? $"缺失报告：{result.MissingReportPath}" : "",
            ]);
            StatusText.Text = "整合包导入完成。";
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = exc.Message;
            StatusText.Text = "整合包导入失败。";
        }
    }

    private void ChooseCapsuleFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "McGo 胶囊|*.mcgo-capsule;*.mcgo;*.zip|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            CapsulePathBox.Text = dialog.FileName;
        }
    }

    private void ImportCapsule(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = _capsules.ImportCapsule(
                CapsulePathBox.Text.Trim(),
                GameDirectoryBox.Text.Trim(),
                _versionSettings,
                CapsuleTargetVersionBox.Text.Trim(),
                _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            _versionSettingsService.SaveVersionSettings(_versionSettings);
            ApplyCapsuleP2P(result.P2P);
            RefreshVersionList();
            PackVersionBox.SelectedItem = result.Version;
            VersionsList.SelectedItem = result.Version;
            PackOutputBox.Text = string.Join(Environment.NewLine, [
                $"胶囊已导入：{result.Name}",
                $"版本：{result.Version}",
                $"Minecraft：{result.Minecraft}",
                $"加载器：{result.Loader}",
                $"文件清单：{result.Files.Count}",
                $"待补齐文件：{result.MissingFiles.Count}",
                $"运行目录：{result.RuntimeDir}",
            ]);
            StatusText.Text = "胶囊导入完成。";
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = exc.Message;
            StatusText.Text = "胶囊导入失败。";
        }
    }

    private void ExportCapsule(object sender, RoutedEventArgs e)
    {
        try
        {
            var versionId = PackVersionBox.SelectedItem?.ToString() ?? VersionsList.SelectedItem?.ToString() ?? "";
            if (versionId.Length == 0)
            {
                StatusText.Text = "请先选择要导出的版本。";
                return;
            }

            var target = CapsulePathBox.Text.Trim();
            if (target.Length == 0)
            {
                target = Path.Combine(Environment.CurrentDirectory, $"{versionId}.mcgo-capsule");
                CapsulePathBox.Text = target;
            }

            var result = _capsules.ExportCapsule(
                GameDirectoryBox.Text.Trim(),
                _versionSettings,
                versionId,
                target,
                BuildCapsuleP2PInfo(),
                _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            PackOutputBox.Text = $"胶囊已导出：{result.TargetPath}\n文件：{result.FileCount}\n大小：{AppFormatter.FormatBytes(result.TotalSize)}";
            StatusText.Text = "胶囊导出完成。";
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = exc.Message;
            StatusText.Text = "胶囊导出失败。";
        }
    }

    private JsonObject BuildCapsuleP2PInfo()
        => new()
        {
            ["relay_host"] = RelayHostBox.Text.Trim(),
            ["relay_port"] = P2PUtilities.CleanPort(RelayPortBox.Text, 10721),
            ["room"] = P2PUtilities.CleanRoom(RoomBox.Text),
            ["secret"] = SecretBox.Text.Trim(),
            ["host_port"] = P2PUtilities.CleanPort(HostPortBox.Text),
            ["join_port"] = P2PUtilities.CleanPort(JoinPortBox.Text),
        };

    private void ApplyCapsuleP2P(JsonObject p2p)
    {
        if (p2p.Count == 0)
        {
            return;
        }

        RelayHostBox.Text = p2p["relay_host"]?.GetValue<string>() ?? RelayHostBox.Text;
        RelayPortBox.Text = (p2p["relay_port"]?.GetValue<int?>() ?? P2PUtilities.CleanPort(RelayPortBox.Text, 10721)).ToString();
        RoomBox.Text = p2p["room"]?.GetValue<string>() ?? RoomBox.Text;
        SecretBox.Text = p2p["secret"]?.GetValue<string>() ?? SecretBox.Text;
        HostPortBox.Text = (p2p["host_port"]?.GetValue<int?>() ?? P2PUtilities.CleanPort(HostPortBox.Text)).ToString();
        JoinPortBox.Text = (p2p["join_port"]?.GetValue<int?>() ?? P2PUtilities.CleanPort(JoinPortBox.Text)).ToString();
        SaveOnlineSettings(this, new RoutedEventArgs());
    }

    private void AnalyzeCrashLogs(object sender, RoutedEventArgs e)
    {
        var versionId = PackVersionBox.SelectedItem?.ToString() ?? VersionsList.SelectedItem?.ToString() ?? "";
        PackOutputBox.Text = ModpackService.AnalyzeCrashLogs(GameDirectoryBox.Text.Trim(), versionId, CrashRuntimeDirBox.Text.Trim());
        StatusText.Text = "崩溃日志分析完成。";
    }

    private void GenerateRoom(object sender, RoutedEventArgs e)
    {
        RoomBox.Text = P2PUtilities.GenerateRoomCode();
        RefreshHome();
    }

    private void PickJoinPort(object sender, RoutedEventArgs e)
    {
        JoinPortBox.Text = P2PUtilities.PickLocalPort().ToString();
    }

    private void SaveServers(object sender, RoutedEventArgs e)
    {
        _settings.Config.Set("SERVERS", "items", EncodeServerList(ServerListBox.Text));
        _settings.SaveConfig();
        StatusText.Text = "服务器列表已保存。";
    }

    private void CopyFirstServer(object sender, RoutedEventArgs e)
    {
        var first = ServerListBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2)[0].Trim())
            .FirstOrDefault(line => line.Length > 0);
        if (string.IsNullOrWhiteSpace(first))
        {
            StatusText.Text = "服务器列表为空。";
            return;
        }

        Clipboard.SetText(first);
        StatusText.Text = "已复制服务器地址：" + first;
    }

    private async void DetectNat(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy("正在检测 NAT...");
            NatOutputBox.Text = "正在通过 STUN 探测 UDP 公网映射...";
            var result = await NatUtils.DetectNatTypeAsync();
            NatOutputBox.Text = $"{result.NatType} ({result.Quality}){Environment.NewLine}{result.Summary}{Environment.NewLine}{result.Details}";
            StatusText.Text = "NAT 检测完成。";
        }
        catch (Exception exc)
        {
            NatOutputBox.Text = exc.Message;
            StatusText.Text = "NAT 检测失败。";
        }
    }

    private void SaveOnlineSettings(object sender, RoutedEventArgs e)
    {
        _settings.Config.Set("P2P", "relay_host", RelayHostBox.Text.Trim());
        _settings.Config.Set("P2P", "relay_port", RelayPortBox.Text.Trim());
        _settings.Config.Set("P2P", "room", P2PUtilities.CleanRoom(RoomBox.Text));
        _settings.Config.Set("P2P", "secret", SecretBox.Text.Trim());
        _settings.Config.Set("P2P", "host_port", P2PUtilities.CleanPort(HostPortBox.Text).ToString());
        _settings.Config.Set("P2P", "join_port", P2PUtilities.CleanPort(JoinPortBox.Text).ToString());
        _settings.Config.Set("P2P", "udp_enabled", (UdpEnabledBox.IsChecked == true).ToString());
        _settings.Config.Set("SERVERS", "items", EncodeServerList(ServerListBox.Text));
        _settings.SaveConfig();
        StatusText.Text = "联机设置已保存。";
        RefreshHome();
    }

    private void BuildInviteText(object sender, RoutedEventArgs e)
    {
        var room = P2PUtilities.CleanRoom(RoomBox.Text);
        OnlineOutputBox.Text = string.Join(Environment.NewLine, [
            $"中继：{RelayHostBox.Text.Trim()}:{P2PUtilities.CleanPort(RelayPortBox.Text, 10721)}",
            $"房间：{room}",
            $"口令：{SecretBox.Text.Trim()}",
            $"加入者连接：127.0.0.1:{P2PUtilities.CleanPort(JoinPortBox.Text)}",
            $"UDP 打洞优先：{(UdpEnabledBox.IsChecked == true ? "开启" : "关闭")}",
        ]);
    }

    private void StartP2PHost(object sender, RoutedEventArgs e)
    {
        StartP2P(async token =>
        {
            var config = BuildP2PConfig();
            await _p2pTunnel.RunHostAsync(config, UdpEnabledBox.IsChecked == true, "127.0.0.1", AppendOnline, token);
        }, "房主隧道已启动。");
    }

    private void StartP2PGuest(object sender, RoutedEventArgs e)
    {
        StartP2P(async token =>
        {
            var config = BuildP2PConfig();
            await _p2pTunnel.RunGuestAsync(config, UdpEnabledBox.IsChecked == true, AppendOnline, token);
        }, "加入者隧道已启动。");
    }

    private P2PTunnelConfig BuildP2PConfig()
        => new(
            RelayHostBox.Text.Trim(),
            P2PUtilities.CleanPort(RelayPortBox.Text, 10721),
            RoomBox.Text,
            SecretBox.Text.Trim(),
            P2PUtilities.CleanPort(JoinPortBox.Text),
            P2PUtilities.CleanPort(HostPortBox.Text));

    private void StartP2P(Func<CancellationToken, Task> runner, string message)
    {
        StopP2P(this, new RoutedEventArgs());
        _p2pCts = new CancellationTokenSource();
        AppendOnline(message);
        _ = Task.Run(async () =>
        {
            try
            {
                await runner(_p2pCts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendOnline("隧道已停止。");
            }
            catch (Exception exc)
            {
                AppendOnline("隧道失败：" + exc.Message);
            }
        });
    }

    private void StopP2P(object sender, RoutedEventArgs e)
    {
        if (_p2pCts is null)
        {
            return;
        }

        _p2pCts.Cancel();
        _p2pCts.Dispose();
        _p2pCts = null;
        AppendOnline("正在停止隧道...");
    }

    private void SaveDownloadSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            var modrinthApiBase = NormalizeOptionalHttpBase(ModrinthApiBaseBox.Text.Trim(), "Modrinth API");
            var modrinthCdnBase = NormalizeOptionalHttpBase(ModrinthCdnBaseBox.Text.Trim(), "Modrinth CDN");
            _settings.Config.Set("DOWNLOAD", "max_core_threads", CoreThreadsBox.Text.Trim());
            _settings.Config.Set("DOWNLOAD", "max_asset_threads", AssetThreadsBox.Text.Trim());
            _settings.Config.Set("DOWNLOAD", "speed_limit_kbps", SpeedLimitBox.Text.Trim());
            _settings.Config.Set("DOWNLOAD", "cache_strategy", ComboText(CacheStrategyBox, "reuse"));
            _settings.Config.Set("DOWNLOAD", "mirror_source", ComboText(MirrorBox, "official"));
            _settings.Config.Set("DOWNLOAD", "modrinth_api_base", modrinthApiBase);
            _settings.Config.Set("DOWNLOAD", "modrinth_cdn_base", modrinthCdnBase);
            _settings.Config.Set("UI", "theme", ComboText(ThemeBox, "dark"));
            _settings.Config.Set("UPDATE", "manifest_url", UpdateManifestBox.Text.Trim());
            _settings.Config.Set("UPDATE", "auto_check", (UpdateAutoCheckBox.IsChecked == true).ToString());
            _settings.Config.Set("HOME", "content_source", HomeContentSourceBox.Text.Trim());
            _settings.Config.Set("HOME", "allow_network", (HomeNetworkCheckBox.IsChecked == true).ToString());
            SaveMusicConfig();
            _settings.SaveConfig();
            ModrinthApiBaseBox.Text = modrinthApiBase;
            ModrinthCdnBaseBox.Text = modrinthCdnBase;
            ApplyModrinthSettings();
            StatusText.Text = "下载与更新设置已保存。";
            _ = RefreshHomeContentAsync(showFeedback: false);
        }
        catch (Exception exc)
        {
            StatusText.Text = "保存下载设置失败：" + exc.Message;
        }
    }

    private void ApplyModrinthSettings()
    {
        var apiBase = _settings.Config.Get("DOWNLOAD", "modrinth_api_base", "").Trim();
        var cdnBase = _settings.Config.Get("DOWNLOAD", "modrinth_cdn_base", "").Trim();
        _resourceMarket.ModrinthApiBase = string.IsNullOrWhiteSpace(apiBase) ? "https://api.modrinth.com/v2" : apiBase.TrimEnd('/');
        _resourceMarket.ModrinthCdnBase = string.IsNullOrWhiteSpace(cdnBase) ? "" : cdnBase.TrimEnd('/');
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

    private void ChooseHomeContentFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "文本文件|*.txt;*.md|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            HomeContentSourceBox.Text = dialog.FileName;
            _settings.Config.Set("HOME", "content_source", dialog.FileName);
            _settings.SaveConfig();
            _ = RefreshHomeContentAsync(showFeedback: true);
        }
    }

    private async void RefreshHomeContent(object sender, RoutedEventArgs e)
    {
        _settings.Config.Set("HOME", "content_source", HomeContentSourceBox.Text.Trim());
        _settings.Config.Set("HOME", "allow_network", (HomeNetworkCheckBox.IsChecked == true).ToString());
        _settings.SaveConfig();
        await RefreshHomeContentAsync(showFeedback: true);
    }

    private async Task RefreshHomeContentAsync(bool showFeedback)
    {
        try
        {
            var result = await _homeContent.LoadContentAsync(HomeContentSourceBox.Text.Trim(), HomeNetworkCheckBox.IsChecked == true);
            HomeCustomText.Text = result.Text;
            if (showFeedback)
            {
                StatusText.Text = result.FromNetwork ? "联网主页已刷新。" : "主页内容已刷新。";
            }
        }
        catch (Exception exc)
        {
            HomeCustomText.Text = "主页内容加载失败：" + exc.Message;
            if (showFeedback)
            {
                StatusText.Text = "主页内容加载失败。";
            }
        }
    }

    private void ChooseMusicFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "音频文件|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac;*.ogg|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            MusicPathBox.Text = dialog.FileName;
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
            var path = MusicPathBox.Text.Trim();
            if (MusicEnabledBox.IsChecked == true && path.Length > 0 && File.Exists(path))
            {
                _musicPlayer.Open(new Uri(path, UriKind.Absolute));
                _musicPlayer.Volume = Math.Clamp(ReadMusicVolume() / 100.0, 0, 1);
                _musicPlayer.Play();
                if (showFeedback)
                {
                    StatusText.Text = "背景音乐已播放。";
                }

                return;
            }

            _musicPlayer.Stop();
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
        _musicPlayer.Stop();
        MusicEnabledBox.IsChecked = false;
        SaveMusicConfig();
        _settings.SaveConfig();
        StatusText.Text = "背景音乐已停止。";
    }

    private void SaveMusicConfig()
    {
        _settings.Config.Set("MUSIC", "path", MusicPathBox.Text.Trim());
        _settings.Config.Set("MUSIC", "enabled", (MusicEnabledBox.IsChecked == true).ToString());
        _settings.Config.Set("MUSIC", "volume", ReadMusicVolume().ToString());
        _settings.Config.Set("MUSIC", "pause_on_launch", (MusicPauseOnLaunchBox.IsChecked == true).ToString());
    }

    private int ReadMusicVolume()
        => int.TryParse(MusicVolumeBox.Text.Trim(), out var volume) ? Math.Clamp(volume, 0, 100) : 35;

    private async void CheckForUpdates(object sender, RoutedEventArgs e)
        => await CheckForUpdatesAsync(silent: false);

    private async void DownloadUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
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

            var progress = new Progress<int>(value => StatusText.Text = $"正在下载更新：{value}%");
            var result = await _updater.DownloadUpdateAsync(
                manifest,
                progress: progress,
                status: message => Dispatcher.Invoke(() => UpdateOutputBox.Text = message));
            _lastUpdateManifest = result;
            _lastUpdatePath = result["path"]?.GetValue<string>();
            UpdateOutputBox.Text = result.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            StatusText.Text = UpdaterService.IsInstallerManifest(result)
                ? "安装器已下载，可点击“应用更新”。"
                : UpdaterService.IsExplicitLegacyExecutableManifest(result) && UpdaterService.CanReplaceCurrentExecutable()
                    ? "更新已下载，可点击“应用更新”。"
                    : "更新已下载，但清单未声明受支持的应用方式。";
        }
        catch (Exception exc)
        {
            UpdateOutputBox.Text = exc.Message;
            StatusText.Text = "下载更新失败。";
        }
    }

    private void ApplyDownloadedUpdate(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_lastUpdatePath) || !File.Exists(_lastUpdatePath))
            {
                StatusText.Text = "请先下载可用更新。";
                return;
            }

            var installerUpdate = UpdaterService.IsInstallerManifest(_lastUpdateManifest);
            var legacyExecutableUpdate = UpdaterService.IsExplicitLegacyExecutableManifest(_lastUpdateManifest);
            if (!installerUpdate && (!legacyExecutableUpdate || !UpdaterService.CanReplaceCurrentExecutable()))
            {
                UpdateOutputBox.Text = $"更新文件已下载：{_lastUpdatePath}{Environment.NewLine}清单未声明安装器更新或显式旧式可执行文件更新。";
                StatusText.Text = "此更新清单不支持自动应用。";
                return;
            }

            var prompt = installerUpdate
                ? "应用更新需要关闭 McGo，然后启动新版安装器。是否继续？"
                : "应用更新需要关闭 McGo，并在替换完成后重新启动。是否继续？";
            var answer = MessageBox.Show(this, prompt, "应用更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var result = installerUpdate
                ? UpdaterService.ScheduleInstallerLaunch(_lastUpdatePath)
                : UpdaterService.ScheduleWindowsReplaceAndRestart(_lastUpdatePath);
            UpdateOutputBox.Text = installerUpdate
                ? $"已安排启动更新安装器。日志：{result.LogPath}"
                : $"已安排更新替换。日志：{result.LogPath}{Environment.NewLine}需要管理员权限：{(result.Elevated ? "是" : "否")}";
            StatusText.Text = "正在关闭 McGo 以应用更新。";
            Application.Current.Shutdown();
        }
        catch (Exception exc)
        {
            UpdateOutputBox.Text = exc.Message;
            StatusText.Text = "应用更新失败。";
        }
    }

    private async Task AutoCheckForUpdatesAsync()
    {
        await Task.Delay(1200);
        await CheckForUpdatesAsync(silent: true);
    }

    private async Task<JsonObject?> CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var manifest = await _updater.CheckForUpdateAsync(UpdateManifestBox.Text.Trim(), AppConstants.AppVersion);
            _lastUpdateManifest = manifest;
            _lastUpdateManifestUrl = UpdateManifestBox.Text.Trim();
            var updateAvailable = manifest["update_available"]?.GetValue<bool>() == true;
            if (!silent || updateAvailable)
            {
                UpdateOutputBox.Text = manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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

    private void LoadLogs(object sender, RoutedEventArgs e)
    {
        var logsDir = Path.Combine(Environment.CurrentDirectory, "logs");
        if (!Directory.Exists(logsDir))
        {
            LogOutputBox.Text = "logs 目录不存在。";
            return;
        }

        var latest = Directory.GetFiles(logsDir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        LogOutputBox.Text = latest is null ? "没有找到日志文件。" : File.ReadAllText(latest);
    }

    private void OpenLogsDirectory(object sender, RoutedEventArgs e)
    {
        var logsDir = Path.Combine(Environment.CurrentDirectory, "logs");
        Directory.CreateDirectory(logsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", logsDir) { UseShellExecute = true });
    }

    private void FillList(ListBox list, IEnumerable<string> values)
    {
        list.Items.Clear();
        foreach (var value in values)
        {
            list.Items.Add(value);
        }
    }

    private void FillCombo(ComboBox combo, IEnumerable<string> values)
    {
        combo.Items.Clear();
        foreach (var value in values)
        {
            combo.Items.Add(value);
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void AppendDownload(string message)
    {
        DownloadLogBox.AppendText((DownloadLogBox.Text.Length == 0 ? "" : Environment.NewLine) + message);
        DownloadLogBox.ScrollToEnd();
    }

    private void AppendOnline(string message)
    {
        Dispatcher.Invoke(() =>
        {
            OnlineOutputBox.AppendText((OnlineOutputBox.Text.Length == 0 ? "" : Environment.NewLine) + message);
            OnlineOutputBox.ScrollToEnd();
        });
    }

    private void SetBusy(string message) => StatusText.Text = message;

    private static string DecodeServerList(string value)
        => value.Replace("\\n", Environment.NewLine);

    private static string EncodeServerList(string value)
        => string.Join("\\n", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private string ComboText(ComboBox combo, string fallback)
    {
        return combo.SelectedItem switch
        {
            ComboBoxItem item => item.Content?.ToString() ?? fallback,
            { } item => item.ToString() ?? fallback,
            _ => fallback,
        };
    }

    private static void SetComboText(ComboBox combo, string value)
    {
        foreach (var item in combo.Items)
        {
            var text = item is ComboBoxItem comboItem ? comboItem.Content?.ToString() : item?.ToString();
            if (string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private JsonObject? SelectedAccount() => (AccountComboBox.SelectedItem as ComboBoxItem)?.Tag as JsonObject;

    private string? SelectedAccountName()
        => SelectedAccount()?["display_name"]?.GetValue<string>() ?? SelectedAccount()?["username"]?.GetValue<string>();

    private static string AccountLabel(JsonObject account)
    {
        var type = account["type"]?.GetValue<string>() ?? "offline";
        var label = type switch
        {
            "microsoft" => "Microsoft",
            "external" => "外置登录",
            "offline" => "离线",
            _ => type,
        };
        var display = account["display_name"]?.GetValue<string>() ?? account["username"]?.GetValue<string>() ?? "未命名";
        return $"{display} ({label})";
    }

    private static Dictionary<string, object?> ToDictionary(JsonObject obj)
    {
        var result = new Dictionary<string, object?>();
        foreach (var pair in obj)
        {
            result[pair.Key] = pair.Value switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                JsonValue value when value.TryGetValue<long>(out var number) => number,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
                _ => pair.Value?.ToJsonString(),
            };
        }

        return result;
    }
}
