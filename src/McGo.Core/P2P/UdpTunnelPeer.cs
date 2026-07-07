using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McGo.Core.P2P;

public sealed class UdpTunnelPeer : IUdpFrameTransport, IDisposable
{
    private readonly UdpClient _udp;
    private readonly Dictionary<(ushort StreamId, string Token), UdpReliableStream> _streams = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<UdpReliableStream>? _incomingStream;
    private readonly object _registrationLock = new();
    private TaskCompletionSource<JsonObject?>? _pendingRegistration;
    private IPEndPoint? _peerEndpoint;
    private bool _started;

    public UdpTunnelPeer(Action<UdpReliableStream>? incomingStream = null)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _incomingStream = incomingStream;
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
    public IPEndPoint? PeerEndpoint => _peerEndpoint;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Task.Run(() => ReceiveLoopAsync(_stop.Token));
    }

    public async Task<IPEndPoint?> RegisterUntilPeerAsync(string relayHost, int relayPort, string room, string secret, string side, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var peer = await RegisterOnceAsync(relayHost, relayPort, room, secret, side, cancellationToken);
            if (peer is not null)
            {
                _peerEndpoint = peer;
                await SendPunchAsync(peer, cancellationToken);
                return peer;
            }

            await Task.Delay(250, cancellationToken);
        }

        return null;
    }

    public async Task<IPEndPoint?> RegisterOnceAsync(string relayHost, int relayPort, string room, string secret, string side, CancellationToken cancellationToken = default)
    {
        Start();
        var payload = JsonSerializer.Serialize(new
        {
            type = "udp_register",
            room = P2PUtilities.CleanRoom(room),
            secret,
            side = side == "join" ? "guest" : side,
        });

        var waiter = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_registrationLock)
        {
            _pendingRegistration = waiter;
        }

        await _udp.SendAsync(Encoding.UTF8.GetBytes(payload), relayHost, relayPort, cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var completed = await Task.WhenAny(waiter.Task, Task.Delay(Timeout.InfiniteTimeSpan, linked.Token));
            if (completed != waiter.Task)
            {
                return null;
            }

            var response = await waiter.Task;
            if (response?["type"]?.GetValue<string>() != "udp_registered")
            {
                return null;
            }

            var peer = response["peer"] as JsonObject;
            var host = peer?["Host"]?.GetValue<string>() ?? peer?["host"]?.GetValue<string>() ?? "";
            var port = P2PUtilities.CleanPort(peer?["Port"]?.GetValue<int?>() ?? peer?["port"]?.GetValue<int?>() ?? 0, 0);
            if (host.Length > 0 && port > 0)
            {
                return new IPEndPoint(IPAddress.Parse(host), port);
            }

            return null;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_registrationLock)
            {
                if (ReferenceEquals(_pendingRegistration, waiter))
                {
                    _pendingRegistration = null;
                }
            }
        }

    }

    public async Task<UdpReliableStream?> ConnectStreamAsync(string relayHost, int relayPort, string room, string secret, CancellationToken cancellationToken = default)
    {
        var peer = await RegisterUntilPeerAsync(relayHost, relayPort, room, secret, "guest", TimeSpan.FromSeconds(8), cancellationToken);
        if (peer is null)
        {
            return null;
        }

        var token = RandomNumberGenerator.GetBytes(8);
        var streamId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue);
        var stream = new UdpReliableStream(this, streamId, token, initiator: true);
        lock (_streams)
        {
            _streams[(streamId, Convert.ToHexString(stream.Token))] = stream;
        }

        return await stream.ConnectAsync(cancellationToken) ? stream : null;
    }

    public void Send(UdpFrame frame)
    {
        if (_peerEndpoint is not null)
        {
            var packet = frame.Pack();
            _udp.Send(packet, packet.Length, _peerEndpoint);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        _stop.Dispose();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(cancellationToken);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                continue;
            }

            var frame = UdpFrame.Unpack(result.Buffer);
            if (frame is null)
            {
                HandleJsonPacket(result);
                continue;
            }

            _peerEndpoint = result.RemoteEndPoint;
            var key = (frame.StreamId, Convert.ToHexString(UdpFrame.NormalizeToken(frame.Token)));
            UdpReliableStream? stream;
            var created = false;
            lock (_streams)
            {
                if (!_streams.TryGetValue(key, out stream) && frame.Kind == UdpFrameKind.Syn)
                {
                    stream = new UdpReliableStream(this, frame.StreamId, frame.Token, initiator: false);
                    _streams[key] = stream;
                    created = true;
                }
            }

            if (stream is not null && created)
            {
                stream.HandleFrame(frame);
                _incomingStream?.Invoke(stream);
                continue;
            }

            stream?.HandleFrame(frame);
        }
    }

    private void HandleJsonPacket(UdpReceiveResult result)
    {
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(Encoding.UTF8.GetString(result.Buffer)) as JsonObject;
        }
        catch
        {
            return;
        }

        var type = payload?["type"]?.GetValue<string>() ?? "";
        if (type == "udp_punch")
        {
            _peerEndpoint = result.RemoteEndPoint;
            return;
        }

        if (type is not ("udp_registered" or "udp_error" or "endpoint"))
        {
            return;
        }

        lock (_registrationLock)
        {
            _pendingRegistration?.TrySetResult(payload);
        }
    }

    private async Task SendPunchAsync(IPEndPoint peer, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes("""{"type":"udp_punch"}""");
        await _udp.SendAsync(bytes, peer, cancellationToken);
    }
}
