using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;
using McGo.Core.Capsules;
using McGo.Core.Configuration;
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
    private readonly CapsuleFileTransferService _capsuleFiles = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private CancellationTokenSource? _p2pCts;
    private CancellationTokenSource? _capsuleFileHostCts;

    public OnlinePage()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        LoadSettings();
    }

    private void LoadSettings()
    {
        _settings.LoadConfig();
        ServerListBox.Text = DecodeServerList(_settings.Config.Get("SERVERS", "items", ""));
        RelayHostBox.Text = _settings.Config.Get("P2P", "relay_host", "flyliq.cn");
        RelayPortBox.Text = _settings.Config.Get("P2P", "relay_port", "10721");
        RoomBox.Text = _settings.Config.Get("P2P", "room", "");
        SecretBox.Text = _settings.Config.Get("P2P", "secret", "");
        HostPortBox.Text = _settings.Config.Get("P2P", "host_port", "25565");
        JoinPortBox.Text = _settings.Config.Get("P2P", "join_port", "25565");
        UdpEnabledBox.IsChecked = _settings.Config.GetBool("P2P", "udp_enabled", true);
    }

    private void GenerateRoom(object sender, RoutedEventArgs e)
    {
        RoomBox.Text = P2PUtilities.GenerateRoomCode();
    }

    private void PickJoinPort(object sender, RoutedEventArgs e)
    {
        JoinPortBox.Text = P2PUtilities.PickLocalPort().ToString();
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
        try
        {
            NatOutputBox.Text = "正在通过 STUN 探测 UDP 公网映射...";
            var result = await NatUtils.DetectNatTypeAsync();
            NatOutputBox.Text = $"{result.NatType} ({result.Quality}){Environment.NewLine}{result.Summary}{Environment.NewLine}{result.Details}";
            SetP2PStatus("NAT 检测完成：" + result.NatType, 100);
        }
        catch (Exception exc)
        {
            NatOutputBox.Text = "NAT 检测失败：" + exc.Message;
            SetP2PStatus("NAT 检测失败", 0);
        }
    }

    private void CopyNatResult(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NatOutputBox.Text))
        {
            AppendOnline("没有可复制的 NAT 检测结果。");
            return;
        }

        CopyText(NatOutputBox.Text);
        AppendOnline("NAT 检测结果已复制。");
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

    private string BuildInvite()
        => string.Join(Environment.NewLine, [
            $"中继：{RelayHostBox.Text.Trim()}:{P2PUtilities.CleanPort(RelayPortBox.Text, 10721)}",
            $"房间：{P2PUtilities.CleanRoom(RoomBox.Text)}",
            $"口令：{SecretBox.Text.Trim()}",
            $"加入者连接：127.0.0.1:{P2PUtilities.CleanPort(JoinPortBox.Text)}",
            $"UDP 打洞优先：{(UdpEnabledBox.IsChecked == true ? "开启" : "关闭")}",
            $"胶囊补包房间：{CapsuleFileTransferService.FileRoom(RoomBox.Text)}",
        ]);

    private void StartP2PHost(object sender, RoutedEventArgs e)
    {
        StartP2P(async token =>
        {
            await _p2pTunnel.RunHostAsync(BuildP2PConfig(), UdpEnabledBox.IsChecked == true, "127.0.0.1", AppendOnline, token);
        }, "房主隧道已启动。");
    }

    private void StartP2PGuest(object sender, RoutedEventArgs e)
    {
        StartP2P(async token =>
        {
            await _p2pTunnel.RunGuestAsync(BuildP2PConfig(), UdpEnabledBox.IsChecked == true, AppendOnline, token);
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
        StopCurrentP2P();
        _p2pCts = new CancellationTokenSource();
        AppendOnline(message);
        SetP2PStatus(message, 35);
        _ = Task.Run(async () =>
        {
            try
            {
                await runner(_p2pCts.Token);
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
        });
    }

    private void StopP2P(object sender, RoutedEventArgs e) => StopCurrentP2P();

    private void StopCurrentP2P()
    {
        if (_p2pCts is null)
        {
            return;
        }

        _p2pCts.Cancel();
        _p2pCts.Dispose();
        _p2pCts = null;
        AppendOnline("正在停止隧道...");
        SetP2PStatus("状态：正在停止", 10);
    }

    private async void ChooseCapsuleManifest(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializePicker(picker);
        picker.FileTypeFilter.Add(".mcgo-capsule");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".zip");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            CapsuleManifestPathBox.Text = file.Path;
        }
    }

    private void StartCapsuleFileHost(object sender, RoutedEventArgs e)
    {
        try
        {
            StopCapsuleHostOnly();
            var runtimeDir = CapsuleRuntimeDirBox.Text.Trim();
            if (runtimeDir.Length == 0)
            {
                AppendOnline("请填写要提供文件的胶囊运行目录。");
                return;
            }

            _capsuleFileHostCts = new CancellationTokenSource();
            SetP2PStatus("状态：胶囊补包服务启动中", 40);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _capsuleFiles.RunHostAsync(runtimeDir, BuildP2PConfig(), UdpEnabledBox.IsChecked == true, AppendOnline, _capsuleFileHostCts.Token);
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
                    SetP2PStatus("状态：胶囊补包服务已停止", 0);
                }
            });
        }
        catch (Exception exc)
        {
            AppendOnline("启动胶囊补包服务失败：" + exc.Message);
        }
    }

    private async void PullCapsuleFiles(object sender, RoutedEventArgs e)
    {
        try
        {
            var runtimeDir = CapsuleRuntimeDirBox.Text.Trim();
            var files = ReadCapsuleFiles(CapsuleManifestPathBox.Text.Trim());
            if (runtimeDir.Length == 0 || files.Count == 0)
            {
                AppendOnline("请填写胶囊运行目录，并选择包含文件清单的胶囊。");
                return;
            }

            CapsulePullProgressBar.Value = 0;
            SetP2PStatus("状态：正在补齐胶囊文件", 45);
            var progress = new Progress<int>(value =>
            {
                CapsulePullProgressBar.Value = value;
                SetP2PStatus($"状态：补齐胶囊文件 {value}%", Math.Max(45, value));
            });
            var result = await _capsuleFiles.PullMissingFilesAsync(runtimeDir, files, BuildP2PConfig(), UdpEnabledBox.IsChecked == true, progress, AppendOnline);
            AppendOnline($"胶囊补包完成：下载 {result.Downloaded}/{result.TotalMissing}，跳过 {result.Skipped}。");
            SetP2PStatus("状态：胶囊补包完成", 100);
        }
        catch (Exception exc)
        {
            AppendOnline("胶囊补包失败：" + exc.Message + Environment.NewLine + RecoveryHint());
            SetP2PStatus("状态：胶囊补包失败", 0);
        }
    }

    private void StopCapsuleFileHost(object sender, RoutedEventArgs e)
    {
        StopCapsuleHostOnly();
        AppendOnline("正在停止胶囊补包服务...");
    }

    private void AppendOnline(string message)
    {
        void Append()
        {
            OnlineOutputBox.Text += (OnlineOutputBox.Text.Length == 0 ? "" : Environment.NewLine) + message;
            OnlineOutputBox.SelectionStart = OnlineOutputBox.Text.Length;
            if (message.Contains("已连接", StringComparison.Ordinal) || message.Contains("接入", StringComparison.Ordinal))
            {
                SetP2PStatus("状态：已连接", 100);
            }
            else if (message.Contains("等待", StringComparison.Ordinal) || message.Contains("监听", StringComparison.Ordinal))
            {
                SetP2PStatus("状态：等待连接", 65);
            }
            else if (message.Contains("回退", StringComparison.Ordinal) || message.Contains("重试", StringComparison.Ordinal))
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
            return;
        }

        _capsuleFileHostCts.Cancel();
        _capsuleFileHostCts.Dispose();
        _capsuleFileHostCts = null;
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
}
