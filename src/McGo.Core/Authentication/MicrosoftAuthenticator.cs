using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace McGo.Core.Authentication;

public sealed class MicrosoftAuthenticator
{
    private readonly HttpClient _httpClient;

    public string ClientId { get; }
    public string RedirectUri { get; }
    public string? AuthorizationCode { get; set; }
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public string? XstsToken { get; private set; }
    public string? UserHash { get; private set; }
    public string? MinecraftAccessToken { get; private set; }

    public MicrosoftAuthenticator(string clientId, string redirectUri, HttpClient? httpClient = null)
    {
        ClientId = clientId;
        RedirectUri = redirectUri;
        _httpClient = httpClient ?? new HttpClient();
    }

    public string GetLoginUrl()
        => "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize"
           + $"?client_id={Uri.EscapeDataString(ClientId)}"
           + "&response_type=code"
           + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
           + "&response_mode=query"
           + "&scope=XboxLive.signin%20offline_access";

    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(AuthorizationCode))
        {
            throw new InvalidOperationException("请先使用 get_login_url() 获取授权码");
        }

        var tokens = await PostFormAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = AuthorizationCode,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = "XboxLive.signin offline_access",
        }, cancellationToken);
        AccessToken = tokens["access_token"]?.GetValue<string>();
        RefreshToken = tokens["refresh_token"]?.GetValue<string>();
        await ExchangeTokensAsync(cancellationToken);
    }

    public async Task RefreshAccessTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("没有可用的刷新令牌");
        }

        var tokens = await PostFormAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = "XboxLive.signin offline_access",
        }, cancellationToken);
        AccessToken = tokens["access_token"]?.GetValue<string>();
        RefreshToken = tokens["refresh_token"]?.GetValue<string>();
        await ExchangeTokensAsync(cancellationToken);
    }

    public async Task<MinecraftProfile> GetMinecraftProfileAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(MinecraftAccessToken))
        {
            throw new InvalidOperationException("未登录或 Minecraft access token 不可用");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MinecraftAccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"获取 Minecraft 档案失败: {(int)response.StatusCode} {text}");
        }

        var profile = JsonNode.Parse(text)!.AsObject();
        return new MinecraftProfile(profile["id"]?.GetValue<string>() ?? "", profile["name"]?.GetValue<string>() ?? "", profile["skins"] as JsonArray ?? []);
    }

    private async Task ExchangeTokensAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            throw new InvalidOperationException("Microsoft access token 不可用。");
        }

        var xblResponse = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + AccessToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        }, cancellationToken);
        var xblToken = xblResponse["Token"]?.GetValue<string>() ?? "";
        UserHash = xblResponse["DisplayClaims"]?["xui"]?.AsArray().FirstOrDefault()?["uhs"]?.GetValue<string>();

        var xstsResponse = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new JsonArray(xblToken),
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        }, cancellationToken);
        XstsToken = xstsResponse["Token"]?.GetValue<string>();

        var minecraftResponse = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox", new JsonObject
        {
            ["identityToken"] = $"XBL3.0 x={UserHash};{XstsToken}",
        }, cancellationToken);
        MinecraftAccessToken = minecraftResponse["access_token"]?.GetValue<string>();
    }

    private async Task<JsonObject> PostFormAsync(string url, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(url, new FormUrlEncodedContent(data), cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
    }

    private async Task<JsonObject> PostJsonAsync(string url, JsonObject data, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(url, data, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
    }
}

public sealed record MinecraftProfile(string Id, string Name, JsonArray Skins);
