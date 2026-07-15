using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Capsules;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
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
        ModpackSectionBar.SelectedItem = ModpackSectionBar.Items[0];
        LoadState();
    }

    private void ModpackSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        var section = (sender.SelectedItem as SelectorBarItem)?.Tag?.ToString() ?? "import";
        ImportPanel.Visibility = section == "import" ? Visibility.Visible : Visibility.Collapsed;
        ExportPanel.Visibility = section == "export" ? Visibility.Visible : Visibility.Collapsed;
        CapsulePanel.Visibility = section == "capsule" ? Visibility.Visible : Visibility.Collapsed;
        CrashPanel.Visibility = section == "crash" ? Visibility.Visible : Visibility.Collapsed;
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
        RelayPortBox.Value = ClampPort(_settings.Config.GetInt("P2P", "relay_port", 10721), 10721);
        RoomBox.Text = _settings.Config.Get("P2P", "room", "");
        SecretBox.Password = _settings.Config.Get("P2P", "secret", "");
        HostPortBox.Value = ClampPort(_settings.Config.GetInt("P2P", "host_port", 25565), 25565);
        JoinPortBox.Value = ClampPort(_settings.Config.GetInt("P2P", "join_port", 25565), 25565);
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

            var packPath = ImportPackPathBox.Text.Trim();
            var gameDir = GameDir();
            var targetVersion = ImportTargetVersionBox.Text.Trim();
            var isolation = _settings.Config.GetBool("GAME", "enable_resource_isolation", true);
            SetPackBusy(true, "已加入全局队列，等待导入整合包...");
            PackOutputBox.Text = "已加入全局队列，等待导入整合包...";
            DownloadTaskManager.Current.Enqueue("modpack", $"导入整合包 {Path.GetFileName(packPath)}", (record, token) =>
                ImportModpackTaskAsync(record, packPath, gameDir, targetVersion, isolation, token));
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "整合包导入失败：" + exc.Message;
            PackStatusText.Text = "整合包导入失败";
            SetPackBusy(false);
        }
    }

    private async Task<string> ImportModpackTaskAsync(
        DownloadTaskRecord record,
        string packPath,
        string gameDir,
        string targetVersion,
        bool isolation,
        CancellationToken cancellationToken)
    {
        try
        {
            PackOutputBox.Text = "正在导入整合包...";
            var result = await _modpacks.ImportModpackAsync(
                packPath,
                gameDir,
                _versionSettings,
                targetVersion,
                isolation,
                message =>
                {
                    PackOutputBox.Text = message;
                    PackStatusText.Text = message;
                    DownloadTaskManager.Current.Update(record, item => item.Detail = message);
                },
                cancellationToken);
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
            return $"已导入 {result.Name}，下载 {result.FilesDownloaded} 个文件";
        }
        catch (OperationCanceledException)
        {
            PackOutputBox.Text = "整合包导入已取消。";
            PackStatusText.Text = "整合包导入已取消";
            throw;
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "整合包导入失败：" + exc.Message;
            PackStatusText.Text = "整合包导入失败";
            throw;
        }
        finally
        {
            SetPackBusy(false);
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
            PackStatusText.Text = $"胶囊已导入，待补齐 {result.MissingFiles.Count} 个文件";
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "胶囊导入失败：" + exc.Message;
            PackStatusText.Text = "胶囊导入失败";
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
            PackStatusText.Text = "胶囊导出完成";
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "胶囊导出失败：" + exc.Message;
            PackStatusText.Text = "胶囊导出失败";
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
            var config = BuildP2PConfig();
            var udpEnabled = UdpEnabled();
            var cts = new CancellationTokenSource();
            var cancellationToken = cts.Token;
            _capsuleFileHostCts = cts;
            StartCapsuleHostButton.IsEnabled = false;
            StopCapsuleHostButton.IsEnabled = true;
            PackStatusText.Text = "正在启动胶囊补包服务";
            PackOutputBox.Text = $"正在启动胶囊补包服务：{runtimeDir}";
            _ = Task.Run(async () =>
            {
                try
                {
                    await _capsuleFiles.RunHostAsync(runtimeDir, config, udpEnabled, message => DispatcherQueue.TryEnqueue(() =>
                    {
                        AppendPack(message);
                        PackStatusText.Text = message;
                    }), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AppendPack("胶囊补包服务已停止。");
                        PackStatusText.Text = "胶囊补包服务已停止";
                    });
                }
                catch (Exception exc)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        AppendPack("胶囊补包服务失败：" + exc.Message);
                        PackStatusText.Text = "胶囊补包服务失败";
                    });
                }
                finally
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (ReferenceEquals(_capsuleFileHostCts, cts))
                        {
                            _capsuleFileHostCts = null;
                            StartCapsuleHostButton.IsEnabled = true;
                            StopCapsuleHostButton.IsEnabled = false;
                        }

                        cts.Dispose();
                    });
                }
            });
            await Task.CompletedTask;
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "启动补包服务失败：" + exc.Message;
            PackStatusText.Text = "启动补包服务失败";
        }
    }

    private void PullCapsuleFiles(object sender, RoutedEventArgs e)
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

            var runtimeDir = _lastCapsuleRuntimeDir;
            var files = _lastCapsuleFiles;
            var config = BuildP2PConfig();
            var udpEnabled = UdpEnabled();
            CapsulePullProgressBar.Value = 0;
            PullCapsuleFilesButton.IsEnabled = false;
            PackStatusText.Text = "已加入全局队列，等待补齐胶囊文件";
            DownloadTaskManager.Current.Enqueue("capsule", "补齐开黑胶囊文件", (record, token) =>
                PullCapsuleFilesTaskAsync(record, runtimeDir, files, config, udpEnabled, token));
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "胶囊补包失败：" + exc.Message;
            PackStatusText.Text = "胶囊补包失败";
            PullCapsuleFilesButton.IsEnabled = true;
        }
    }

    private async Task<string> PullCapsuleFilesTaskAsync(
        DownloadTaskRecord record,
        string runtimeDir,
        JsonArray files,
        P2PTunnelConfig config,
        bool udpEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            PackStatusText.Text = "正在补齐胶囊文件";
            var progress = new Progress<int>(value =>
            {
                CapsulePullProgressBar.Value = value;
                DownloadTaskManager.Current.Update(record, item =>
                {
                    item.Progress = value;
                    item.Detail = $"补齐胶囊文件 {value}%";
                });
            });
            var result = await _capsuleFiles.PullMissingFilesAsync(
                runtimeDir,
                files,
                config,
                udpEnabled,
                progress,
                message => DispatcherQueue.TryEnqueue(() =>
                {
                    AppendPack(message);
                    DownloadTaskManager.Current.Update(record, item => item.Detail = message);
                }),
                cancellationToken);
            PackOutputBox.Text = $"胶囊补包完成：下载 {result.Downloaded}/{result.TotalMissing}，跳过 {result.Skipped}，写入 {AppFormatter.FormatBytes(result.Bytes)}";
            PackStatusText.Text = "胶囊补包完成";
            return $"胶囊补包完成：下载 {result.Downloaded}/{result.TotalMissing}";
        }
        catch (OperationCanceledException)
        {
            PackOutputBox.Text = "胶囊补包已取消。";
            PackStatusText.Text = "胶囊补包已取消";
            throw;
        }
        catch (Exception exc)
        {
            PackOutputBox.Text = "胶囊补包失败：" + exc.Message;
            PackStatusText.Text = "胶囊补包失败";
            throw;
        }
        finally
        {
            PullCapsuleFilesButton.IsEnabled = true;
        }
    }

    private void StopCapsuleFileHost(object sender, RoutedEventArgs e)
    {
        StopCapsuleHostOnly();
        AppendPack("正在停止胶囊补包服务...");
        PackStatusText.Text = "正在停止胶囊补包服务";
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
            ["relay_port"] = NumberPort(RelayPortBox, 10721),
            ["room"] = P2PUtilities.CleanRoom(RoomBox.Text),
            ["secret"] = SecretBox.Password.Trim(),
            ["host_port"] = NumberPort(HostPortBox, 25565),
            ["join_port"] = NumberPort(JoinPortBox, 25565),
        };

    private P2PTunnelConfig BuildP2PConfig()
        => new(
            RelayHostBox.Text.Trim(),
            NumberPort(RelayPortBox, 10721),
            P2PUtilities.CleanRoom(RoomBox.Text),
            SecretBox.Password.Trim(),
            NumberPort(JoinPortBox, 25565),
            NumberPort(HostPortBox, 25565));

    private void ApplyCapsuleP2P(JsonObject p2p)
    {
        if (p2p.Count == 0)
        {
            return;
        }

        RelayHostBox.Text = p2p["relay_host"]?.GetValue<string>() ?? RelayHostBox.Text;
        RelayPortBox.Value = ClampPort(p2p["relay_port"]?.GetValue<int?>() ?? NumberPort(RelayPortBox, 10721), 10721);
        RoomBox.Text = p2p["room"]?.GetValue<string>() ?? RoomBox.Text;
        SecretBox.Password = p2p["secret"]?.GetValue<string>() ?? SecretBox.Password;
        HostPortBox.Value = ClampPort(p2p["host_port"]?.GetValue<int?>() ?? NumberPort(HostPortBox, 25565), 25565);
        JoinPortBox.Value = ClampPort(p2p["join_port"]?.GetValue<int?>() ?? NumberPort(JoinPortBox, 25565), 25565);
        SaveCapsuleP2PSettings();
    }

    private void GenerateCapsuleRoom(object sender, RoutedEventArgs e)
    {
        RoomBox.Text = P2PUtilities.GenerateRoomCode();
        PackOutputBox.Text = "已生成胶囊 P2P 房间号。";
    }

    private void SaveCapsuleP2P(object sender, RoutedEventArgs e)
    {
        SaveCapsuleP2PSettings();
        PackOutputBox.Text = "胶囊 P2P 信息已保存。";
    }

    private void BuildCapsuleInvite(object sender, RoutedEventArgs e)
    {
        var room = P2PUtilities.CleanRoom(RoomBox.Text);
        if (room.Length == 0)
        {
            room = P2PUtilities.GenerateRoomCode();
            RoomBox.Text = room;
        }

        SaveCapsuleP2PSettings();
        PackOutputBox.Text = string.Join(Environment.NewLine, [
            $"中继：{RelayHostBox.Text.Trim()}:{NumberPort(RelayPortBox, 10721)}",
            $"房间：{room}",
            $"口令：{(SecretBox.Password.Trim().Length == 0 ? "无" : SecretBox.Password.Trim())}",
            $"加入者本地服务器：127.0.0.1:{NumberPort(JoinPortBox, 25565)}",
            $"胶囊补包房间：{CapsuleFileTransferService.FileRoom(room)}",
        ]);
    }

    private void SaveCapsuleP2PSettings()
    {
        _settings.Config.Set("P2P", "relay_host", RelayHostBox.Text.Trim());
        _settings.Config.Set("P2P", "relay_port", NumberPort(RelayPortBox, 10721).ToString());
        _settings.Config.Set("P2P", "room", P2PUtilities.CleanRoom(RoomBox.Text));
        _settings.Config.Set("P2P", "secret", SecretBox.Password.Trim());
        _settings.Config.Set("P2P", "host_port", NumberPort(HostPortBox, 25565).ToString());
        _settings.Config.Set("P2P", "join_port", NumberPort(JoinPortBox, 25565).ToString());
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

        var cts = _capsuleFileHostCts;
        _capsuleFileHostCts = null;
        cts.Cancel();
        cts.Dispose();
        StartCapsuleHostButton.IsEnabled = true;
        StopCapsuleHostButton.IsEnabled = false;
    }

    private void SetPackBusy(bool busy, string status = "")
    {
        ImportModpackButton.IsEnabled = !busy;
        PackProgressBar.IsIndeterminate = busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            PackStatusText.Text = status;
        }
        else if (!busy && PackStatusText.Text.StartsWith("正在", StringComparison.Ordinal))
        {
            PackStatusText.Text = "等待操作";
        }
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

    private static int NumberPort(NumberBox box, int fallback)
    {
        if (double.IsNaN(box.Value))
        {
            return fallback;
        }

        return ClampPort((int)Math.Round(box.Value), fallback);
    }

    private static int ClampPort(int value, int fallback)
        => value is >= 1 and <= 65535 ? value : fallback;

    internal void RunSmokeChecks()
    {
        if (ModpackSectionBar.Items.Count != 4)
        {
            throw new InvalidOperationException("整合包页缺少任务分段。");
        }

        foreach (var item in ModpackSectionBar.Items.OfType<SelectorBarItem>())
        {
            ModpackSectionBar.SelectedItem = item;
            var section = item.Tag?.ToString() ?? "";
            if (ImportPanel.Visibility != (section == "import" ? Visibility.Visible : Visibility.Collapsed)
                || ExportPanel.Visibility != (section == "export" ? Visibility.Visible : Visibility.Collapsed)
                || CapsulePanel.Visibility != (section == "capsule" ? Visibility.Visible : Visibility.Collapsed)
                || CrashPanel.Visibility != (section == "crash" ? Visibility.Visible : Visibility.Collapsed))
            {
                throw new InvalidOperationException($"整合包分段 {section} 的可见性不正确。");
            }
        }

        ModpackSectionBar.SelectedItem = ModpackSectionBar.Items[0];

        if (SecretBox.GetType() != typeof(PasswordBox))
        {
            throw new InvalidOperationException("胶囊口令必须使用 PasswordBox。");
        }

        if (StartCapsuleHostButton is null || StopCapsuleHostButton is null || PackStatusText is null)
        {
            throw new InvalidOperationException("整合包页缺少补包服务状态控件。");
        }
    }

    internal void SelectModpackSectionForSmoke(string section)
    {
        SelectModpackSection(section);
        ResponsiveLayoutRoot.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    internal void OpenTask(string section, string versionId, string runtimeDirectory)
    {
        SelectModpackSection(section);
        if (section == "export" && PackVersionBox.Items.Contains(versionId))
        {
            PackVersionBox.SelectedItem = versionId;
        }

        if (section == "crash")
        {
            CrashRuntimeDirBox.Text = runtimeDirectory;
        }

        ResponsiveLayoutRoot.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void SelectModpackSection(string section)
    {
        ModpackSectionBar.SelectedItem = ModpackSectionBar.Items.OfType<SelectorBarItem>()
            .First(item => string.Equals(item.Tag?.ToString(), section, StringComparison.Ordinal));
    }

}
