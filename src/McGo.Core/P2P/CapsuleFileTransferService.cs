using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using McGo.Core.Capsules;
using McGo.Core.Formatting;
using McGo.Core.Utilities;

namespace McGo.Core.P2P;

public sealed class CapsuleFileTransferService
{
    public const string FileRoomSuffix = "-FILES";
    private const int BufferSize = 256 * 1024;
    private readonly P2PTunnel _p2pTunnel = new();

    public static string FileRoom(string room) => P2PUtilities.CleanRoom(room) + FileRoomSuffix;

    public async Task RunHostAsync(
        string runtimeDir,
        P2PTunnelConfig config,
        bool udpEnabled,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        runtimeDir = Path.GetFullPath(runtimeDir);
        if (!Directory.Exists(runtimeDir))
        {
            throw new DirectoryNotFoundException("胶囊运行目录不存在：" + runtimeDir);
        }

        var localPort = P2PUtilities.PickLocalPort();
        var listener = new TcpListener(IPAddress.Loopback, localPort);
        listener.Start();
        var fileConfig = config with
        {
            Room = FileRoom(config.Room),
            RemotePort = localPort,
        };
        status?.Invoke($"胶囊补包服务正在启动：房间 {fileConfig.Room}");
        var tunnelTask = _p2pTunnel.RunHostAsync(fileConfig, udpEnabled, "127.0.0.1", status, cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(
                    () => HandleHostClientAsync(client, runtimeDir, status, cancellationToken),
                    cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
            await SuppressAsync(tunnelTask);
            status?.Invoke("胶囊补包服务已停止。");
        }
    }

    public async Task<CapsulePullResult> PullMissingFilesAsync(
        string runtimeDir,
        JsonArray files,
        P2PTunnelConfig config,
        bool udpEnabled,
        IProgress<int>? progress = null,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        runtimeDir = Path.GetFullPath(runtimeDir);
        Directory.CreateDirectory(runtimeDir);
        var missing = CapsuleService.CapsuleMissingFiles(runtimeDir, files);
        if (missing.Count == 0)
        {
            return new CapsulePullResult(0, files.Count, 0, 0);
        }

        var joinPort = P2PUtilities.PickLocalPort();
        var fileConfig = config with
        {
            Room = FileRoom(config.Room),
            LocalPort = joinPort,
        };
        using var tunnelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tunnelTask = _p2pTunnel.RunGuestAsync(fileConfig, udpEnabled, status, tunnelCts.Token);
        var downloaded = 0;
        long totalBytes = 0;

        try
        {
            for (var index = 0; index < missing.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = missing[index];
                var relative = item["path"]?.GetValue<string>() ?? "";
                var expectedSha256 = item["sha256"]?.GetValue<string>() ?? "";
                status?.Invoke($"正在补包 {index + 1}/{missing.Count}：{relative}");
                var target = CapsuleService.CapsuleTargetPath(runtimeDir, relative);
                var tempTarget = target + ".mcgo-download";
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    await using (var output = File.Create(tempTarget))
                    using (var client = await ConnectWithRetryAsync(IPAddress.Loopback, joinPort, TimeSpan.FromSeconds(12), cancellationToken))
                    {
                        var stream = client.GetStream();
                        await WriteJsonLineAsync(stream, new JsonObject
                        {
                            ["type"] = "get",
                            ["path"] = relative,
                            ["sha256"] = expectedSha256,
                        }, cancellationToken);

                        var response = await ReadJsonLineAsync(stream, cancellationToken);
                        if (response["type"]?.GetValue<string>() != "ok")
                        {
                            throw new InvalidOperationException(response["message"]?.GetValue<string>() ?? $"补包失败：{relative}");
                        }

                        var size = response["size"]?.GetValue<long>() ?? 0;
                        var remaining = size;
                        var buffer = new byte[BufferSize];
                        while (remaining > 0)
                        {
                            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, remaining)), cancellationToken);
                            if (read <= 0)
                            {
                                throw new IOException("补包连接中断：" + relative);
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            remaining -= read;
                            totalBytes += read;
                        }
                    }

                    var actualSha256 = FileUtilities.Sha256File(tempTarget);
                    if (!string.IsNullOrWhiteSpace(expectedSha256) && !actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("补包文件校验失败：" + relative);
                    }

                    File.Move(tempTarget, target, overwrite: true);
                }
                catch
                {
                    // 任意失败（连接中断、校验不符、取消）都清理半成品，避免遗留 .mcgo-download 垃圾文件。
                    TryDeleteFile(tempTarget);
                    throw;
                }

                downloaded++;
                progress?.Report((int)Math.Round((index + 1) * 100.0 / missing.Count));
            }

