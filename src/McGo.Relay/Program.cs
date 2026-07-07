using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = RelayOptions.Parse(args);
var server = new RelayServer(options);
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    server.Stop();
};
await server.RunAsync();

internal sealed record RelayOptions(IPAddress Host, int Port, int RelayRateLimitKbps)
{
    public static RelayOptions Parse(string[] args)
    {
        var host = IPAddress.Any;
        var port = 10721;
        var relayRateLimitKbps = 8192;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            var value = index + 1 < args.Length ? args[index + 1] : "";
            switch (arg)
            {
                case "--host" when IPAddress.TryParse(value, out var parsedHost):
                    host = parsedHost;
                    index++;
                    break;
                case "--port" when int.TryParse(value, out var parsedPort):
                    port = parsedPort;
                    index++;
                    break;
                case "--relay-rate-limit-kbps" when int.TryParse(value, out var parsedLimit):
                    relayRateLimitKbps = parsedLimit;
                    index++;
                    break;
            }
        }

        return new RelayOptions(host, port, relayRateLimitKbps);
    }
}

internal sealed class RelayServer
{
    private readonly RelayOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, RelayRoomState> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _tcp;
    private UdpClient? _udp;

    public RelayServer(RelayOptions options)
    {
        _options = options;
    }

    public async Task RunAsync()
    {
        _tcp = new TcpListener(_options.Host, _options.Port);
        _udp = new UdpClient(new IPEndPoint(_options.Host, _options.Port));
        _tcp.Start();
        Console.WriteLine($"McGo relay listening on {_options.Host}:{_options.Port}");
        Console.WriteLine($"Relay rate limit: {_options.RelayRateLimitKbps} KiB/s");

        await Task.WhenAll(AcceptTcpAsync(_stop.Token), HandleUdpAsync(_stop.Token));
    }

    public void Stop()
    {
        _stop.Cancel();
        _tcp?.Stop();
        _udp?.Dispose();
    }

    private async Task AcceptTcpAsync(CancellationToken cancellationToken)
    {
        if (_tcp is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _tcp.AcceptTcpClientAsync(cancellationToken);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            _ = Task.Run(() => HandleTcpClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await WriteJsonLineAsync(writer, new JsonObject
        {
            ["type"] = "hello",
            ["server"] = "McGo.Relay",
            ["rateLimitKbps"] = _options.RelayRateLimitKbps,
        });

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            JsonObject request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject ?? [];
            }
            catch
            {
                await WriteJsonLineAsync(writer, Error("invalid_json", "请求不是有效 JSON。"));
                continue;
            }

            var streamResult = await TryHandleStreamMessageAsync(client, request, writer, cancellationToken);
            if (streamResult is not null)
            {
                return;
            }

            await WriteJsonLineAsync(writer, HandleControlMessage(request, client.Client.RemoteEndPoint));
        }
    }

