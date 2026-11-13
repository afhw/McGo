using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using McGo.Core.App;
using McGo.Core.Authentication;
using Microsoft.UI.Xaml;

namespace McGo.WinUI.Pages;

public sealed partial class AccountsPage
{
    internal async Task RunMicrosoftLoginSmokeChecksAsync(bool openRealBrowser)
    {
        SelectLoginMode("microsoft");
        using var tokenHandler = new MicrosoftLoginSmokeHandler();
        using var tokenClient = new HttpClient(tokenHandler);
        using var callbackClient = new HttpClient(new HttpClientHandler { UseProxy = false });
        _microsoftCallbackUri = FreeCallbackUri();
        _microsoftAuthenticatorFactory = redirect => new MicrosoftAuthenticator(AppConstants.ClientId, redirect, tokenClient);
        var browserCalls = 0;
        _openMicrosoftBrowserAsync = async loginUri =>
        {
            browserCalls++;
            using var response = await callbackClient.GetAsync(CallbackUri(loginUri, "code=smoke-code"));
            response.EnsureSuccessStatusCode();
            return true;
        };
        var originalCount = _accounts.Count;
        await BeginMicrosoftLoginAsync(openBrowser: true);
        await _microsoftLoginCompletion.WaitAsync(TimeSpan.FromSeconds(20));
        if (browserCalls != 1 || tokenHandler.Calls != 5 || _accounts.Count != originalCount + 1
            || !MicrosoftOutputBox.Text.Contains("SmokePlayer") || !StartMicrosoftLoginButton.IsEnabled)
            throw new InvalidOperationException("The ready callback, token exchange and account save did not complete.");

        await BeginMicrosoftLoginAsync(openBrowser: false);
        var firstUrl = await EnsureMicrosoftLoginReadyAsync();
        if (await EnsureMicrosoftLoginReadyAsync() != firstUrl || browserCalls != 1 || StartMicrosoftLoginButton.IsEnabled)
            throw new InvalidOperationException("Repeated login actions did not reuse the active session.");
        CancelMicrosoftLogin(this, new RoutedEventArgs());
        await _microsoftLoginCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        if (!StartMicrosoftLoginButton.IsEnabled || CancelMicrosoftLoginButton.IsEnabled)
            throw new InvalidOperationException("Cancellation did not restore login controls.");

        _openMicrosoftBrowserAsync = _ => throw new Win32Exception(6, "Invalid handle smoke test");
        await BeginMicrosoftLoginAsync(openBrowser: true);
        if (_microsoftLoginCancellation is null || !MicrosoftOutputBox.Text.Contains("复制链接"))
            throw new InvalidOperationException("Browser failure did not retain a usable manual login session.");
        CancelMicrosoftLogin(this, new RoutedEventArgs());
        await _microsoftLoginCompletion.WaitAsync(TimeSpan.FromSeconds(10));

        var callsBeforeDenial = tokenHandler.Calls;
        _openMicrosoftBrowserAsync = async loginUri =>
        {
            using var response = await callbackClient.GetAsync(CallbackUri(loginUri, "error=access_denied"));
            return response.IsSuccessStatusCode;
        };
        await BeginMicrosoftLoginAsync(openBrowser: true);
        await _microsoftLoginCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        if (tokenHandler.Calls != callsBeforeDenial || !MicrosoftOutputBox.Text.Contains("取消了 Microsoft 授权"))
            throw new InvalidOperationException("Denied authorization was not surfaced before token exchange.");

        using (var occupied = new TcpListener(IPAddress.Loopback, 0))
        {
            occupied.Start();
            _microsoftCallbackUri = $"http://localhost:{((IPEndPoint)occupied.LocalEndpoint).Port}/login/callback";
            _openMicrosoftBrowserAsync = _ => throw new InvalidOperationException("Browser must not open when callback binding fails.");
            await BeginMicrosoftLoginAsync(openBrowser: true);
            if (_microsoftLoginCancellation is not null || !MicrosoftOutputBox.Text.Contains("准备本机回调失败"))
                throw new InvalidOperationException("Callback bind failure was not reported before opening the browser.");
        }

        if (openRealBrowser)
        {
            _microsoftCallbackUri = FreeCallbackUri();
            _openMicrosoftBrowserAsync = async loginUri => await Windows.System.Launcher.LaunchUriAsync(CallbackUri(loginUri, "code=smoke-code"));
            await BeginMicrosoftLoginAsync(openBrowser: true);
            try
            {
                await _microsoftLoginCompletion.WaitAsync(TimeSpan.FromSeconds(30));
                if (!MicrosoftOutputBox.Text.Contains("SmokePlayer"))
                    throw new InvalidOperationException("The default browser did not complete the local callback.");
            }
            finally
            {
                _microsoftLoginCancellation?.Cancel();
                await _microsoftLoginCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        static string FreeCallbackUri()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return $"http://localhost:{((IPEndPoint)probe.LocalEndpoint).Port}/login/callback";
        }

        static Uri CallbackUri(Uri loginUri, string response)
        {
            var query = HttpUtility.ParseQueryString(loginUri.Query);
            var state = query["state"];
            if (string.IsNullOrWhiteSpace(state)) throw new InvalidOperationException("Missing authorization state.");
            return new Uri(query["redirect_uri"] + "?" + response + "&state=" + Uri.EscapeDataString(state));
        }
    }

    private sealed class MicrosoftLoginSmokeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/token", StringComparison.Ordinal))
            {
                var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (form["code"] != "smoke-code") throw new InvalidOperationException("Unexpected authorization code.");
            }
            var body = path switch
            {
                "/consumers/oauth2/v2.0/token" => "{\"access_token\":\"smoke-ms\",\"refresh_token\":\"smoke-refresh\"}",
                "/user/authenticate" => "{\"Token\":\"smoke-xbl\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"smoke-user\"}]}}",
                "/xsts/authorize" => "{\"Token\":\"smoke-xsts\"}",
                "/authentication/login_with_xbox" => "{\"access_token\":\"smoke-minecraft\"}",
                "/minecraft/profile" => "{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"SmokePlayer\",\"skins\":[]}",
                _ => throw new InvalidOperationException("Unexpected authentication endpoint.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
