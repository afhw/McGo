using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Authentication;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Formatting;
using McGo.Core.Java;
using McGo.Core.Minecraft;
using McGo.WinUI.Services;

namespace McGo.WinUI.Pages;

public sealed partial class LaunchPage : Page
{
    private readonly AppSettings _settings = new();
    private readonly LauncherService _launcher = new();
    private readonly VersionSettingsService _versionSettingsService;
    private readonly LaunchPreflightService _launchPreflight = new();
    private readonly DownloaderService _downloader = new();
    private readonly MicrosoftAuthenticator _microsoftAuthenticator = new(AppConstants.ClientId, AppConstants.RedirectUri);
    private JsonObject _versionSettings = [];
    private List<JsonObject> _accounts = [];
    private readonly Dictionary<string, JsonObject> _accountsByLabel = [];
    private bool _initialized;

    public LaunchPage()
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
        _accounts = _settings.LoadAccounts();
        GameDirectoryBox.Text = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        JavaPathBox.Text = _settings.Config.Get("JAVA", "path", "java");
        MemoryBox.Text = AppSettings.RecommendedMemoryMb().ToString();
        RefreshAccountsList();
        RefreshVersionList();
    }

    private void RefreshVersions(object sender, RoutedEventArgs e) => RefreshVersionList();

    private void VersionCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        RefreshVersionList();
    }

    private void RefreshVersionList()
    {
        if (VersionsList is null || GameDirectoryBox is null || VersionCategoryBox is null || OutputBox is null)
        {
            return;
        }

        var selected = VersionsList.SelectedItem?.ToString();
        var gameDir = GameDirectoryBox.Text.Trim();
        var allVersions = _launcher.GetLocalVersions(gameDir).ToList();
        var category = ComboText(VersionCategoryBox, "全部版本");
        var versions = allVersions
            .Where(versionId => VersionVisibleInCategory(gameDir, versionId, category))
            .OrderBy(versionId => VersionSettingsService.VersionSettingsEntry(_versionSettings, versionId)["favorite"]?.GetValue<bool>() == true ? 0 : 1)
            .ThenBy(versionId => versionId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        VersionsList.ItemsSource = versions;
        if (selected is not null && versions.Contains(selected))
        {
            VersionsList.SelectedItem = selected;
        }
        else if (versions.Count > 0)
        {
            VersionsList.SelectedIndex = 0;
        }

        OutputBox.Text = allVersions.Count == 0 ? "没有发现本地版本。" : $"发现 {allVersions.Count} 个本地版本，当前显示 {versions.Count} 个。";
    }

    private bool VersionVisibleInCategory(string gameDir, string versionId, string category)
    {
        var entry = VersionSettingsService.VersionSettingsEntry(_versionSettings, versionId);
        var hidden = entry["hidden"]?.GetValue<bool>() == true;
        var favorite = entry["favorite"]?.GetValue<bool>() == true;
        return category switch
        {
            "隐藏" => hidden,
            "收藏" => favorite && !hidden,
            _ => !hidden && _versionSettingsService.VersionMatchesCategory(gameDir, versionId, category),
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
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        _settings.Config.Set("GAME", "directory", GameDirectoryBox.Text.Trim());
        _settings.Config.Set("USER", "username", SelectedAccountName() ?? "");
        _settings.Config.Set("JAVA", "path", JavaPathBox.Text.Trim());
        _settings.SaveConfig();
        RefreshVersionList();
        OutputBox.Text = "设置已保存。";
    }

    private void SaveVersionSettings(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            OutputBox.Text = "请先选择一个版本。";
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
        OutputBox.Text = "版本设置已保存。";
    }

    private void CheckJavaRuntime(object sender, RoutedEventArgs e)
    {
        var javaPath = JavaPathBox.Text.Trim().Length == 0 ? "java" : JavaPathBox.Text.Trim();
        var major = JavaRuntimeService.GetJavaMajorVersion(javaPath);
        var required = VersionsList.SelectedItem is string versionId
            ? _launcher.InferRequiredJavaVersion(GameDirectoryBox.Text.Trim(), versionId)
            : 0;
        OutputBox.Text = major == 0
            ? $"无法识别 Java 版本：{javaPath}"
            : required > 0
                ? $"当前 Java：{major}{Environment.NewLine}所选版本需要：Java {required}{Environment.NewLine}{(major >= required ? "Java 版本满足要求。" : "Java 版本过低，请下载推荐 Java 或更换路径。")}"
                : $"当前 Java：{major}";
    }

    private async void DownloadRecommendedJava(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        var required = _launcher.InferRequiredJavaVersion(GameDirectoryBox.Text.Trim(), versionId);
        var installRoot = Path.Combine(GameDirectoryBox.Text.Trim(), "java_runtimes");
        try
        {
            OutputBox.Text = $"正在下载 Java {required}...";
            var progress = new Progress<JavaRuntimeDownloadProgress>(snapshot =>
            {
                var total = snapshot.TotalBytes > 0 ? AppFormatter.FormatBytes(snapshot.TotalBytes) : "未知大小";
                OutputBox.Text = $"{snapshot.Phase}：{snapshot.CurrentItem}{Environment.NewLine}{AppFormatter.FormatBytes(snapshot.DownloadedBytes)} / {total}";
            });
            var result = await JavaRuntimeService.DownloadJavaRuntimeAsync(required, installRoot, progress: progress);
            JavaPathBox.Text = result.JavaPath;
            _settings.Config.Set("JAVA", "path", result.JavaPath);
            _settings.SaveConfig();
            OutputBox.Text = string.Join(Environment.NewLine, [
                result.Reused ? $"已复用 Java {required}" : $"Java {required} 下载完成",
                $"路径：{result.JavaPath}",
                $"安装目录：{result.InstallDirectory}",
            ]);
        }
        catch (Exception exc)
        {
            OutputBox.Text = "下载 Java 失败：" + AppSettings.ConciseDownloadError(exc.Message);
        }
    }

    private async void PreviewCommand(object sender, RoutedEventArgs e)
    {
        try
        {
            var account = await RefreshSelectedAccountForLaunchAsync();
            OutputBox.Text = string.Join(Environment.NewLine, BuildSelectedLaunchCommand(account));
        }
        catch (Exception exc)
        {
            OutputBox.Text = exc.Message;
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
                OutputBox.Text = string.Join(Environment.NewLine, lines);
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
            MusicPlaybackService.PauseForLaunchIfConfigured();
            OutputBox.Text = "Minecraft 已启动。";
        }
        catch (Exception exc)
        {
            OutputBox.Text = "启动失败：" + exc.Message;
        }
    }

    private void RunPreLaunchCommand()
    {
        if (VersionsList.SelectedItem is not string versionId)
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
        if (VersionsList.SelectedItem is not string versionId)
        {
            return new LaunchPreflightResult([new LaunchPreflightIssue("version", "请先选择本地游戏版本。")], 0, 1);
        }

        return _launchPreflight.Validate(GameDirectoryBox.Text.Trim(), versionId, JavaPathBox.Text.Trim().Length == 0 ? "java" : JavaPathBox.Text.Trim(), includeAssets: true);
    }

    private async void RepairSelectedVersion(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        var gameDirectory = GameDirectoryBox.Text.Trim();
        var versionJsonPath = Path.Combine(gameDirectory, "versions", versionId, $"{versionId}.json");
        if (!File.Exists(versionJsonPath))
        {
            OutputBox.Text = $"版本 JSON 不存在：{versionJsonPath}";
            return;
        }

        try
        {
            OutputBox.Text = $"准备校验 {versionId}...";
            var versionJson = JsonNode.Parse(await File.ReadAllTextAsync(versionJsonPath)) as JsonObject
                ?? throw new InvalidOperationException("版本 JSON 无法解析。");
            var progress = new Progress<DownloadProgress>(snapshot =>
            {
                OutputBox.Text = $"[{snapshot.Phase}] {snapshot.CompletedJobs}/{snapshot.TotalJobs} reuse={snapshot.ReusedJobs} {Path.GetFileName(snapshot.CurrentFile)}";
            });
            var summary = await _downloader.RepairGameFilesAsync(versionJson, gameDirectory, versionId, "official", includeAssets: true, progress);
            OutputBox.Text = string.Join(Environment.NewLine, [
                $"补全完成：{versionId}",
                $"校验/补齐：{summary.CompletedJobs}/{summary.TotalJobs}",
                $"复用：{summary.ReusedJobs}",
                $"写入：{AppFormatter.FormatBytes(summary.DownloadedBytes)}",
                $"natives：解压 {summary.NativeFilesExtracted} 个文件到 {summary.NativesDirectory}",
            ]);
        }
        catch (Exception exc)
        {
            OutputBox.Text = "补全失败：" + AppSettings.ConciseDownloadError(exc.Message);
        }
    }

    private void OpenVersionFolder(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        OpenDirectory(Path.Combine(GameDirectoryBox.Text.Trim(), "versions", versionId), create: false);
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
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        OpenDirectory(Path.Combine(runtime, name), create: true);
    }

    private async void ExportLaunchScript(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
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
            OutputBox.Text = $"启动脚本已导出：{scriptPath}";
        }
        catch (Exception exc)
        {
            OutputBox.Text = "导出启动脚本失败：" + exc.Message;
        }
    }

    private async void DeleteSelectedVersion(object sender, RoutedEventArgs e)
    {
        if (VersionsList.SelectedItem is not string versionId)
        {
            OutputBox.Text = "请先选择一个本地版本。";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除版本",
            Content = $"确定要删除版本 {VersionSettingsService.VersionDisplayName(_versionSettings, versionId)} 吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var result = VersionManagementService.DeleteVersion(GameDirectoryBox.Text.Trim(), versionId);
            _versionSettings.Remove(versionId);
            _versionSettingsService.SaveVersionSettings(_versionSettings);
            RefreshVersionList();
            OutputBox.Text = $"版本已删除：{result.DeletedPath}";
        }
        catch (Exception exc)
        {
            OutputBox.Text = "删除版本失败：" + exc.Message;
        }
    }

    private string CurrentRuntimeDirectory()
        => VersionsList.SelectedItem is string versionId
            ? VersionSettingsService.RuntimeDirectoryForVersion(GameDirectoryBox.Text.Trim(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true))
            : "";

    private string CurrentModsDirectory()
        => VersionsList.SelectedItem is string versionId
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
        selectedAccountId ??= SelectedAccount()?["id"]?.GetValue<string>();
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
}
