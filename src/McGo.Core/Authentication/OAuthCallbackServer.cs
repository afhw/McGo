using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace McGo.Core.Authentication;

public sealed class OAuthCallbackServer
{
    /// <summary>Returns only after the loopback listener is ready to receive browser callbacks.</summary>
    public async Task<OAuthCallbackSession> StartAsync(string callbackUri, TimeSpan timeout,
        CancellationToken cancellationToken = default, string? expectedState = null)
    {
        if (!Uri.TryCreate(callbackUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || uri.Port == 0 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              uri.Host == "127.0.0.1" || uri.Host == "[::1]"))
        {
            throw new ArgumentException("OAuth 回调必须是 http://localhost、127.0.0.1 或 [::1] 的固定端口地址，且不能包含查询参数或片段。", nameof(callbackUri));
        }

        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout), "OAuth 回调超时必须是有限的正时间。");

        cancellationToken.ThrowIfCancellationRequested();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // Do not inherit configured endpoints or request loggers from the launcher's environment.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestLineSize = 8192;
            options.Limits.MaxRequestHeadersTotalSize = 16384;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                options.Listen(IPAddress.Loopback, uri.Port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
                if (Socket.OSSupportsIPv6)
                    options.Listen(IPAddress.IPv6Loopback, uri.Port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
            }
            else
            {
                var address = uri.Host == "127.0.0.1" ? IPAddress.Loopback : IPAddress.IPv6Loopback;
                options.Listen(address, uri.Port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
            }
        });

        var app = builder.Build();
        var session = new OAuthCallbackSession(app, uri.AbsolutePath, expectedState, timeout, cancellationToken);
        app.Run(session.HandleAsync);
        await session.StartAsync(uri.Port).ConfigureAwait(false);
        return session;
    }

    public async Task<OAuthCallbackResult> WaitForCodeAsync(string callbackUri, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await using var session = await StartAsync(callbackUri, timeout, cancellationToken).ConfigureAwait(false);
        return await session.WaitForCodeAsync().ConfigureAwait(false);
    }
}

public sealed class OAuthCallbackSession : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _path;
    private readonly string? _expectedState;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _disposed = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly TaskCompletionSource<OAuthCallbackResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task<OAuthCallbackResult> _completion = null!;
    private bool _stopped;
    private int _claimed;

    internal OAuthCallbackSession(WebApplication app, string path, string? expectedState,
        TimeSpan timeout, CancellationToken callerToken)
    {
        _app = app;
        _path = path;
        _expectedState = expectedState;
        _callerToken = callerToken;
        _timeout = new CancellationTokenSource(timeout);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _timeout.Token, _disposed.Token);
    }

    internal async Task StartAsync(int port)
    {
        try
        {
            await _app.StartAsync(_lifetime.Token).ConfigureAwait(false);
            _lifetime.Token.ThrowIfCancellationRequested();
            _completion = CompleteAsync();
        }
        catch (Exception error)
        {
            var timedOut = _timeout.IsCancellationRequested && !_callerToken.IsCancellationRequested;
            await StopAsync().ConfigureAwait(false);
            if (error is OperationCanceledException && timedOut)
                throw new TimeoutException("等待 Microsoft 登录回调超时。", error);
            if (error is IOException or SocketException)
                throw new InvalidOperationException($"无法监听本机 OAuth 回调端口 {port}：端口可能已被占用或系统拒绝绑定，请关闭占用程序后重试。", error);
            throw;
        }
    }

    /// <summary>Completes after the listener has released its port, including on cancellation or timeout.</summary>
    public Task<OAuthCallbackResult> WaitForCodeAsync() => _completion;

    private async Task<OAuthCallbackResult> CompleteAsync()
    {
        try
        {
            return await _result.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_callerToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(_callerToken);
        }
        catch (OperationCanceledException) when (_timeout.IsCancellationRequested)
        {
            throw new TimeoutException("等待 Microsoft 登录回调超时。");
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _app.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _stopped = true;
                    _lifetime.Dispose();
                    _timeout.Dispose();
                    _disposed.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_stopped) _disposed.Cancel();
        }
        try
        {
            await _completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
    }

    internal async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Connection = "close";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        // RawTarget preserves escaping and dot segments that the server's parsed Path can normalize.
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
        var queryStart = target.IndexOf('?');
        var path = queryStart < 0 ? target : target[..queryStart];
        if (!string.Equals(path, _path, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET";
            return;
        }

        var query = context.Request.Query;
        if (_expectedState is not null &&
            (query["state"].Count != 1 || !string.Equals(query["state"][0], _expectedState, StringComparison.Ordinal)))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var error = query["error"];
        var code = query["code"];
        OAuthCallbackResult result;
        if (error.Count == 1 && !string.IsNullOrWhiteSpace(error[0]))
        {
            var description = query["error_description"];
            result = new OAuthCallbackResult("", error[0]!, description.Count == 1 ? description[0]! : error[0]!);
        }
        else if (error.Count == 0 && code.Count == 1 && !string.IsNullOrWhiteSpace(code[0]))
        {
            result = new OAuthCallbackResult(code[0]!, "", "");
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var message = result.Error.Length == 0
            ? "已收到授权码，请回到启动器继续完成帐号登录。"
            : "未获得 Microsoft 授权，请回到启动器查看详情或重试。";
        var html = $"<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>McGo Microsoft 授权</title><body><h1>{message}</h1></body></html>";
        context.Response.ContentType = "text/html; charset=utf-8";
        using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.Token);
        responseDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await context.Response.WriteAsync(html, responseDeadline.Token).ConfigureAwait(false);
            await context.Response.CompleteAsync().WaitAsync(responseDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { context.Abort(); }
        catch (IOException) { context.Abort(); }
        finally
        {
            _result.TrySetResult(result);
        }
    }
}

public sealed record OAuthCallbackResult(string Code, string Error, string ErrorDescription);
