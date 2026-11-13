using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using McGo.Core.Downloads;
using McGo.Core.Installation;

internal static class DownloadReliabilityTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("complete-download-payload");
    private const string DownloadUrl = "https://libraries.minecraft.net/example/demo/1/demo-1.jar";

    public static void Register(TestRunner runner)
    {
        Add(runner, "Downloader retries real HttpClient timeouts before success", CoreTimeoutThenSuccessAsync);
        Add(runner, "Downloader timeout exhaustion faults instead of returning 0/1 success", CoreTimeoutExhaustionAsync);
        Add(runner, "Downloader user cancellation during headers is not retried", () => CancelHeadersAsync(installation: false));
        Add(runner, "Downloader retries after a partial body disconnect", CoreDisconnectThenSuccessAsync);
        Add(runner, "Downloader rejects silently truncated Content-Length", () => TruncatedBodyAsync(installation: false));
        Add(runner, "Downloader body cancellation preserves the target and removes temporary files", () => CancelBodyAsync(installation: false));
        Add(runner, "Installer falls back after a real HttpClient timeout", () => InstallationTimeoutAsync(exhaust: false));
        Add(runner, "Installer exhausted timeouts fault instead of canceling", () => InstallationTimeoutAsync(exhaust: true));
        Add(runner, "Installer user cancellation during headers skips fallback", () => CancelHeadersAsync(installation: true));
        Add(runner, "Installer falls back after a partial body disconnect", InstallationDisconnectThenSuccessAsync);
        Add(runner, "Installer rejects silently truncated Content-Length", () => TruncatedBodyAsync(installation: true));
        Add(runner, "Installer body cancellation preserves the target and removes temporary files", () => CancelBodyAsync(installation: true));
        Add(runner, "Profile retries never reuse partial libraries without metadata", ProfileRetryAsync);
        Add(runner, "Profile libraries validate existing and downloaded SHA-1", ProfileHashValidationAsync);
        Add(runner, "Profile libraries validate metadata size without Content-Length", ProfileSizeValidationAsync);
        Add(runner, "Concurrent installer downloads use independent temporary files", ConcurrentInstallationDownloadsAsync);
        runner.Add("Missing-file inspection honors cancellation even for empty inputs", InspectionCancellation);
    }

    private static void Add(TestRunner runner, string name, Func<Task> test)
        => runner.Add(name, () => test().GetAwaiter().GetResult());

    private static async Task CoreTimeoutThenSuccessAsync()
    {
        using var temp = TempDir.Create();
        using var handler = new AttemptHandler(async (_, attempt, token) =>
        {
            if (attempt < 3) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Payload);
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var service = new DownloaderService(client);
        var job = Job(Path.Combine(temp.Path, "library.jar"));

        var summary = await service.DownloadJobsAsync([job]);

        Assert.Equal(3, handler.Attempts);
        Assert.Equal(1, summary.TotalJobs);
        Assert.Equal(1, summary.CompletedJobs);
        Assert.Equal(0, summary.ReusedJobs);
        Assert.Equal((long)Payload.Length, summary.DownloadedBytes);
        Assert.SequenceEqual(Payload, File.ReadAllBytes(job.TargetPath));
        var reused = await service.DownloadJobsAsync([job]);
        Assert.Equal(1, reused.ReusedJobs);
        Assert.Equal(0L, reused.DownloadedBytes);
        Assert.Equal(3, handler.Attempts);
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task CoreTimeoutExhaustionAsync()
    {
        using var temp = TempDir.Create();
        using var handler = new AttemptHandler(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Payload);
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var target = Path.Combine(temp.Path, "library.jar");
        var task = new DownloaderService(client).DownloadJobsAsync([Job(target)]);

        var error = await ExpectFailureAsync<TimeoutException>(task);

        Assert.True(error.InnerException is OperationCanceledException);
        Assert.True(task.IsFaulted);
        Assert.Equal(3, handler.Attempts);
        Assert.False(File.Exists(target));
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task CancelHeadersAsync(bool installation)
    {
        using var temp = TempDir.Create();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AttemptHandler(async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Payload);
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var target = Path.Combine(temp.Path, "library.jar");
        Task task = installation
            ? new InstallServices(client).StreamDownloadAsync(DownloadUrl, target, cancellationToken: cancellation.Token)
            : new DownloaderService(client).DownloadJobsAsync([Job(target)], cancellationToken: cancellation.Token);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
        }

        await ExpectFailureAsync<OperationCanceledException>(task);
        Assert.True(task.IsCanceled);
        Assert.Equal(1, handler.Attempts);
        Assert.False(File.Exists(target));
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task CoreDisconnectThenSuccessAsync()
    {
        using var temp = TempDir.Create();
        var streams = new List<BodyStream>();
        using var handler = new AttemptHandler((_, attempt, _) =>
        {
            var stream = new BodyStream(attempt < 3 ? Payload[..4] : Payload, disconnect: attempt < 3);
            streams.Add(stream);
            return Task.FromResult(Response(stream, Payload.Length));
        });
        using var client = new HttpClient(handler);
        var target = Path.Combine(temp.Path, "library.jar");

        var summary = await new DownloaderService(client).DownloadJobsAsync([Job(target)]);

        Assert.Equal(3, handler.Attempts);
        Assert.Equal(1, summary.CompletedJobs);
        Assert.Equal((long)Payload.Length, summary.DownloadedBytes);
        Assert.SequenceEqual(Payload, File.ReadAllBytes(target));
        Assert.True(streams.All(stream => stream.Disposed));
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task TruncatedBodyAsync(bool installation)
    {
        using var temp = TempDir.Create();
        using var handler = new AttemptHandler((_, _, _) => Task.FromResult(Response(new BodyStream(Payload[..4]), Payload.Length)));
        using var client = new HttpClient(handler);
        var target = Path.Combine(temp.Path, "library.jar");
        // No job metadata: the response Content-Length must detect silent EOF.
        Task task = installation
            ? new InstallServices(client).StreamDownloadAsync(DownloadUrl, target)
            : new DownloaderService(client).DownloadJobsAsync([new DownloadJob(DownloadUrl, target, "", 0, "library")]);

        await ExpectFailureAsync<IOException>(task);

        Assert.Equal(installation ? 2 : 3, handler.Attempts);
        Assert.False(File.Exists(target));
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task CancelBodyAsync(bool installation)
    {
        using var temp = TempDir.Create();
        using var cancellation = new CancellationTokenSource();
        var original = Encoding.UTF8.GetBytes("keep-original-file");
        var target = Path.Combine(temp.Path, "library.jar");
        File.WriteAllBytes(target, original);
        var stream = new BodyStream(Payload[..4], wait: true);
        using var handler = new AttemptHandler((_, _, _) => Task.FromResult(Response(stream, Payload.Length)));
        using var client = new HttpClient(handler);
        var snapshots = new List<DownloadSnapshot>();
        var progress = new SynchronousProgress<DownloadSnapshot>(snapshots.Add);
        Task task = installation
            ? new InstallServices(client).StreamDownloadAsync(DownloadUrl, target, progress, cancellationToken: cancellation.Token)
            : new DownloaderService(client).DownloadJobsAsync([Job(target)], cancellationToken: cancellation.Token);

        try
        {
            await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.SequenceEqual(original, File.ReadAllBytes(target));
        }
        finally
        {
            cancellation.Cancel();
        }

        await ExpectFailureAsync<OperationCanceledException>(task);
        Assert.Equal(1, handler.Attempts);
        Assert.True(stream.Disposed);
        Assert.SequenceEqual(original, File.ReadAllBytes(target));
        Assert.False(snapshots.Any(snapshot => snapshot.CompletedFiles > 0));
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task InstallationTimeoutAsync(bool exhaust)
    {
        using var temp = TempDir.Create();
        using var handler = new AttemptHandler(async (_, attempt, token) =>
        {
            if (exhaust || attempt == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Payload);
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var target = Path.Combine(temp.Path, "library.jar");
        var task = new InstallServices(client).StreamDownloadAsync(DownloadUrl, target);
        if (exhaust)
        {
            var error = await ExpectFailureAsync<TimeoutException>(task);
            Assert.True(error.InnerException is OperationCanceledException);
            Assert.True(task.IsFaulted);
            Assert.False(File.Exists(target));
        }
        else
        {
            await task;
            Assert.SequenceEqual(Payload, File.ReadAllBytes(target));
        }

        Assert.Equal(2, handler.Attempts);
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task InstallationDisconnectThenSuccessAsync()
    {
        using var temp = TempDir.Create();
        var original = Encoding.UTF8.GetBytes("original-file");
        var target = Path.Combine(temp.Path, "library.jar");
        File.WriteAllBytes(target, original);
        var streams = new List<BodyStream>();
        var hosts = new List<string>();
        using var handler = new AttemptHandler((request, attempt, _) =>
        {
            Assert.SequenceEqual(original, File.ReadAllBytes(target));
            AssertNoTemporaryFiles(temp.Path);
            hosts.Add(request.RequestUri!.Host);
            var stream = new BodyStream(attempt == 1 ? Payload[..4] : Payload, disconnect: attempt == 1);
            streams.Add(stream);
            return Task.FromResult(Response(stream, Payload.Length));
        });
        using var client = new HttpClient(handler);
        var snapshots = new List<DownloadSnapshot>();

        await new InstallServices(client).StreamDownloadAsync(DownloadUrl, target, new SynchronousProgress<DownloadSnapshot>(snapshots.Add));

        Assert.SequenceEqual(new[] { "libraries.minecraft.net", "bmclapi2.bangbang93.com" }, hosts);
        Assert.SequenceEqual(Payload, File.ReadAllBytes(target));
        Assert.True(streams.All(stream => stream.Disposed));
        Assert.Equal(1, snapshots.Count(snapshot => snapshot.CompletedFiles == 1));
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task ProfileRetryAsync()
    {
        using var temp = TempDir.Create();
        var profile = Profile();
        var target = ProfileTarget(temp.Path);
        using var handler = new AttemptHandler((_, attempt, _) => Task.FromResult(
            attempt <= 2 ? Response(new BodyStream(Payload[..4], disconnect: true), Payload.Length) : Response(Payload)));
        using var client = new HttpClient(handler);
        var service = new InstallServices(client);

        await ExpectFailureAsync<IOException>(service.DownloadProfileLibrariesAsync(profile, temp.Path, "official"));
        Assert.Equal(2, handler.Attempts);
        Assert.False(File.Exists(target));
        AssertNoTemporaryFiles(temp.Path);

        await service.DownloadProfileLibrariesAsync(profile, temp.Path, "official");
        Assert.Equal(3, handler.Attempts);
        Assert.SequenceEqual(Payload, File.ReadAllBytes(target));
        await service.DownloadProfileLibrariesAsync(profile, temp.Path, "official");
        Assert.Equal(3, handler.Attempts);
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task ProfileHashValidationAsync()
    {
        foreach (var nested in new[] { false, true })
        {
            using var temp = TempDir.Create();
            var profile = Profile();
            var library = profile["libraries"]![0]!.AsObject();
            var metadata = new JsonObject { ["size"] = Payload.Length, ["sha1"] = Sha1(Payload) };
            if (nested)
            {
                library["downloads"] = new JsonObject { ["artifact"] = metadata };
            }
            else
            {
                library["size"] = Payload.Length;
                library["sha1"] = Sha1(Payload);
            }

            var target = ProfileTarget(temp.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var corrupt = Enumerable.Repeat((byte)'x', Payload.Length).ToArray();
            File.WriteAllBytes(target, corrupt);
            using var handler = new AttemptHandler((_, attempt, _) => Task.FromResult(Response(attempt == 1 ? corrupt : Payload)));
            using var client = new HttpClient(handler);
            var service = new InstallServices(client);

            await service.DownloadProfileLibrariesAsync(profile, temp.Path, "official");

            Assert.Equal(2, handler.Attempts);
            Assert.SequenceEqual(Payload, File.ReadAllBytes(target));
            await service.DownloadProfileLibrariesAsync(profile, temp.Path, "official");
            Assert.Equal(2, handler.Attempts);
            AssertNoTemporaryFiles(temp.Path);
        }
    }

    private static async Task ProfileSizeValidationAsync()
    {
        using var temp = TempDir.Create();
        var profile = Profile();
        profile["libraries"]![0]!["size"] = Payload.Length;
        var target = ProfileTarget(temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var original = Payload[..2];
        File.WriteAllBytes(target, original);
        using var handler = new AttemptHandler((_, _, _) => Task.FromResult(Response(new BodyStream(Payload[..4]))));
        using var client = new HttpClient(handler);

        await ExpectFailureAsync<InvalidOperationException>(new InstallServices(client).DownloadProfileLibrariesAsync(profile, temp.Path, "official"));

        Assert.Equal(2, handler.Attempts);
        Assert.SequenceEqual(original, File.ReadAllBytes(target));
        AssertNoTemporaryFiles(temp.Path);
    }

    private static async Task ConcurrentInstallationDownloadsAsync()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            await ConcurrentInstallationDownloadAttemptAsync(separateServices: iteration % 2 != 0);
        }
    }

    private static async Task ConcurrentInstallationDownloadAttemptAsync(bool separateServices)
    {
        using var temp = TempDir.Create();
        using var cancellation = new CancellationTokenSource();
        var target = Path.Combine(temp.Path, "library.jar");
        var first = new BodyStream(Payload, wait: true);
        var second = new BodyStream(Payload, wait: true);
        using var handler = new AttemptHandler((_, attempt, _) => Task.FromResult(Response(
            attempt == 1 ? first : attempt == 2 ? second : new BodyStream(Payload), Payload.Length)));
        using var client = new HttpClient(handler);
        var service = new InstallServices(client);
        var secondService = separateServices ? new InstallServices(client) : service;
        var downloads = Task.WhenAll(
            service.StreamDownloadAsync(DownloadUrl, target, cancellationToken: cancellation.Token),
            secondService.StreamDownloadAsync(DownloadUrl, target, cancellationToken: cancellation.Token));
        try
        {
            await Task.WhenAll(first.Waiting.Task, second.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(target));
            var temporaryFiles = Directory.GetFiles(temp.Path, "*.download");
            if (temporaryFiles.Length != 2)
                throw new InvalidOperationException($"Expected 2 temporary files, got {temporaryFiles.Length}.");
            first.Release.TrySetResult();
            second.Release.TrySetResult();
            await downloads;
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await downloads;
            }
            catch
            {
                // The assertions or the await above report failures; always drain both downloads.
            }
        }

        if (handler.Attempts != 2)
            throw new InvalidOperationException($"Expected 2 HTTP requests, got {handler.Attempts}.");
        Assert.True(first.Disposed && second.Disposed);
        Assert.SequenceEqual(Payload, File.ReadAllBytes(target));
        AssertNoTemporaryFiles(temp.Path);
    }

    private static void InspectionCancellation()
    {
        using var temp = TempDir.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => DownloaderService.CollectMissingGameFiles(new JsonObject(), temp.Path, "test", cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => DownloaderService.MatchesFile(Path.Combine(temp.Path, "missing.jar"), cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => DownloaderService.BuildCoreJobs(new JsonObject(), temp.Path, "test", "official", cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => DownloaderService.BuildAssetJobs(new JsonObject(), temp.Path, "official", cancellation.Token));
    }

    private static DownloadJob Job(string target) => new(DownloadUrl, target, Sha1(Payload), Payload.Length, "library");

    private static string Sha1(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes));

    private static JsonObject Profile() => new()
    {
        ["libraries"] = new JsonArray(new JsonObject { ["name"] = "example:demo:1", ["url"] = "https://libraries.minecraft.net/" }),
    };

    private static string ProfileTarget(string root)
        => Path.Combine(root, "libraries", InstallServices.LibraryRelativePath("example:demo:1")!);

    private static HttpResponseMessage Response(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Response(BodyStream stream, long? contentLength = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = contentLength;
        return response;
    }

    private static void AssertNoTemporaryFiles(string root)
        => Assert.Equal(0, Directory.GetFiles(root, "*.download", SearchOption.AllDirectories).Length);

    private static async Task<TException> ExpectFailureAsync<TException>(Task task) where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class AttemptHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, Interlocked.Increment(ref _attempts), cancellationToken);
    }

    private sealed class BodyStream(byte[] bytes, bool disconnect = false, bool wait = false) : Stream
    {
        private int _position;
        public bool Disposed { get; private set; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - _position);
                bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            if (disconnect) throw new IOException("Simulated connection loss after partial body.");
            if (wait)
            {
                Waiting.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