    private JsonObject HandleControlMessage(JsonObject request, EndPoint? remoteEndPoint)
    {
        var type = request["type"]?.GetValue<string>() ?? "";
        if (type == "ping")
        {
            return new JsonObject { ["type"] = "pong", ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        }

        if (type is "host" or "join")
        {
            var room = CleanRoom(request["room"]?.GetValue<string>() ?? "");
            if (room.Length == 0)
            {
                return Error("missing_room", "房间号不能为空。");
            }

            if (!_rooms.TryGetValue(room, out var state))
            {
                state = new RelayRoomState(room);
                _rooms[room] = state;
            }

            if (type == "host")
            {
                state.HostEndpoint = remoteEndPoint?.ToString() ?? "";
            }
            else
            {
                state.GuestEndpoints.Add(remoteEndPoint?.ToString() ?? "");
            }

            return new JsonObject
            {
                ["type"] = "room",
                ["room"] = room,
                ["host"] = state.HostEndpoint,
                ["guests"] = state.GuestEndpoints.Count,
            };
        }

        return Error("unknown_type", "未知控制消息。");
    }

    private async Task<JsonObject?> TryHandleStreamMessageAsync(TcpClient client, JsonObject request, StreamWriter writer, CancellationToken cancellationToken)
    {
        if (request["type"]?.GetValue<string>() != "stream")
        {
            return null;
        }

        var room = CleanRoom(request["room"]?.GetValue<string>() ?? "");
        var role = request["role"]?.GetValue<string>() ?? "";
        if (room.Length == 0 || role is not ("host" or "join"))
        {
            return Error("invalid_stream", "stream 消息需要 room 和 role。");
        }

        var peer = new RelayStreamPeer(client, role);
        RelayStreamPeer? counterpart = null;
        lock (_rooms)
        {
            if (!_rooms.TryGetValue(room, out var state))
            {
                state = new RelayRoomState(room);
                _rooms[room] = state;
            }

            var ownQueue = role == "host" ? state.HostStreams : state.GuestStreams;
            var oppositeQueue = role == "host" ? state.GuestStreams : state.HostStreams;
            if (oppositeQueue.Count > 0)
            {
                counterpart = oppositeQueue.Dequeue();
            }
            else
            {
                ownQueue.Enqueue(peer);
            }
        }

        if (counterpart is null)
        {
            await peer.WaitForPairAsync(cancellationToken);
            await peer.WaitForCompletionAsync(cancellationToken);
            return new JsonObject { ["type"] = "closed" };
        }
        else
        {
            peer.Pair(counterpart);
            counterpart.Pair(peer);
        }

        await WriteJsonLineAsync(writer, new JsonObject { ["type"] = "paired", ["room"] = room, ["role"] = role });
        await counterpart.SendPairedAsync(room, cancellationToken);
        await CopyBidirectionalAsync(client.GetStream(), counterpart.Client.GetStream(), cancellationToken);
        peer.Complete();
        counterpart.Complete();
        return new JsonObject { ["type"] = "closed" };
    }

    private async Task HandleUdpAsync(CancellationToken cancellationToken)
    {
        if (_udp is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp.ReceiveAsync(cancellationToken);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            var response = JsonSerializer.Serialize(new
            {
                type = "endpoint",
                host = packet.RemoteEndPoint.Address.ToString(),
                port = packet.RemoteEndPoint.Port,
                time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            try
            {
                var payload = JsonNode.Parse(Encoding.UTF8.GetString(packet.Buffer)) as JsonObject;
                if (payload?["type"]?.GetValue<string>() == "udp_register")
                {
                    response = HandleUdpRegister(payload, packet.RemoteEndPoint);
                }
            }
            catch
            {
                // Non-JSON UDP packets are treated as simple endpoint probes.
            }
            await _udp.SendAsync(Encoding.UTF8.GetBytes(response), packet.RemoteEndPoint, cancellationToken);
        }
    }

    private string HandleUdpRegister(JsonObject payload, IPEndPoint remoteEndPoint)
    {
        var room = CleanRoom(payload["room"]?.GetValue<string>() ?? "");
        var side = payload["side"]?.GetValue<string>() ?? payload["role"]?.GetValue<string>() ?? "";
        if (room.Length == 0 || side is not ("host" or "guest" or "join"))
        {
            return JsonSerializer.Serialize(new { type = "udp_error", message = "invalid room or side" });
        }

        if (side == "join")
        {
            side = "guest";
        }

        UdpEndpointInfo? peer;
        var endpoint = new UdpEndpointInfo(remoteEndPoint.Address.ToString(), remoteEndPoint.Port);
        lock (_rooms)
        {
            if (!_rooms.TryGetValue(room, out var state))
            {
                state = new RelayRoomState(room);
                _rooms[room] = state;
            }

            if (side == "host")
            {
                state.HostUdp = endpoint;
                peer = state.GuestUdp;
            }
            else
            {
                state.GuestUdp = endpoint;
                peer = state.HostUdp;
            }
        }

        return JsonSerializer.Serialize(new
        {
            type = "udp_registered",
            room,
            side,
            endpoint,
            peer,
        });
    }

    private static async Task WriteJsonLineAsync(TextWriter writer, JsonObject payload)
        => await writer.WriteLineAsync(payload.ToJsonString());

    private static async Task CopyBidirectionalAsync(Stream left, Stream right, CancellationToken cancellationToken)
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

    private static JsonObject Error(string code, string message)
        => new() { ["type"] = "error", ["code"] = code, ["message"] = message };

    private static string CleanRoom(string value)
        => new(value.ToUpperInvariant().Where(char.IsLetterOrDigit).Take(16).ToArray());
}

internal sealed record RelayRoomState(string Room)
{
    public string HostEndpoint { get; set; } = "";
    public HashSet<string> GuestEndpoints { get; } = [];
    public Queue<RelayStreamPeer> HostStreams { get; } = [];
    public Queue<RelayStreamPeer> GuestStreams { get; } = [];
    public UdpEndpointInfo? HostUdp { get; set; }
    public UdpEndpointInfo? GuestUdp { get; set; }
}

internal sealed record UdpEndpointInfo(string Host, int Port);

internal sealed class RelayStreamPeer
{
    private readonly TaskCompletionSource<RelayStreamPeer> _paired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RelayStreamPeer(TcpClient client, string role)
    {
        Client = client;
        Role = role;
    }

    public TcpClient Client { get; }
    public string Role { get; }

    public void Pair(RelayStreamPeer counterpart) => _paired.TrySetResult(counterpart);

    public void Complete() => _completed.TrySetResult();

    public async Task<RelayStreamPeer> WaitForPairAsync(CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => _paired.TrySetCanceled(cancellationToken));
        return await _paired.Task;
    }

    public async Task WaitForCompletionAsync(CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => _completed.TrySetCanceled(cancellationToken));
        await _completed.Task;
    }

    public async Task SendPairedAsync(string room, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(Client.GetStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(new JsonObject
        {
            ["type"] = "paired",
            ["room"] = room,
            ["role"] = Role,
        }.ToJsonString().AsMemory(), cancellationToken);
    }
}
