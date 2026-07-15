using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;
using McGo.Core.Capsules;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Formatting;
using McGo.Core.Logging;
using McGo.Core.Minecraft;
using McGo.Core.Network;
using McGo.Core.P2P;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace McGo.WinUI.Pages;

public sealed partial class OnlinePage : Page
{
    private readonly AppSettings _settings = new();
    private readonly P2PTunnel _p2pTunnel = new();
    private readonly CapsuleService _capsules = new();
    private readonly CapsuleFileTransferService _capsuleFiles = new();
    private readonly LauncherService _launcher = new();
    private readonly VersionSettingsService _versionSettingsService;
    private readonly DispatcherQueue _dispatcherQueue;
    private JsonObject _versionSettings = [];
    private CancellationTokenSource? _p2pCts;
    private CancellationTokenSource? _capsuleFileHostCts;
    private bool _loadingCapsuleVersions;
    private bool _loadingP2PRole;
    private bool _natRunning;
    private bool _capsulePackageBusy;
    private int _capsuleVersionScanRequest;

    public OnlinePage()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _versionSettingsService = new VersionSettingsService(_launcher);
        OnlineSectionBar.SelectedItem = OnlineSectionBar.Items[0];
        _loadingP2PRole = true;
        P2PRoleBar.SelectedItem = P2PRoleBar.Items[0];
        _loadingP2PRole = false;
        LoadSettings();
        ApplyP2PRole();
        Loaded += OnlinePage_Loaded;
    }

    private async void OnlinePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnlinePage_Loaded;
        await RefreshCapsuleVersionChoicesAsync();
    }

    private void OnlineSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        var section = (sender.SelectedItem as SelectorBarItem)?.Tag?.ToString() ?? "p2p";
        P2PPanel.Visibility = section == "p2p" ? Visibility.Visible : Visibility.Collapsed;
        NatPanel.Visibility = section == "nat" ? Visibility.Visible : Visibility.Collapsed;
        ServersPanel.Visibility = section == "servers" ? Visibility.Visible : Visibility.Collapsed;
        CapsulePanel.Visibility = section == "capsule" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadSettings()
    {
        _settings.LoadConfig();
        ServerListBox.Text = DecodeServerList(_settings.Config.Get("SERVERS", "items", ""));
        RelayHostBox.Text = _settings.Config.Get("P2P", "relay_host", "flyliq.cn");
        RelayPortBox.Value = ClampPort(_settings.Config.GetInt("P2P", "relay_port", 10721), 10721);
        RoomBox.Text = _settings.Config.Get("P2P", "room", "");
        SecretBox.Password = _settings.Config.Get("P2P", "secret", "");
        HostPortBox.Value = ClampPort(_settings.Config.GetInt("P2P", "host_port", 25565), 25565);
        JoinPortBox.Value = ClampPort(_settings.Config.GetInt("P2P", "join_port", 25565), 25565);
        UdpEnabledBox.IsChecked = _settings.Config.GetBool("P2P", "udp_enabled", true);
        CapsuleGameDirBox.Text = _settings.Config.Get("GAME", "directory", McGo.Core.App.AppConstants.DefaultGameDirectory);
        _versionSettings = _versionSettingsService.LoadVersionSettings();
        _loadingCapsuleVersions = true;
        CapsuleVersionBox.ItemsSource = new[] { "正在扫描本地版本..." };
        CapsuleVersionBox.SelectedIndex = 0;
        _loadingCapsuleVersions = false;
        SetP2PRunning(false);
        SetCapsuleHostRunning(false);
    }

    internal void RunSmokeChecks()
    {
        if (OnlineSectionBar.Items.Count != 4)
        {
            throw new InvalidOperationException("联机页缺少 Python 版联机任务分段。");
        }

        foreach (var item in OnlineSectionBar.Items.OfType<SelectorBarItem>())
        {
            OnlineSectionBar.SelectedItem = item;
            var section = item.Tag?.ToString() ?? "";
            if (P2PPanel.Visibility != (section == "p2p" ? Visibility.Visible : Visibility.Collapsed)
                || NatPanel.Visibility != (section == "nat" ? Visibility.Visible : Visibility.Collapsed)
                || ServersPanel.Visibility != (section == "servers" ? Visibility.Visible : Visibility.Collapsed)
                || CapsulePanel.Visibility != (section == "capsule" ? Visibility.Visible : Visibility.Collapsed))
            {
                throw new InvalidOperationException($"联机分段 {section} 的可见性不正确。");
            }
        }

        OnlineSectionBar.SelectedItem = OnlineSectionBar.Items[0];

        RoomBox.Text = "";
        var room = EnsureRoom();
        if (room.Length == 0)
        {
            throw new InvalidOperationException("P2P 房间号生成失败。");
        }

        SecretBox.Password = "smoke-secret";
        var payload = CurrentP2PInvitePayload();
        ApplyP2PInvitePayload(payload);
        if (payload["room"]?.GetValue<string>() != RoomBox.Text || SecretBox.Password != "smoke-secret")
        {
            throw new InvalidOperationException("P2P 邀请参数往返失败。");
        }

        var invite = BuildInvite();
        if (!invite.Contains(room, StringComparison.Ordinal) || !invite.Contains("127.0.0.1", StringComparison.Ordinal) || !ApplyInviteText(invite))
        {
            throw new InvalidOperationException("P2P 邀请文本生成失败。");
        }

        P2PRoleBar.SelectedItem = P2PRoleBar.Items[1];
        if (JoinPortPanel.Visibility != Visibility.Visible || HostPortPanel.Visibility != Visibility.Collapsed || StartP2PButton.Content?.ToString() != "加入房间")
        {
            throw new InvalidOperationException("P2P 加入者角色界面状态不正确。");
        }

        P2PRoleBar.SelectedItem = P2PRoleBar.Items[0];

        UpdateCapsuleRuntimeDirectory();
    }

    internal void SelectOnlineSectionForSmoke(string section)
    {
        OnlineSectionBar.SelectedItem = OnlineSectionBar.Items.OfType<SelectorBarItem>()
            .First(item => string.Equals(item.Tag?.ToString(), section, StringComparison.Ordinal));
        ResponsiveLayoutRoot.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    private void GenerateRoom(object sender, RoutedEventArgs e)
    {
        RoomBox.Text = P2PUtilities.GenerateRoomCode();
    }

    private void P2PRoleChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        if (!_loadingP2PRole)
        {
            ApplyP2PRole();
        }
    }

    private void P2PPortChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateP2PConnectionHint();

    private string SelectedP2PRole()
        => (P2PRoleBar.SelectedItem as SelectorBarItem)?.Tag?.ToString() == "guest" ? "guest" : "host";

    private void ApplyP2PRole()
    {
        var isHost = SelectedP2PRole() == "host";
        HostPortPanel.Visibility = isHost ? Visibility.Visible : Visibility.Collapsed;
        JoinPortPanel.Visibility = isHost ? Visibility.Collapsed : Visibility.Visible;
        StartP2PButton.Content = isHost ? "创建房间" : "加入房间";
        P2PRoleHintTitle.Text = isHost ? "房主操作" : "游戏内连接地址";
        UpdateP2PConnectionHint();
    }

    private void UpdateP2PConnectionHint()
    {
        if (P2PConnectionHintText is null || HostPortBox is null || JoinPortBox is null)
        {
            return;
        }

        P2PConnectionHintText.Text = SelectedP2PRole() == "host"
            ? $"开放局域网后填写端口，当前为 {NumberPort(HostPortBox, 25565)}"
            : $"在 Minecraft 中连接 127.0.0.1:{NumberPort(JoinPortBox, 25565)}";
    }

    private void PickJoinPort(object sender, RoutedEventArgs e)
    {
        JoinPortBox.Value = P2PUtilities.PickLocalPort();
        UpdateP2PConnectionHint();
    }

    private void SaveServers(object sender, RoutedEventArgs e)
    {
        _settings.Config.Set("SERVERS", "items", EncodeServerList(ServerListBox.Text));
        _settings.SaveConfig();
        AppendOnline("服务器列表已保存。");
    }

    private void CopyFirstServer(object sender, RoutedEventArgs e)
    {
        var first = ServerListBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('|', 2)[0].Trim())
            .FirstOrDefault(line => line.Length > 0);
        if (string.IsNullOrWhiteSpace(first))
        {
            AppendOnline("服务器列表为空。");
            return;
        }

        var package = new DataPackage();
        package.SetText(first);
        Clipboard.SetContent(package);
        AppendOnline("已复制服务器地址：" + first);
    }

    private async void DetectNat(object sender, RoutedEventArgs e)
    {
        if (_natRunning)
        {
            return;
        }

        _natRunning = true;
        NatDetectButton.IsEnabled = false;
        NatStatusText.Text = "NAT 类型：检测中";
        try
        {
            NatOutputBox.Text = "正在通过 STUN 探测 UDP 公网映射...";
            var result = await NatUtils.DetectNatTypeAsync();
            NatOutputBox.Text = $"{result.NatType} ({result.Quality}){Environment.NewLine}{result.Summary}{Environment.NewLine}{result.Details}";
            NatStatusText.Text = "NAT 类型：" + result.NatType;
        }
        catch (Exception exc)
        {
            NatOutputBox.Text = "NAT 检测失败：" + exc.Message;
            NatStatusText.Text = "NAT 类型：检测失败";
        }
        finally
        {
            _natRunning = false;
            NatDetectButton.IsEnabled = true;
        }
    }

    private void CopyNatResult(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NatOutputBox.Text))
        {
            AppendOnline("没有可复制的 NAT 检测结果。");
            return;
        }

        CopyText($"{NatStatusText.Text}{Environment.NewLine}{NatOutputBox.Text}");
        AppendOnline("NAT 检测结果已复制。");
    }

    private void SaveOnlineSettings(object sender, RoutedEventArgs e)
    {
        _settings.Config.Set("P2P", "relay_host", RelayHostBox.Text.Trim().Length == 0 ? "flyliq.cn" : RelayHostBox.Text.Trim());
        _settings.Config.Set("P2P", "relay_port", NumberPort(RelayPortBox, 10721).ToString());
        _settings.Config.Set("P2P", "room", EnsureRoom());
        _settings.Config.Set("P2P", "secret", SecretBox.Password.Trim());
        _settings.Config.Set("P2P", "host_port", NumberPort(HostPortBox, 25565).ToString());
        _settings.Config.Set("P2P", "join_port", NumberPort(JoinPortBox, 25565).ToString());
        _settings.Config.Set("P2P", "udp_enabled", (UdpEnabledBox.IsChecked == true).ToString());
        _settings.Config.Set("SERVERS", "items", EncodeServerList(ServerListBox.Text));
        _settings.SaveConfig();
        AppendOnline("联机设置已保存。");
    }

    private void BuildInviteText(object sender, RoutedEventArgs e)
    {
        OnlineOutputBox.Text = BuildInvite();
    }

    private void CopyInviteText(object sender, RoutedEventArgs e)
    {
        var invite = BuildInvite();
        CopyText(invite);
        OnlineOutputBox.Text = invite;
        AppendOnline("邀请信息已复制。");
    }

    private async void PasteInviteText(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                AppendOnline("剪贴板中没有邀请文本。");
                return;
            }

            var invite = await content.GetTextAsync();
            if (!ApplyInviteText(invite))
            {
                AppendOnline("未识别邀请文本，请复制完整的 McGo P2P 邀请。");
                return;
            }

            _loadingP2PRole = true;
            P2PRoleBar.SelectedItem = P2PRoleBar.Items[1];
            _loadingP2PRole = false;
            ApplyP2PRole();
            SaveOnlineSettings(this, new RoutedEventArgs());
            AppendOnline("邀请已导入，可以直接加入房间。");
        }
        catch (Exception exc)
        {
            AppendOnline("读取邀请失败：" + exc.Message);
        }
    }

    private bool ApplyInviteText(string invite)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in invite.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(['：', ':'], 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                values[parts[0]] = parts[1];
            }
        }

        if (!values.TryGetValue("房间", out var room) || P2PUtilities.CleanRoom(room).Length == 0)
        {
            return false;
        }

        RoomBox.Text = P2PUtilities.CleanRoom(room);
        if (values.TryGetValue("口令", out var secret))
        {
            SecretBox.Password = string.Equals(secret, "无", StringComparison.OrdinalIgnoreCase) ? "" : secret;
        }

        if (values.TryGetValue("中继", out var relay) && TryParseHostPort(relay, out var relayHost, out var relayPort))
        {
            RelayHostBox.Text = relayHost;
            RelayPortBox.Value = relayPort;
        }

        if (values.TryGetValue("加入者连接", out var localAddress) && TryParseHostPort(localAddress, out _, out var joinPort))
        {
            JoinPortBox.Value = joinPort;
        }

        if (values.TryGetValue("UDP 打洞优先", out var udp))
        {
            UdpEnabledBox.IsChecked = udp.Contains("开启", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool TryParseHostPort(string value, out string host, out int port)
    {
        var separator = value.LastIndexOf(':');
        host = separator > 0 ? value[..separator].Trim() : "";
        port = 0;
        return separator > 0 && int.TryParse(value[(separator + 1)..], out port) && port is >= 1 and <= 65535;
    }

    private string BuildInvite()
        => string.Join(Environment.NewLine, [
            "McGo P2P 联机",
            $"中继：{(RelayHostBox.Text.Trim().Length == 0 ? "flyliq.cn" : RelayHostBox.Text.Trim())}:{NumberPort(RelayPortBox, 10721)}",
            $"房间：{EnsureRoom()}",
            $"口令：{(SecretBox.Password.Trim().Length == 0 ? "无" : SecretBox.Password.Trim())}",
            $"加入者连接：127.0.0.1:{NumberPort(JoinPortBox, 25565)}",
            $"UDP 打洞优先：{(UdpEnabledBox.IsChecked == true ? "开启" : "关闭")}",
            $"胶囊补包房间：{CapsuleFileTransferService.FileRoom(RoomBox.Text)}",
        ]);

    private void StartSelectedP2P(object sender, RoutedEventArgs e)
    {
        if (SelectedP2PRole() == "host")
        {
            StartP2PHost(sender, e);
        }
        else
        {
            StartP2PGuest(sender, e);
        }
    }

    private void StartP2PHost(object sender, RoutedEventArgs e)
    {
        var config = BuildP2PConfig();
        var udpEnabled = UdpEnabledBox.IsChecked == true;
        if (StartP2P(async token =>
        {
            await _p2pTunnel.RunHostAsync(config, udpEnabled, "127.0.0.1", AppendOnline, token);
        }, "正在创建 P2P 房间..."))
        {
            TryStartCapsuleFileHost(quietIfUnavailable: true);
        }
    }

    private void StartP2PGuest(object sender, RoutedEventArgs e)
    {
        var config = BuildP2PConfig();
        var udpEnabled = UdpEnabledBox.IsChecked == true;
        StartP2P(async token =>
        {
            await _p2pTunnel.RunGuestAsync(config, udpEnabled, AppendOnline, token);
        }, "正在准备本地加入端口...");
    }

    private P2PTunnelConfig BuildP2PConfig()
        => new(
            RelayHostBox.Text.Trim().Length == 0 ? "flyliq.cn" : RelayHostBox.Text.Trim(),
            NumberPort(RelayPortBox, 10721),
            EnsureRoom(),
            SecretBox.Password.Trim(),
            NumberPort(JoinPortBox, 25565),
            NumberPort(HostPortBox, 25565));

    private string EnsureRoom()
    {
        var room = P2PUtilities.CleanRoom(RoomBox.Text);
        if (room.Length == 0)
        {
            room = P2PUtilities.GenerateRoomCode();
        }

        RoomBox.Text = room;
        return room;
    }

    private bool StartP2P(Func<CancellationToken, Task> runner, string message)
    {
        if (_p2pCts is not null)
        {
            AppendOnline("P2P 隧道正在运行，请先停止当前隧道。");
            return false;
        }

        SaveOnlineSettings(this, new RoutedEventArgs());
        var cts = new CancellationTokenSource();
        _p2pCts = cts;
        SetP2PRunning(true);
        AppendOnline(message);
        SetP2PStatus(message, 35);
        _ = Task.Run(async () =>
        {
            try
            {
                await runner(cts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendOnline("隧道已停止。");
                SetP2PStatus("状态：已停止", 0);
            }
            catch (Exception exc)
            {
                AppendOnline("隧道失败：" + exc.Message + Environment.NewLine + RecoveryHint());
                SetP2PStatus("状态：异常，可重试", 0);
            }
            finally
            {
                if (ReferenceEquals(_p2pCts, cts))
                {
                    _p2pCts = null;
                    StopCapsuleHostOnly();
                }

                cts.Dispose();
                SetP2PRunning(false);
            }
        });
        return true;
    }

    private void StopP2P(object sender, RoutedEventArgs e) => StopCurrentP2P();

    private void StopCurrentP2P()
    {
        StopCapsuleHostOnly();
        if (DownloadTaskManager.Current.ActiveRecord?.Kind == "capsule")
        {
            DownloadTaskManager.Current.CancelCurrent();
        }
        if (_p2pCts is null)
        {
            SetP2PRunning(false);
            return;
        }

        _p2pCts.Cancel();
        AppendOnline("正在停止隧道...");
        SetP2PStatus("状态：正在停止", 10);
    }

    private void SetP2PRunning(bool running)
    {
        void Apply()
        {
            StartP2PButton.IsEnabled = !running;
            StopP2PButton.IsEnabled = running;
            P2PRoleBar.IsEnabled = !running;
            RelayHostBox.IsEnabled = !running;
            RelayPortBox.IsEnabled = !running;
            RoomBox.IsEnabled = !running;
            SecretBox.IsEnabled = !running;
            HostPortBox.IsEnabled = !running;
            JoinPortBox.IsEnabled = !running;
            P2PPrimarySettingsGrid.Opacity = running ? 0.65 : 1;
            P2PConnectionSettings.IsEnabled = !running;
            UdpEnabledBox.IsEnabled = !running;
        }

        if (!_dispatcherQueue.TryEnqueue(Apply))
        {
            Apply();
        }
    }

    private async void RefreshCapsuleVersions(object sender, RoutedEventArgs e)
    {
        await RefreshCapsuleVersionChoicesAsync(CapsuleVersionBox.SelectedItem?.ToString());
        AppendOnline("胶囊本地版本列表已同步。");
    }

    private async Task RefreshCapsuleVersionChoicesAsync(string? selectedVersion = null)
    {
        var request = ++_capsuleVersionScanRequest;
        var gameDirectory = CapsuleGameDirBox.Text.Trim();
        JsonObject versionSettings;
        IReadOnlyList<string> versions;
        try
        {
            var result = await Task.Run(() => (
                Settings: _versionSettingsService.LoadVersionSettings(),
                Versions: (IReadOnlyList<string>)_launcher.GetLocalVersions(gameDirectory).ToList()));
            versionSettings = result.Settings;
            versions = result.Versions;
        }
        catch (Exception exc)
        {
            if (request == _capsuleVersionScanRequest)
            {
                AppendOnline("同步胶囊版本失败：" + exc.Message);
            }

            return;
        }

        if (request != _capsuleVersionScanRequest)
        {
            return;
        }

        _loadingCapsuleVersions = true;
        try
        {
            _versionSettings = versionSettings;
            CapsuleVersionBox.ItemsSource = versions.Count == 0 ? new[] { "未发现本地版本" } : versions;
            CapsuleVersionBox.SelectedItem = selectedVersion is not null && versions.Contains(selectedVersion)
                ? selectedVersion
                : CapsuleVersionBox.Items.FirstOrDefault();
        }
        finally
        {
            _loadingCapsuleVersions = false;
        }

        UpdateCapsuleRuntimeDirectory();
    }

    private void CapsuleVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingCapsuleVersions)
        {
            UpdateCapsuleRuntimeDirectory();
        }
    }

    private void UpdateCapsuleRuntimeDirectory()
    {
        var versionId = CapsuleVersionBox.SelectedItem?.ToString() ?? "";
        if (versionId.Length == 0 || versionId == "未发现本地版本" || versionId.StartsWith("正在扫描", StringComparison.Ordinal))
        {
            CapsuleRuntimeDirBox.Text = "";
            return;
        }

        CapsuleRuntimeDirBox.Text = VersionSettingsService.RuntimeDirectoryForVersion(
            CapsuleGameDirBox.Text.Trim(),
            _versionSettings,
            versionId,
            _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
    }

    private async void ChooseCapsuleManifest(object sender, RoutedEventArgs e)
    {
        var file = await PickCapsuleFileAsync();
        if (file is not null)
        {
            CapsuleManifestPathBox.Text = file.Path;
            UpdateCapsuleManifestState();
        }
    }

    private async void ImportCapsule(object sender, RoutedEventArgs e)
    {
        if (_capsulePackageBusy)
        {
            AppendOnline("胶囊导入或导出正在进行。");
            return;
        }

        SetCapsulePackageBusy(true);
        try
        {
            var capsulePath = CapsuleManifestPathBox.Text.Trim();
            if (!File.Exists(capsulePath))
            {
                var file = await PickCapsuleFileAsync();
                if (file is null)
                {
                    return;
                }

                capsulePath = file.Path;
                CapsuleManifestPathBox.Text = capsulePath;
            }

            var gameDir = CapsuleGameDirBox.Text.Trim();
            if (gameDir.Length == 0)
            {
                AppendOnline("请填写胶囊导入目标游戏目录。");
                return;
            }

            if (Path.GetExtension(capsulePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(capsulePath)) as JsonObject
                    ?? throw new InvalidOperationException("胶囊清单格式无效。");
                ApplyP2PInvitePayload(manifest["p2p"] as JsonObject ?? []);
                PullCapsuleButton.IsEnabled = (manifest["files"] as JsonArray)?.Count > 0;
                AppendOnline($"胶囊清单已加载：{manifest["name"]?.GetValue<string>() ?? Path.GetFileName(capsulePath)} | 文件 {(manifest["files"] as JsonArray)?.Count ?? 0} 项。");
                return;
            }

            var globalIsolation = _settings.Config.GetBool("GAME", "enable_resource_isolation", true);
            var result = await Task.Run(() =>
            {
                var imported = _capsules.ImportCapsule(capsulePath, gameDir, _versionSettings, globalIsolation: globalIsolation);
                _versionSettingsService.SaveVersionSettings(_versionSettings);
                return imported;
            });
            ApplyP2PInvitePayload(result.P2P);
            await RefreshCapsuleVersionChoicesAsync(result.Version);
            CapsuleRuntimeDirBox.Text = result.RuntimeDir;
            PullCapsuleButton.IsEnabled = result.Files.Count > 0;
            AppendOnline($"胶囊已导入：{result.Name} | 版本 {result.Version} | 待补齐 {result.MissingFiles.Count}/{result.Files.Count} 个文件。");
        }
        catch (Exception exc)
        {
            AppendOnline("导入开黑胶囊失败：" + exc.Message);
        }
        finally
        {
            SetCapsulePackageBusy(false);
        }
    }

    private async void ExportCapsule(object sender, RoutedEventArgs e)
    {
        if (_capsulePackageBusy)
        {
            AppendOnline("胶囊导入或导出正在进行。");
            return;
        }

        SetCapsulePackageBusy(true);
        try
        {
            var versionId = CapsuleVersionBox.SelectedItem?.ToString() ?? "";
            if (versionId.Length == 0 || versionId == "未发现本地版本")
            {
                AppendOnline("请先同步并选择要导出的本地版本。");
                return;
            }

            var picker = new FileSavePicker { SuggestedFileName = versionId + ".mcgo-capsule" };
            InitializePicker(picker);
            picker.FileTypeChoices.Add("McGo 开黑胶囊", [".mcgo-capsule"]);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var gameDirectory = CapsuleGameDirBox.Text.Trim();
            var invite = CurrentP2PInvitePayload();
            var globalIsolation = _settings.Config.GetBool("GAME", "enable_resource_isolation", true);
            var result = await Task.Run(() => _capsules.ExportCapsule(
                gameDirectory,
                _versionSettings,
                versionId,
                file.Path,
                invite,
                globalIsolation));
            CapsuleManifestPathBox.Text = file.Path;
            PullCapsuleButton.IsEnabled = result.FileCount > 0;
            AppendOnline($"胶囊已导出：{result.Name} | {result.FileCount} 个文件 | {AppFormatter.FormatBytes(result.TotalSize)} | {result.TargetPath}");
        }
        catch (Exception exc)
        {
            AppendOnline("导出开黑胶囊失败：" + exc.Message);
        }
        finally
        {
            SetCapsulePackageBusy(false);
        }
    }

    private void SetCapsulePackageBusy(bool busy)
    {
        _capsulePackageBusy = busy;
        ImportCapsuleButton.IsEnabled = !busy;
        ExportCapsuleButton.IsEnabled = !busy;
    }

    private async Task<Windows.Storage.StorageFile?> PickCapsuleFileAsync()
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add(".mcgo-capsule");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".zip");
        return await picker.PickSingleFileAsync();
    }

    private void UpdateCapsuleManifestState()
    {
        try
        {
            PullCapsuleButton.IsEnabled = ReadCapsuleFiles(CapsuleManifestPathBox.Text.Trim()).Count > 0;
        }
        catch (Exception exc)
        {
            PullCapsuleButton.IsEnabled = false;
            AppendOnline("读取胶囊清单失败：" + exc.Message);
        }
    }

    private JsonObject CurrentP2PInvitePayload()
        => new()
        {
            ["relay_host"] = RelayHostBox.Text.Trim().Length == 0 ? "flyliq.cn" : RelayHostBox.Text.Trim(),
            ["relay_port"] = NumberPort(RelayPortBox, 10721),
            ["room"] = EnsureRoom(),
            ["secret"] = SecretBox.Password.Trim(),
            ["host_port"] = NumberPort(HostPortBox, 25565),
            ["join_port"] = NumberPort(JoinPortBox, 25565),
            ["udp_enabled"] = UdpEnabledBox.IsChecked == true,
        };

    private void ApplyP2PInvitePayload(JsonObject payload)
    {
        var relayHost = payload["relay_host"]?.GetValue<string>() ?? payload["relayHost"]?.GetValue<string>() ?? "";
        if (relayHost.Length > 0)
        {
            RelayHostBox.Text = relayHost;
        }

        RelayPortBox.Value = JsonPort(payload["relay_port"], NumberPort(RelayPortBox, 10721));
        HostPortBox.Value = JsonPort(payload["host_port"], NumberPort(HostPortBox, 25565));
        JoinPortBox.Value = JsonPort(payload["join_port"], NumberPort(JoinPortBox, 25565));
        var room = payload["room"]?.GetValue<string>() ?? "";
        if (room.Length > 0)
        {
            RoomBox.Text = P2PUtilities.CleanRoom(room);
        }

        SecretBox.Password = payload["secret"]?.GetValue<string>() ?? "";
        if (payload["udp_enabled"]?.GetValue<bool?>() is bool udpEnabled)
        {
            UdpEnabledBox.IsChecked = udpEnabled;
        }

        SaveOnlineSettings(this, new RoutedEventArgs());
    }

    private void StartCapsuleFileHost(object sender, RoutedEventArgs e)
        => TryStartCapsuleFileHost(quietIfUnavailable: false);

    private bool TryStartCapsuleFileHost(bool quietIfUnavailable)
    {
        try
        {
            if (_capsuleFileHostCts is not null)
            {
                if (!quietIfUnavailable)
                {
                    AppendOnline("胶囊补包服务正在运行。");
                }

                return false;
            }

            var runtimeDir = CapsuleRuntimeDirBox.Text.Trim();
            if (runtimeDir.Length == 0 || !Directory.Exists(runtimeDir))
            {
                if (!quietIfUnavailable)
                {
                    AppendOnline("请选择存在的胶囊运行目录。");
                }

                return false;
            }

            var cts = new CancellationTokenSource();
            _capsuleFileHostCts = cts;
            var config = BuildP2PConfig();
            var udpEnabled = UdpEnabledBox.IsChecked == true;
            SetCapsuleHostRunning(true);
            AppendOnline($"胶囊补包服务启动中：{CapsuleFileTransferService.FileRoom(config.Room)}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _capsuleFiles.RunHostAsync(runtimeDir, config, udpEnabled, message => AppendOnline("胶囊补包：" + message), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    AppendOnline("胶囊补包服务已停止。");
                }
                catch (Exception exc)
                {
                    AppendOnline("胶囊补包服务失败：" + exc.Message + Environment.NewLine + RecoveryHint());
                }
                finally
                {
                    if (ReferenceEquals(_capsuleFileHostCts, cts))
                    {
                        _capsuleFileHostCts = null;
                    }

                    cts.Dispose();
                    SetCapsuleHostRunning(false);
                }
            });
            return true;
        }
        catch (Exception exc)
        {
            AppendOnline("启动胶囊补包服务失败：" + exc.Message);
            return false;
        }
    }

    private void PullCapsuleFiles(object sender, RoutedEventArgs e)
    {
        if (DownloadTaskManager.Current.ActiveRecord?.Kind == "capsule")
        {
            DownloadTaskManager.Current.CancelCurrent();
            AppendOnline("正在取消胶囊补包...");
            return;
        }

        try
        {
            var runtimeDir = CapsuleRuntimeDirBox.Text.Trim();
            var files = ReadCapsuleFiles(CapsuleManifestPathBox.Text.Trim());
            if (runtimeDir.Length == 0 || files.Count == 0)
            {
                AppendOnline("请填写胶囊运行目录，并选择包含文件清单的胶囊。");
                return;
            }

            var config = BuildP2PConfig();
            var udpEnabled = UdpEnabledBox.IsChecked == true;
            CapsulePullProgressBar.Value = 0;
            PullCapsuleButton.Content = "等待全局队列";
            PullCapsuleButton.IsEnabled = false;
            SetP2PStatus("状态：胶囊补包已加入全局队列", 40);
            DownloadTaskManager.Current.Enqueue("capsule", "补齐联机胶囊文件", (record, token) =>
                PullCapsuleFilesTaskAsync(record, runtimeDir, files, config, udpEnabled, token));
        }
        catch (Exception exc)
        {
            AppendOnline("胶囊补包失败：" + exc.Message + Environment.NewLine + RecoveryHint());
            SetP2PStatus("状态：胶囊补包失败", 0);
            PullCapsuleButton.Content = "补齐胶囊文件";
            UpdateCapsuleManifestState();
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
            PullCapsuleButton.Content = "取消补包";
            PullCapsuleButton.IsEnabled = true;
            SetP2PStatus("状态：正在补齐胶囊文件", 45);
            var progress = new Progress<int>(value =>
            {
                CapsulePullProgressBar.Value = value;
                SetP2PStatus($"状态：补齐胶囊文件 {value}%", Math.Max(45, value));
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
                message =>
                {
                    AppendOnline(message);
                    DownloadTaskManager.Current.Update(record, item => item.Detail = message);
                },
                cancellationToken);
            AppendOnline($"胶囊补包完成：下载 {result.Downloaded}/{result.TotalMissing}，跳过 {result.Skipped}。");
            SetP2PStatus("状态：胶囊补包完成", 100);
            return $"胶囊补包完成：下载 {result.Downloaded}/{result.TotalMissing}";
        }
        catch (OperationCanceledException)
        {
            AppendOnline("胶囊补包已取消。");
            SetP2PStatus("状态：胶囊补包已取消", 0);
            throw;
        }
        catch (Exception exc)
        {
            AppendOnline("胶囊补包失败：" + exc.Message + Environment.NewLine + RecoveryHint());
            SetP2PStatus("状态：胶囊补包失败", 0);
            throw;
        }
        finally
        {
            PullCapsuleButton.Content = "补齐胶囊文件";
            UpdateCapsuleManifestState();
        }
    }

    private void StopCapsuleFileHost(object sender, RoutedEventArgs e)
    {
        StopCapsuleHostOnly();
        AppendOnline("正在停止胶囊补包服务...");
    }

    private void AppendOnline(string message)
    {
        AppLog.Info("Online", message);
        void Append()
        {
            OnlineOutputBox.Text += (OnlineOutputBox.Text.Length == 0 ? "" : Environment.NewLine) + message;
            OnlineOutputBox.SelectionStart = OnlineOutputBox.Text.Length;
            var updatesMainTunnel = !message.StartsWith("胶囊补包", StringComparison.Ordinal);
            if (updatesMainTunnel && (message.Contains("已连接", StringComparison.Ordinal) || message.Contains("接入", StringComparison.Ordinal)))
            {
                SetP2PStatus("状态：已连接", 100);
            }
            else if (updatesMainTunnel && (message.Contains("等待", StringComparison.Ordinal) || message.Contains("监听", StringComparison.Ordinal)))
            {
                SetP2PStatus("状态：等待连接", 65);
            }
            else if (updatesMainTunnel && (message.Contains("回退", StringComparison.Ordinal) || message.Contains("重试", StringComparison.Ordinal)))
            {
                SetP2PStatus("状态：回退/重试中", 35);
            }
        }

        if (!_dispatcherQueue.TryEnqueue(Append))
        {
            Append();
        }
    }

    private void SetP2PStatus(string text, double progress)
    {
        void Apply()
        {
            P2PStatusText.Text = text;
            P2PStatusBar.Value = Math.Clamp(progress, 0, 100);
        }

        if (!_dispatcherQueue.TryEnqueue(Apply))
        {
            Apply();
        }
    }

    private void StopCapsuleHostOnly()
    {
        if (_capsuleFileHostCts is null)
        {
            SetCapsuleHostRunning(false);
            return;
        }

        _capsuleFileHostCts.Cancel();
    }

    private void SetCapsuleHostRunning(bool running)
    {
        void Apply()
        {
            StartCapsuleHostButton.IsEnabled = !running;
            StartCapsuleHostButton.Content = running ? "补包服务运行中" : "启动补包服务";
            StopCapsuleHostButton.IsEnabled = running;
        }

        if (!_dispatcherQueue.TryEnqueue(Apply))
        {
            Apply();
        }
    }

    private static JsonArray ReadCapsuleFiles(string path)
    {
        if (path.Length == 0 || !File.Exists(path))
        {
            return [];
        }

        JsonObject manifest;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            manifest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        else
        {
            manifest = CapsuleService.ReadCapsuleManifest(path);
        }

        return manifest["files"] as JsonArray ?? [];
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static string RecoveryHint()
        => "恢复建议：检查中继地址/端口、房间号和口令是否一致；如 UDP 不稳定，可关闭 UDP 打洞后重试 TCP 中继。";

    private static void InitializePicker(object picker)
    {
        if (App.MainWindow is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        }
    }

    private static string DecodeServerList(string value)
        => value.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);

    private static string EncodeServerList(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static int NumberPort(NumberBox box, int fallback)
    {
        if (double.IsNaN(box.Value))
        {
            return fallback;
        }

        return ClampPort((int)Math.Round(box.Value), fallback);
    }

    private static int JsonPort(JsonNode? value, int fallback)
        => int.TryParse(value?.ToString(), out var parsed) ? ClampPort(parsed, fallback) : fallback;

    private static int ClampPort(int value, int fallback)
        => value is >= 1 and <= 65535 ? value : fallback;

}
