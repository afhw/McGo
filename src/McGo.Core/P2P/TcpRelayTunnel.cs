using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace McGo.Core.P2P;

public sealed class TcpRelayTunnel
{
    public async Task RunHostAsync(
        string relayHost,
        int relayPort,
        string room,
        string secret,
        string minecraftHost,
        int minecraftPort,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            status?.Invoke("等待加入者连接...");
            using var relay = await ConnectRelayStreamAsync(relayHost, relayPort, room, secret, "host", cancellationToken);
            status?.Invoke($"加入者已连接，正在转发到 {minecraftHost}:{minecraftPort}");
            using var minecraft = new TcpClient();
            await minecraft.ConnectAsync(minecraftHost, minecraftPort, cancellationToken);
            await CopyBidirectionalAsync(relay.GetStream(), minecraft.GetStream(), cancellationToken);
            status?.Invoke("本次联机连接已断开。");
        }
    }

    public async Task RunGuestAsync(
        string relayHost,
        int relayPort,
        string room,
        string secret,
        int localPort,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, localPort);
        listener.Start();
        status?.Invoke($"加入者本地监听已启动：127.0.0.1:{localPort}");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var local = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(async () =>
                {
                    using var localClient = local;
                    try
                    {
                        status?.Invoke("Minecraft 本地客户端已连接，正在连接中继...");
                        using var relay = await ConnectRelayStreamAsync(relayHost, relayPort, room, secret, "join", cancellationToken);
                        await CopyBidirectionalAsync(localClient.GetStream(), relay.GetStream(), cancellationToken);
                    }
                    catch (Exception exc)
                    {
                        status?.Invoke("联机转发失败：" + exc.Message);
                    }
                }, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task<TcpClient> ConnectRelayStreamAsync(string relayHost, int relayPort, string room, string secret, string role, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        await client.ConnectAsync(relayHost, relayPort, cancellationToken);
        try
        {
            await PerformRelayHandshakeAsync(client.GetStream(), room, secret, role, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // 握手必须逐行读取,禁止使用带内部缓冲的 StreamReader:StreamReader 会预读
    // 并缓存 "paired" 之后紧跟到达的隧道原始字节,握手结束丢弃 reader 时这些字节
    // 会一并丢失,导致联机流开头被截断/损坏。这里按字节读到换行为止,只消费握手行。
    public static async Task PerformRelayHandshakeAsync(Stream stream, string room, string secret, string role, CancellationToken cancellationToken = default)
    {
        await ReadLineRawAsync(stream, cancellationToken); // hello
        var payload = new JsonObject
        {
            ["type"] = "stream",
            ["room"] = P2PUtilities.CleanRoom(room),
            ["secret"] = secret,
            ["role"] = role,
        };
        var requestBytes = Encoding.UTF8.GetBytes(payload.ToJsonString() + "\n");
        await stream.WriteAsync(requestBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var responseLine = await ReadLineRawAsync(stream, cancellationToken);
        var response = JsonNode.Parse(responseLine ?? "{}") as JsonObject ?? [];
        if (response["type"]?.GetValue<string>() != "paired")
        {
            throw new InvalidOperationException(response["message"]?.GetValue<string>() ?? "中继未能建立配对连接。");
        }
    }

    private static async Task<string> ReadLineRawAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            if (one[0] != (byte)'\r')
            {
                buffer.Add(one[0]);
            }

            if (buffer.Count > 64 * 1024)
            {
                throw new InvalidOperationException("中继握手响应过长。");
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static async Task CopyBidirectionalAsync(Stream left, Stream right, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leftToRight = CopyOneWayAsync(left, right, linked.Token);
        var rightToLeft = CopyOneWayAsync(right, left, linked.Token);
        await Task.WhenAny(leftToRight, rightToLeft);
        await linked.CancelAsync();
        await Task.WhenAll(Suppress(leftToRight), Suppress(rightToLeft));
    }

    private static async Task CopyOneWayAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await destination.FlushAsync(cancellationToken);
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
