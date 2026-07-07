using System.Net;
using System.Net.Sockets;

namespace McGo.Core.P2P;

public sealed class P2PTunnel
{
    private readonly TcpRelayTunnel _tcpRelayTunnel = new();

    public async Task RunHostAsync(
        P2PTunnelConfig config,
        bool udpEnabled = true,
        string minecraftHost = "127.0.0.1",
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task>
        {
            RunTcpHostFallbackAsync(config, minecraftHost, status, cancellationToken),
        };

        if (udpEnabled)
        {
            var udpPeer = new UdpTunnelPeer(stream =>
            {
                _ = Task.Run(
                    () => HandleHostUdpStreamAsync(stream, minecraftHost, config.RemotePort, status, cancellationToken),
                    cancellationToken);
            });
            tasks.Add(RunHostUdpAsync(udpPeer, config, status, cancellationToken));
        }
        else
        {
            status?.Invoke("UDP 打洞已关闭，将只使用 TCP 中继。");
        }

        await Task.WhenAll(tasks);
    }

    public async Task RunGuestAsync(
        P2PTunnelConfig config,
        bool udpEnabled = true,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, config.LocalPort);
        listener.Start();
        status?.Invoke($"加入者本地监听已启动：127.0.0.1:{config.LocalPort}");
        if (udpEnabled)
        {
            status?.Invoke("UDP 打洞已启用，Minecraft 连接时会优先尝试直连。");
        }
        else
        {
            status?.Invoke("UDP 打洞已关闭，将直接使用 TCP 中继。");
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var local = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(
                    () => HandleGuestConnectionAsync(local, config, udpEnabled, status, cancellationToken),
                    cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task BridgeUdpStreamAsync(Stream tcpStream, UdpReliableStream udpStream, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tcpToUdp = CopyTcpToUdpAsync(tcpStream, udpStream, linked.Token);
        var udpToTcp = CopyUdpToTcpAsync(udpStream, tcpStream, linked.Token);
        await Task.WhenAny(tcpToUdp, udpToTcp);
        await linked.CancelAsync();
        await Task.WhenAll(Suppress(tcpToUdp), Suppress(udpToTcp));
    }

    private async Task RunTcpHostFallbackAsync(P2PTunnelConfig config, string minecraftHost, Action<string>? status, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _tcpRelayTunnel.RunHostAsync(
                    config.RelayHost,
                    config.RelayPort,
                    config.Room,
                    config.Secret,
                    minecraftHost,
                    config.RemotePort,
                    status,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exc)
            {
                status?.Invoke("TCP 中继暂不可用，稍后重试：" + exc.Message);
                await DelayQuietly(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private static async Task RunHostUdpAsync(UdpTunnelPeer udpPeer, P2PTunnelConfig config, Action<string>? status, CancellationToken cancellationToken)
    {
        using var peer = udpPeer;
        var announced = false;
        status?.Invoke("正在注册 UDP 打洞端点...");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var peerEndpoint = await peer.RegisterUntilPeerAsync(
                    config.RelayHost,
                    config.RelayPort,
                    config.Room,
                    config.Secret,
                    "host",
                    TimeSpan.FromSeconds(4),
                    cancellationToken);
                if (peerEndpoint is not null && !announced)
                {
                    status?.Invoke($"UDP 打洞已发现加入者：{peerEndpoint.Address}:{peerEndpoint.Port}");
                    announced = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exc)
            {
                status?.Invoke("UDP 打洞注册失败，稍后重试：" + exc.Message);
            }

            await DelayQuietly(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private static async Task HandleHostUdpStreamAsync(
        UdpReliableStream stream,
        string minecraftHost,
        int minecraftPort,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        try
        {
            using var minecraft = new TcpClient();
            await minecraft.ConnectAsync(minecraftHost, minecraftPort, cancellationToken);
            status?.Invoke($"加入者已通过 UDP 打洞接入，正在转发到 {minecraftHost}:{minecraftPort}。");
            await BridgeUdpStreamAsync(minecraft.GetStream(), stream, cancellationToken);
            status?.Invoke("一条 UDP 打洞联机连接已断开。");
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            status?.Invoke("UDP 打洞房主转发失败：" + exc.Message);
        }
    }

    private async Task HandleGuestConnectionAsync(
        TcpClient local,
        P2PTunnelConfig config,
        bool udpEnabled,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        using var localClient = local;
        try
        {
            if (udpEnabled && await TryBridgeGuestUdpAsync(localClient, config, status, cancellationToken))
            {
                return;
            }

            status?.Invoke("UDP 打洞不可用，正在回退到 TCP 中继。");
            using var relay = await TcpRelayTunnel.ConnectRelayStreamAsync(
                config.RelayHost,
                config.RelayPort,
                config.Room,
                config.Secret,
                "join",
                cancellationToken);
            status?.Invoke($"已通过房间 {P2PUtilities.CleanRoom(config.Room)} 接入房主。");
            await TcpRelayTunnel.CopyBidirectionalAsync(localClient.GetStream(), relay.GetStream(), cancellationToken);
            status?.Invoke("一条本地 Minecraft 连接已断开。");
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            status?.Invoke("P2P 加入失败：" + exc.Message);
        }
    }

    private static async Task<bool> TryBridgeGuestUdpAsync(
        TcpClient localClient,
        P2PTunnelConfig config,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        using var peer = new UdpTunnelPeer();
        status?.Invoke("正在尝试 UDP 打洞连接房主...");
        var stream = await peer.ConnectStreamAsync(
            config.RelayHost,
            config.RelayPort,
            config.Room,
            config.Secret,
            cancellationToken);
        if (stream is null)
        {
            return false;
        }

        status?.Invoke("已通过 UDP 打洞接入房主。");
        await BridgeUdpStreamAsync(localClient.GetStream(), stream, cancellationToken);
        status?.Invoke("一条本地 Minecraft UDP 打洞连接已断开。");
        return true;
    }

    private static async Task CopyTcpToUdpAsync(Stream source, UdpReliableStream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested && !destination.Closed)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsSpan(0, read).ToArray(), cancellationToken);
        }

        await destination.CloseAsync(cancellationToken);
    }

    private static async Task CopyUdpToTcpAsync(UdpReliableStream source, Stream destination, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !source.Closed)
        {
            var chunk = await source.ReadAsync(cancellationToken);
            if (chunk.Length == 0)
            {
                break;
            }

            await destination.WriteAsync(chunk, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task Suppress(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }
}