            status?.Invoke($"胶囊补包完成：{downloaded}/{missing.Count}，写入 {AppFormatter.FormatBytes(totalBytes)}");
            return new CapsulePullResult(downloaded, files.Count - missing.Count, missing.Count, totalBytes);
        }
        finally
        {
            await tunnelCts.CancelAsync();
            await SuppressAsync(tunnelTask);
        }
    }

    private static async Task HandleHostClientAsync(TcpClient client, string runtimeDir, Action<string>? status, CancellationToken cancellationToken)
    {
        using var tcp = client;
        try
        {
            var stream = tcp.GetStream();
            var request = await ReadJsonLineAsync(stream, cancellationToken);
            if (request["type"]?.GetValue<string>() != "get")
            {
                await WriteJsonLineAsync(stream, new JsonObject { ["type"] = "error", ["message"] = "unknown request" }, cancellationToken);
                return;
            }

            var relative = request["path"]?.GetValue<string>() ?? "";
            var expectedSha256 = request["sha256"]?.GetValue<string>() ?? "";
            var path = CapsuleService.CapsuleTargetPath(runtimeDir, relative);
            if (!File.Exists(path))
            {
                await WriteJsonLineAsync(stream, new JsonObject { ["type"] = "error", ["message"] = "file not found" }, cancellationToken);
                return;
            }

            var actualSha256 = FileUtilities.Sha256File(path);
            if (!string.IsNullOrWhiteSpace(expectedSha256) && !actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonLineAsync(stream, new JsonObject { ["type"] = "error", ["message"] = "file sha256 mismatch on host" }, cancellationToken);
                return;
            }

            var size = new FileInfo(path).Length;
            await WriteJsonLineAsync(stream, new JsonObject
            {
                ["type"] = "ok",
                ["size"] = size,
                ["sha256"] = actualSha256,
            }, cancellationToken);

            await using var input = File.OpenRead(path);
            await input.CopyToAsync(stream, BufferSize, cancellationToken);
            status?.Invoke("已发送胶囊文件：" + relative);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            try
            {
                await WriteJsonLineAsync(client.GetStream(), new JsonObject { ["type"] = "error", ["message"] = exc.Message }, CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private static async Task<TcpClient> ConnectWithRetryAsync(IPAddress host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port, cancellationToken);
                return client;
            }
            catch (Exception exc) when (exc is SocketException or IOException)
            {
                client.Dispose();
                last = exc;
                await Task.Delay(150, cancellationToken);
            }
        }

        throw new InvalidOperationException("无法连接胶囊补包通道：" + last?.Message);
    }

    private static async Task<JsonObject> ReadJsonLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read <= 0)
            {
                throw new IOException("connection closed");
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            buffer.Add(one[0]);
            if (buffer.Count > 16 * 1024)
            {
                throw new InvalidOperationException("response line is too large");
            }
        }

        return JsonNode.Parse(Encoding.UTF8.GetString(buffer.ToArray())) as JsonObject ?? [];
    }

    private static async Task WriteJsonLineAsync(Stream stream, JsonObject payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString() + "\n");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task SuppressAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed record CapsulePullResult(int Downloaded, int Skipped, int TotalMissing, long Bytes);
