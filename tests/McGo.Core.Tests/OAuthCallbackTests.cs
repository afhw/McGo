using System.Net;
using System.Net.Sockets;
using McGo.Core.Authentication;

internal static class OAuthCallbackTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public static void Register(TestRunner runner)
    {
        Add(runner, "OAuth immediate callback is captured before waiting", ImmediateCallbackAsync);
        Add(runner, "OAuth access_denied returns authorization error", () => ErrorAsync("access_denied", "The user declined"));
        Add(runner, "OAuth provider error falls back to its error code", () => ErrorAsync("server_error", null));
        Add(runner, "OAuth ignores missing, mismatched and duplicate state", StateAsync);
        Add(runner, "OAuth enforces exact path and GET while ignoring empty callbacks", PathAndMethodAsync);
        Add(runner, "OAuth cancellation releases both localhost ports", CancellationAsync);
        Add(runner, "OAuth timeout releases ports even without an active waiter", TimeoutAsync);
        Add(runner, "OAuth disposal cancels the waiter and is repeatable", DisposalAsync);
        Add(runner, "OAuth IPv4 callback works for localhost listener", () => AddressAsync("localhost", "127.0.0.1"));
        Add(runner, "OAuth IPv6 callback works for localhost listener", () => AddressAsync("localhost", "[::1]"));
        Add(runner, "OAuth literal IPv4 callback works", () => AddressAsync("127.0.0.1", "127.0.0.1"));
        Add(runner, "OAuth literal IPv6 callback works", () => AddressAsync("[::1]", "[::1]"));
        Add(runner, "OAuth occupied port fails clearly before browser launch", OccupiedPortAsync);
        Add(runner, "OAuth partial localhost bind failure releases IPv4", PartialBindFailureAsync);
        Add(runner, "OAuth rejects non-loopback endpoints and invalid timeouts", InvalidOptionsAsync);
        Add(runner, "OAuth pre-canceled startup does not occupy a port", PreCanceledAsync);
        Add(runner, "OAuth bounds responses and does not log callback secrets", NoSecretsAsync);
    }

    private static void Add(TestRunner runner, string name, Func<Task> test)
        => runner.Add(name, () => test().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult());

    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TestTimeout };

    private static string Callback(int port, string host = "localhost", string path = "/login/callback")
        => $"http://{host}:{port}{path}";

    private static TcpListener Bind(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        listener.Server.ExclusiveAddressUse = true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = false;
        listener.Start();
        return listener;
    }

    private static int FreePort()
    {
        using var listener = Bind(IPAddress.Loopback, 0);
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void AssertReleased(int port)
    {
        using var ipv4 = Bind(IPAddress.Loopback, port);
        if (Socket.OSSupportsIPv6)
        {
            using var ipv6 = Bind(IPAddress.IPv6Loopback, port);
        }
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task ImmediateCallbackAsync()
    {
        var port = FreePort();
        var uri = Callback(port);
        await using var session = await new OAuthCallbackServer().StartAsync(uri, TestTimeout);
        using var client = Client();
        using var response = await client.GetAsync(uri + "?code=immediate%2Bcode");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("已收到授权码", html);
        Assert.False(html.Contains("登录成功", StringComparison.Ordinal));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var result = await session.WaitForCodeAsync();
        Assert.Equal("immediate+code", result.Code);
        Assert.Equal("", result.Error);
        AssertReleased(port);
    }

    private static async Task ErrorAsync(string error, string? description)
    {
        var port = FreePort();
        var uri = Callback(port);
        await using var session = await new OAuthCallbackServer().StartAsync(uri, TestTimeout, expectedState: "expected");
        using var client = Client();
        var query = $"?state=expected&error={error}";
        if (description is not null) query += "&error_description=" + Uri.EscapeDataString(description);
        using var response = await client.GetAsync(uri + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await session.WaitForCodeAsync();
        Assert.Equal("", result.Code);
        Assert.Equal(error, result.Error);
        Assert.Equal(description ?? error, result.ErrorDescription);
        AssertReleased(port);
    }

    private static async Task StateAsync()
    {
        var uri = Callback(FreePort());
        await using var session = await new OAuthCallbackServer().StartAsync(uri, TestTimeout, expectedState: "Exact+State");
        using var client = Client();
        foreach (var query in new[] { "code=bad", "code=bad&state=exact%2BState", "error=access_denied&state=wrong", "code=bad&state=Exact%2BState&state=wrong" })
        {
            using var ignored = await client.GetAsync(uri + "?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, ignored.StatusCode);
            Assert.False(session.WaitForCodeAsync().IsCompleted);
        }
        using var response = await client.GetAsync(uri + "?code=accepted&state=Exact%2BState");
        Assert.Equal("accepted", (await session.WaitForCodeAsync()).Code);
    }

    private static async Task PathAndMethodAsync()
    {
        var port = FreePort();
        var uri = Callback(port);
        await using var session = await new OAuthCallbackServer().StartAsync(uri, TestTimeout);
        using var client = Client();
        foreach (var path in new[] { "/favicon.ico", "/login/Callback", "/login/callback/", "/login/callback/extra", "/other" })
        {
            using var response = await client.GetAsync(Callback(port, path: path) + "?code=wrong&error=access_denied");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.False(session.WaitForCodeAsync().IsCompleted);
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Head, HttpMethod.Options })
        {
            using var request = new HttpRequestMessage(method, uri + "?code=wrong");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.False(session.WaitForCodeAsync().IsCompleted);
        }
        foreach (var query in new[] { "", "?code=", "?code=%20", "?code=first&code=second" })
        {
            using var response = await client.GetAsync(uri + query);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(session.WaitForCodeAsync().IsCompleted);
        }
        using var accepted = await client.GetAsync(uri + "?code=accepted");
        Assert.Equal("accepted", (await session.WaitForCodeAsync()).Code);
    }

    private static async Task CancellationAsync()
    {
        var port = FreePort();
        using var cancellation = new CancellationTokenSource();
        await using var session = await new OAuthCallbackServer().StartAsync(Callback(port), TestTimeout, cancellation.Token);
        // An incomplete browser connection must not hold shutdown open indefinitely.
        using var idleConnection = new TcpClient();
        await idleConnection.ConnectAsync(IPAddress.Loopback, port);
        cancellation.Cancel();
        var error = await ThrowsAsync<OperationCanceledException>(() => session.WaitForCodeAsync());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        AssertReleased(port);
    }

    private static async Task TimeoutAsync()
    {
        var port = FreePort();
        await using var session = await new OAuthCallbackServer().StartAsync(Callback(port), TimeSpan.FromMilliseconds(250));
        // Cleanup starts when the session starts, independently of WaitForCodeAsync being called.
        await Task.Delay(750);
        AssertReleased(port);
        await ThrowsAsync<TimeoutException>(() => session.WaitForCodeAsync());
    }

    private static async Task DisposalAsync()
    {
        var port = FreePort();
        var session = await new OAuthCallbackServer().StartAsync(Callback(port), TestTimeout);
        var waiting = session.WaitForCodeAsync();
        await session.DisposeAsync();
        await session.DisposeAsync();
        await ThrowsAsync<OperationCanceledException>(() => waiting);
        AssertReleased(port);
    }

    private static async Task AddressAsync(string listenHost, string requestHost)
    {
        if (requestHost == "[::1]" && !Socket.OSSupportsIPv6) return;
        var port = FreePort();
        await using var session = await new OAuthCallbackServer().StartAsync(Callback(port, listenHost), TestTimeout);
        using var client = Client();
        using var response = await client.GetAsync(Callback(port, requestHost) + "?code=loopback");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("loopback", (await session.WaitForCodeAsync()).Code);
        AssertReleased(port);
    }

    private static async Task OccupiedPortAsync()
    {
        using var listener = Bind(IPAddress.Loopback, 0);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var error = await ThrowsAsync<InvalidOperationException>(() => new OAuthCallbackServer().StartAsync(Callback(port), TestTimeout));
        Assert.Contains(port.ToString(), error.Message);
        Assert.Contains("占用", error.Message);
    }

    private static async Task PartialBindFailureAsync()
    {
        if (!Socket.OSSupportsIPv6) return;
        var port = FreePort();
        using var listener = Bind(IPAddress.IPv6Loopback, port);
        await ThrowsAsync<InvalidOperationException>(() => new OAuthCallbackServer().StartAsync(Callback(port), TestTimeout));
        using var ipv4 = Bind(IPAddress.Loopback, port);
    }

    private static async Task InvalidOptionsAsync()
    {
        var server = new OAuthCallbackServer();
        foreach (var uri in new[] { "http://0.0.0.0:5000/cb", "http://[::]:5000/cb", "http://192.168.1.2:5000/cb", "http://example.com:5000/cb", "https://localhost:5000/cb", "http://localhost:0/cb", "http://user@localhost:5000/cb", "http://localhost:5000/cb?code=x", "http://localhost:5000/cb#fragment" })
            await ThrowsAsync<ArgumentException>(() => server.StartAsync(uri, TestTimeout));
        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
            await ThrowsAsync<ArgumentOutOfRangeException>(() => server.StartAsync(Callback(FreePort()), timeout));
    }

    private static async Task PreCanceledAsync()
    {
        var port = FreePort();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => new OAuthCallbackServer().StartAsync(Callback(port), TestTimeout, cancellation.Token));
        AssertReleased(port);
    }

    private static async Task NoSecretsAsync()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Console.SetOut(output);
        Console.SetError(errors);
        try
        {
            var uri = Callback(FreePort());
            await using var session = await new OAuthCallbackServer().StartAsync(uri, TestTimeout);
            using var client = Client();
            using var response = await client.GetAsync(uri + "?error=access_denied&error_description=" + Uri.EscapeDataString("<script>secret-code</script>" + new string('x', 4000)));
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(html.Length < 1024);
            Assert.False(html.Contains("secret-code", StringComparison.Ordinal));
            Assert.Equal("access_denied", (await session.WaitForCodeAsync()).Error);
            Assert.Equal("", output.ToString());
            Assert.Equal("", errors.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
