using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Capsules;
using McGo.Core.Configuration;
using McGo.Core.Formatting;
using McGo.Core.Minecraft;
using McGo.Core.Modpacks;
using McGo.Core.P2P;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace McGo.WinUI.Pages;

public sealed partial class ModpacksPage : Page
{
    private readonly AppSettings _settings = new();
    private readonly LauncherService _launcher = new();
    private readonly VersionSettingsService _versionSettingsService;
    private readonly ModpackService _modpacks;
    private readonly CapsuleService _capsules;
    private readonly CapsuleFileTransferService _capsuleFiles = new();
    private JsonObject _versionSettings = [];
    private JsonArray _lastCapsuleFiles = [];
    private string _lastCapsuleRuntimeDir = "";
    private CancellationTokenSource? _capsuleFileHostCts;

    public ModpacksPage()
    {
        InitializeComponent();
        _versionSettingsService = new VersionSettingsService(_launcher);
        _modpacks = new ModpackService(_launcher);
        _capsules = new CapsuleService(_launcher, _versionSettingsService);
        LoadState();
    }

    private void LoadState()
    {
        _settings.LoadConfig();
        _versionSettings = _versionSettingsService.LoadVersionSettings();
        var versions = _launcher.GetLocalVersions(GameDir()).ToList();
        PackVersionBox.ItemsSource = versions;
        if (versions.Count > 0)
        {
            PackVersionBox.SelectedIndex = 0;
        }

        RelayHostBox.Text = _settings.Config.Get("P2P", "relay_host", "flyliq.cn");
        RelayPortBox.Text = _settings.Config.Get("P2P", "relay_port", "10721");
        RoomBox.Text = _settings.Config.Get("P2P", "room", "");
        SecretBox.Text = _settings.Config.Get("P2P", "secret", "");
        HostPortBox.Text = _settings.Config.Get("P2P", "host_port", "25565");
        JoinPortBox.Text = _settings.Config.Get("P2P", "join_port", "25565");
    }

