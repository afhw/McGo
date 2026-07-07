using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace McGo.Core.Network;

public static class NatUtils
{
    public static readonly IReadOnlyList<StunServer> DefaultStunServers =
    [
        new("stun.miwifi.com", 3478),
        new("stun.l.google.com", 19302),
        new("stun.cloudflare.com", 3478),
        new("stun.ekiga.net", 3478),
    ];

    public static byte[] BuildBindingRequest(byte[] transactionId)
    {
        if (transactionId.Length != 12)
        {
            throw new ArgumentException("STUN transaction id must be 12 bytes.", nameof(transactionId));
        }

        var packet = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), 0x2112A442);
        transactionId.CopyTo(packet.AsSpan(8));
        return packet;
    }

    public static IPEndPoint? ParseStunBindingResponse(byte[] data, byte[] transactionId)
    {
        if (data.Length < 20 || transactionId.Length != 12)
        {
            return null;
        }

        var messageType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2, 2));
        var cookie = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
        if (messageType != 0x0101 || cookie != 0x2112A442 || !data.AsSpan(8, 12).SequenceEqual(transactionId))
        {
            return null;
        }

        var offset = 20;
        var end = Math.Min(data.Length, 20 + length);
        while (offset + 4 <= end)
        {
            var attrType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            var attrLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2, 2));
            offset += 4;
            if (offset + attrLength > data.Length)
            {
                break;
            }

            var value = data.AsSpan(offset, attrLength);
            var address = ParseAddressAttribute(attrType, value, transactionId);
            if (address is not null)
            {
                return address;
            }

            offset += (attrLength + 3) & ~3;
        }

        return null;
    }

    private static IPEndPoint? ParseAddressAttribute(ushort attrType, ReadOnlySpan<byte> value, byte[] transactionId)
    {
        if (value.Length < 8)
        {
            return null;
        }

        var family = value[1];
        if (family != 0x01)
        {
            return null;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2));
        var addressBytes = value.Slice(4, 4).ToArray();
        if (attrType == 0x0020)
        {
            port ^= 0x2112;
            var cookieBytes = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
            for (var index = 0; index < 4; index++)
            {
                addressBytes[index] ^= cookieBytes[index];
            }
        }
        else if (attrType != 0x0001)
        {
            return null;
        }

        return new IPEndPoint(new IPAddress(addressBytes), port);
    }

    public static async Task<NatDetectionResult> DetectNatTypeAsync(
        IReadOnlyList<StunServer>? stunServers = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        stunServers ??= DefaultStunServers;
        var samples = new List<StunSample>();
        var errors = new List<string>();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        foreach (var server in stunServers)
        {
            try
            {
                samples.Add(await QueryStunAsync(udp, server, timeout ?? TimeSpan.FromSeconds(2.5), cancellationToken));
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                errors.Add($"{server.Host}:{server.Port} - {exc.Message}");
            }
        }

        if (samples.Count == 0)
        {
            return new NatDetectionResult(
                "UDP 不可达或被防火墙阻止",
                "blocked",
                "无法从 STUN 服务器取得公网映射地址。",
                errors.Count > 0 ? string.Join(Environment.NewLine, errors) : "所有 STUN 探测均超时。",
                []);
        }

        var first = samples[0];
        var mappedIps = samples.Select(sample => sample.Mapped.Address.ToString()).ToHashSet(StringComparer.Ordinal);
        var mappedPorts = samples.Select(sample => sample.Mapped.Port).ToHashSet();
        string natType;
        string quality;
        string summary;
        if (first.Local.Address.Equals(first.Mapped.Address) && first.Local.Port == first.Mapped.Port)
        {
            natType = "开放网络或无 NAT";
            quality = "open";
            summary = "本机 UDP 地址与公网映射一致，联机连通性通常最好。";
        }
        else if (samples.Count == 1)
        {
            natType = "NAT 后网络（类型待确认）";
            quality = "unknown";
            summary = "已取得公网映射，但只有一个 STUN 样本，无法判断映射是否会随目标变化。";
        }
        else if (mappedIps.Count == 1 && mappedPorts.Count == 1)
        {
            natType = "端口保持型 NAT";
            quality = "good";
            summary = "多个 STUN 服务器返回相同公网端口，通常适合 UDP 打洞。";
        }
        else if (mappedIps.Count == 1)
        {
            natType = "对称 NAT 或地址相关映射";
            quality = "strict";
            summary = "同一本地端口访问不同目标时公网端口会变化，直连成功率较低，建议使用中继。";
        }
        else
        {
            natType = "多出口 NAT";
            quality = "strict";
            summary = "不同 STUN 服务器看到的公网 IP 不一致，建议使用中继联机。";
        }

        var details = new StringBuilder();
        details.AppendLine($"本地 UDP：{first.Local.Address}:{first.Local.Port}");
        details.AppendLine($"公网映射：{first.Mapped.Address}:{first.Mapped.Port}");
        foreach (var sample in samples)
        {
            details.AppendLine($"{sample.Server} -> {sample.Mapped.Address}:{sample.Mapped.Port} (本地 {sample.Local.Address}:{sample.Local.Port})");
        }

        if (errors.Count > 0)
        {
            details.AppendLine("失败节点：" + string.Join("；", errors));
        }

        return new NatDetectionResult(natType, quality, summary, details.ToString().Trim(), samples);
    }

    private static async Task<StunSample> QueryStunAsync(UdpClient udp, StunServer server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(server.Host, cancellationToken);
        var lastError = "";
        foreach (var address in addresses.Where(item => item.AddressFamily == AddressFamily.InterNetwork))
        {
            var endpoint = new IPEndPoint(address, server.Port);
            try
            {
                udp.Connect(endpoint);
                var transactionId = RandomNumberGenerator.GetBytes(12);
                var request = BuildBindingRequest(transactionId);
                await udp.SendAsync(request, cancellationToken);
                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                while (!linked.Token.IsCancellationRequested)
                {
                    var response = await udp.ReceiveAsync(linked.Token);
                    var mapped = ParseStunBindingResponse(response.Buffer, transactionId);
                    if (mapped is null)
                    {
                        continue;
                    }

                    return new StunSample(
                        $"{server.Host}:{server.Port}",
                        (IPEndPoint)udp.Client.LocalEndPoint!,
                        mapped);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = "请求超时";
            }
            catch (Exception exc)
            {
                lastError = exc.Message;
            }
        }

        throw new TimeoutException(lastError.Length > 0 ? lastError : $"{server.Host}:{server.Port} 无响应");
    }
}

public sealed record StunServer(string Host, int Port);

public sealed record StunSample(string Server, IPEndPoint Local, IPEndPoint Mapped);

public sealed record NatDetectionResult(string NatType, string Quality, string Summary, string Details, IReadOnlyList<StunSample> Samples);
