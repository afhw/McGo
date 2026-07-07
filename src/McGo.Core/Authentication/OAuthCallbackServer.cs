using System.Net;
using System.Text;

namespace McGo.Core.Authentication;

public sealed class OAuthCallbackServer
{
    public async Task<OAuthCallbackResult> WaitForCodeAsync(string callbackUri, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(callbackUri);
        var prefix = $"{uri.Scheme}://{uri.Host}:{uri.Port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(linked.Token);
                var requestPath = context.Request.Url?.AbsolutePath ?? "";
                if (!requestPath.Equals(uri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHtmlAsync(context.Response, "McGo", "回调地址不匹配。", false);
                    continue;
                }

                var error = context.Request.QueryString["error"];
                if (!string.IsNullOrWhiteSpace(error))
                {
                    var description = context.Request.QueryString["error_description"] ?? error;
                    await WriteHtmlAsync(context.Response, "McGo 登录失败", description, false);
                    return new OAuthCallbackResult("", error, description);
                }

                var code = context.Request.QueryString["code"] ?? "";
                if (!string.IsNullOrWhiteSpace(code))
                {
                    await WriteHtmlAsync(context.Response, "McGo 登录成功", "可以回到启动器继续。", true);
                    return new OAuthCallbackResult(code, "", "");
                }

                await WriteHtmlAsync(context.Response, "McGo", "回调缺少授权码。", false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException("等待 Microsoft 登录回调超时。");
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse response, string title, string message, bool success)
    {
        var color = success ? "#16a34a" : "#dc2626";
        var html = $"""
<!doctype html>
<meta charset="utf-8">
<title>{WebUtility.HtmlEncode(title)}</title>
<body style="font-family:Segoe UI, sans-serif; background:#111827; color:#e5e7eb; display:grid; place-items:center; min-height:100vh;">
  <main style="max-width:560px; padding:32px; background:#172033; border-radius:12px;">
    <h1 style="color:{color};">{WebUtility.HtmlEncode(title)}</h1>
    <p>{WebUtility.HtmlEncode(message)}</p>
  </main>
</body>
""";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}

public sealed record OAuthCallbackResult(string Code, string Error, string ErrorDescription);