    private void ExportModpack(object sender, RoutedEventArgs e)
    {
        try
        {
            var versionId = PackVersionBox.SelectedItem?.ToString() ?? "";
            if (versionId.Length == 0)
            {
                PackOutputBox.Text = "请先选择要导出的版本。";
                return;
            }

            var target = PackTargetBox.Text.Trim();
            if (target.Length == 0)
            {
                target = Path.Combine(Environment.CurrentDirectory, $"{versionId}.mrpack");
                PackTargetBox.Text = target;
            }

            var added = _modpacks.ExportModpack(GameDir(), _versionSettings, versionId, target, ComboText(PackFormatBox, "modrinth"), _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            PackOutputBox.Text = $"已导出 {added} 个文件：{target}";
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "导出失败：" + exc.Message;
        }
    }

    private async void ImportModpack(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await ConfirmModpackImportAsync())
            {
                PackOutputBox.Text = "已取消导入。";
                return;
            }

            PackOutputBox.Text = "正在导入整合包...";
            var result = await _modpacks.ImportModpackAsync(
                ImportPackPathBox.Text.Trim(),
                GameDir(),
                _versionSettings,
                ImportTargetVersionBox.Text.Trim(),
                _settings.Config.GetBool("GAME", "enable_resource_isolation", true),
                message => PackOutputBox.Text = message);
            _versionSettingsService.SaveVersionSettings(_versionSettings);
            LoadState();
            PackVersionBox.SelectedItem = result.Version;
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
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "整合包导入失败：" + exc.Message;
        }
    }

    private void ImportCapsule(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = _capsules.ImportCapsule(
                CapsulePathBox.Text.Trim(),
                GameDir(),
                _versionSettings,
                CapsuleTargetVersionBox.Text.Trim(),
                _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            _versionSettingsService.SaveVersionSettings(_versionSettings);
            ApplyCapsuleP2P(result.P2P);
            _lastCapsuleFiles = result.Files;
            _lastCapsuleRuntimeDir = result.RuntimeDir;
            LoadState();
            PackVersionBox.SelectedItem = result.Version;
            PackOutputBox.Text = string.Join(Environment.NewLine, [
                $"胶囊已导入：{result.Name}",
                $"版本：{result.Version}",
                $"Minecraft：{result.Minecraft}",
                $"加载器：{result.Loader}",
                $"文件清单：{result.Files.Count}",
                $"待补齐文件：{result.MissingFiles.Count}",
                $"运行目录：{result.RuntimeDir}",
            ]);
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "胶囊导入失败：" + exc.Message;
        }
    }

    private void ExportCapsule(object sender, RoutedEventArgs e)
    {
        try
        {
            var versionId = PackVersionBox.SelectedItem?.ToString() ?? "";
            if (versionId.Length == 0)
            {
                PackOutputBox.Text = "请先选择要导出的版本。";
                return;
            }

            var target = CapsulePathBox.Text.Trim();
            if (target.Length == 0)
            {
                target = Path.Combine(Environment.CurrentDirectory, $"{versionId}.mcgo-capsule");
                CapsulePathBox.Text = target;
            }

            var result = _capsules.ExportCapsule(
                GameDir(),
                _versionSettings,
                versionId,
                target,
                BuildCapsuleP2PInfo(),
                _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            PackOutputBox.Text = $"胶囊已导出：{result.TargetPath}\n文件：{result.FileCount}\n大小：{AppFormatter.FormatBytes(result.TotalSize)}";
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "胶囊导出失败：" + exc.Message;
        }
    }

    private async void StartCapsuleFileHost(object sender, RoutedEventArgs e)
    {
        try
        {
            StopCapsuleHostOnly();
            var versionId = PackVersionBox.SelectedItem?.ToString() ?? CapsuleTargetVersionBox.Text.Trim();
            if (versionId.Length == 0)
            {
                PackOutputBox.Text = "请先选择要提供补包的版本。";
                return;
            }

            var runtimeDir = VersionSettingsService.RuntimeDirectoryForVersion(GameDir(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            _capsuleFileHostCts = new CancellationTokenSource();
            PackOutputBox.Text = $"正在启动胶囊补包服务：{runtimeDir}";
            _ = Task.Run(async () =>
            {
                try
                {
                    await _capsuleFiles.RunHostAsync(runtimeDir, BuildP2PConfig(), UdpEnabled(), message => DispatcherQueue.TryEnqueue(() => AppendPack(message)), _capsuleFileHostCts.Token);
                }
                catch (OperationCanceledException)
                {
                    DispatcherQueue.TryEnqueue(() => AppendPack("胶囊补包服务已停止。"));
                }
                catch (Exception exc)
                {
                    DispatcherQueue.TryEnqueue(() => AppendPack("胶囊补包服务失败：" + exc.Message));
                }
            });
            await Task.CompletedTask;
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "启动补包服务失败：" + exc.Message;
        }
    }

    private async void PullCapsuleFiles(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_lastCapsuleFiles.Count == 0 || string.IsNullOrWhiteSpace(_lastCapsuleRuntimeDir))
            {
                if (!string.IsNullOrWhiteSpace(CapsulePathBox.Text) && File.Exists(CapsulePathBox.Text))
                {
                    var manifest = CapsuleService.ReadCapsuleManifest(CapsulePathBox.Text);
                    _lastCapsuleFiles = manifest["files"] as JsonArray ?? [];
                    var versionId = CapsuleTargetVersionBox.Text.Trim();
                    if (versionId.Length == 0)
                    {
                        versionId = manifest["name"]?.GetValue<string>() ?? manifest["sourceVersion"]?.GetValue<string>() ?? "";
                    }

                    _lastCapsuleRuntimeDir = Path.Combine(GameDir(), "versions", versionId);
                }
            }

            if (_lastCapsuleFiles.Count == 0 || string.IsNullOrWhiteSpace(_lastCapsuleRuntimeDir))
            {
                PackOutputBox.Text = "请先导入胶囊，或选择包含文件清单的胶囊。";
                return;
            }

            CapsulePullProgressBar.Value = 0;
            var progress = new Progress<int>(value => CapsulePullProgressBar.Value = value);
            var result = await _capsuleFiles.PullMissingFilesAsync(
                _lastCapsuleRuntimeDir,
                _lastCapsuleFiles,
                BuildP2PConfig(),
                UdpEnabled(),
                progress,
                message => DispatcherQueue.TryEnqueue(() => AppendPack(message)));
            PackOutputBox.Text = $"胶囊补包完成：下载 {result.Downloaded}/{result.TotalMissing}，跳过 {result.Skipped}，写入 {AppFormatter.FormatBytes(result.Bytes)}";
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "胶囊补包失败：" + exc.Message;
        }
    }

    private void StopCapsuleFileHost(object sender, RoutedEventArgs e)
    {
        StopCapsuleHostOnly();
        AppendPack("正在停止胶囊补包服务...");
    }

    private async void ChooseImportPack(object sender, RoutedEventArgs e)
    {
        var file = await PickOpenFileAsync([".mrpack", ".zip"]);
        if (file is not null)
        {
            ImportPackPathBox.Text = file.Path;
        }
    }

    private async void ChooseCapsulePath(object sender, RoutedEventArgs e)
    {
        var file = await PickOpenFileAsync([".mcgo-capsule", ".zip"]);
        if (file is not null)
        {
            CapsulePathBox.Text = file.Path;
        }
    }

    private async void ChooseCapsuleTarget(object sender, RoutedEventArgs e)
    {
        var file = await PickSaveFileAsync(".mcgo-capsule", PackVersionBox.SelectedItem?.ToString() ?? "McGoCapsule");
        if (file is not null)
        {
            CapsulePathBox.Text = file.Path;
        }
    }

    private async void ChoosePackTarget(object sender, RoutedEventArgs e)
    {
        var extension = ComboText(PackFormatBox, "modrinth") switch
        {
            "curseforge" => ".zip",
            "zip" => ".zip",
            _ => ".mrpack",
        };
        var file = await PickSaveFileAsync(extension, PackVersionBox.SelectedItem?.ToString() ?? "McGoPack");
        if (file is not null)
        {
            PackTargetBox.Text = file.Path;
        }
    }

    private void AnalyzeCrashLogs(object sender, RoutedEventArgs e)
    {
        var versionId = PackVersionBox.SelectedItem?.ToString() ?? "";
        var runtimeDir = CrashRuntimeDirBox.Text.Trim();
        if (runtimeDir.Length == 0 && versionId.Length > 0)
        {
            runtimeDir = VersionSettingsService.RuntimeDirectoryForVersion(GameDir(), _versionSettings, versionId, _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            CrashRuntimeDirBox.Text = runtimeDir;
        }

        PackOutputBox.Text = ModpackService.AnalyzeCrashLogs(GameDir(), versionId, runtimeDir);
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

    private P2PTunnelConfig BuildP2PConfig()
        => new(
            RelayHostBox.Text.Trim(),
            P2PUtilities.CleanPort(RelayPortBox.Text, 10721),
            P2PUtilities.CleanRoom(RoomBox.Text),
            SecretBox.Text.Trim(),
            P2PUtilities.CleanPort(JoinPortBox.Text),
            P2PUtilities.CleanPort(HostPortBox.Text));

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
        _settings.Config.Set("P2P", "relay_host", RelayHostBox.Text.Trim());
        _settings.Config.Set("P2P", "relay_port", RelayPortBox.Text.Trim());
        _settings.Config.Set("P2P", "room", P2PUtilities.CleanRoom(RoomBox.Text));
        _settings.Config.Set("P2P", "secret", SecretBox.Text.Trim());
        _settings.Config.Set("P2P", "host_port", P2PUtilities.CleanPort(HostPortBox.Text).ToString());
        _settings.Config.Set("P2P", "join_port", P2PUtilities.CleanPort(JoinPortBox.Text).ToString());
        _settings.SaveConfig();
    }

    private async Task<bool> ConfirmModpackImportAsync()
    {
        var preview = _modpacks.PreviewImportModpack(
            ImportPackPathBox.Text.Trim(),
            GameDir(),
            _versionSettings,
            ImportTargetVersionBox.Text.Trim(),
            _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
        if (!preview.VersionExists && preview.Conflicts.Count == 0)
        {
            return true;
        }

        var lines = new List<string>
        {
            $"目标版本：{preview.Version}",
            $"运行目录：{preview.RuntimeDir}",
            $"待解压文件：{preview.FilesToCopy}",
        };
        if (preview.VersionExists)
        {
            lines.Add("目标版本已存在，导入会更新该版本配置。");
        }

        if (preview.Conflicts.Count > 0)
        {
            lines.Add($"将覆盖 {preview.Conflicts.Count} 个已存在文件：");
            lines.AddRange(preview.Conflicts.Take(12).Select(item => "- " + item));
            if (preview.Conflicts.Count > 12)
            {
                lines.Add($"还有 {preview.Conflicts.Count - 12} 个未列出。");
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认导入整合包",
            Content = string.Join(Environment.NewLine, lines),
            PrimaryButtonText = "继续导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private bool UdpEnabled() => _settings.Config.GetBool("P2P", "udp_enabled", true);

    private void StopCapsuleHostOnly()
    {
        if (_capsuleFileHostCts is null)
        {
            return;
        }

        _capsuleFileHostCts.Cancel();
        _capsuleFileHostCts.Dispose();
        _capsuleFileHostCts = null;
    }

    private void AppendPack(string message)
    {
        PackOutputBox.Text += (PackOutputBox.Text.Length == 0 ? "" : Environment.NewLine) + message;
        PackOutputBox.SelectionStart = PackOutputBox.Text.Length;
    }

    private async Task<Windows.Storage.StorageFile?> PickOpenFileAsync(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        return await picker.PickSingleFileAsync();
    }

    private async Task<Windows.Storage.StorageFile?> PickSaveFileAsync(string extension, string suggestedName)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        InitializePicker(picker);
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);
        return await picker.PickSaveFileAsync();
    }

    private static void InitializePicker(object picker)
    {
        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }
    }

    private string GameDir()
        => _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);

    private static string ComboText(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? fallback;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }
}
