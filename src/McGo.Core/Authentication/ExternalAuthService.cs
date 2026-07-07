using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using McGo.Core.Utilities;

namespace McGo.Core.Authentication;

public sealed class ExternalAuthService
{
    public const string AuthlibInjectorMetadataUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
    private readonly HttpClient _httpClient;

    public ExternalAuthService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public static string NormalizeAuthServer(string? serverUrl)
    {
        var server = (serverUrl ?? "").Trim().TrimEnd('/');
        if (server.Length == 0)
        {
            throw new InvalidOperationException("请填写外置登录服务器地址。");
        }

        if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            server = "https://" + server;
        }

        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("外置登录服务器地址格式不正确。");
        }

        if (uri.Scheme == "http" && uri.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            throw new InvalidOperationException("外置登录服务器必须使用 HTTPS，本机测试地址除外。");
        }

        const string authserver = "/authserver";
        return server.EndsWith(authserver, StringComparison.OrdinalIgnoreCase) ? server[..^authserver.Length] : server;
    }

    public static string ExternalAuthEndpoint(string server, string action)
        => $"{NormalizeAuthServer(server)}/authserver/{action}";

    public static Dictionary<string, string> ExternalAuthHeaders()
        => new()
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json",
            ["User-Agent"] = "McGo/1.0",
        };

    public static string ExternalAuthError(HttpResponseMessage response, string action, string detail = "")
    {
        var labels = new Dictionary<string, string>
        {
            ["authenticate"] = "登录",
            ["refresh"] = "刷新",
            ["validate"] = "验证",
        };
        var actionLabel = labels.TryGetValue(action, out var label) ? label : action;
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : "详情：" + detail;
        return (int)response.StatusCode switch
        {
            401 or 403 => $"外置登录{actionLabel}被拒绝，请检查用户名、密码或令牌。{suffix}",
            404 => "认证端点不存在，请确认地址是 Yggdrasil/Authlib-Injector 根地址，例如 https://example.com/api/yggdrasil。",
            >= 500 => $"认证服务器内部错误（{(int)response.StatusCode}）。{suffix}",
            _ => $"外置登录{actionLabel}失败（HTTP {(int)response.StatusCode}）。{suffix}",
        };
    }

    public async Task<ExternalProbeResult> ProbeExternalAuthServerAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        var server = NormalizeAuthServer(serverUrl);
        var probes = new[] { $"{server}/authserver", $"{server}/authserver/validate", server };
        var errors = new List<string>();
        foreach (var url in probes)
        {
            try
            {
                using var response = url.EndsWith("/validate", StringComparison.Ordinal)
                    ? await _httpClient.PostAsJsonAsync(url, new { accessToken = "mcgo-probe" }, cancellationToken)
                    : await _httpClient.GetAsync(url, cancellationToken);
                if ((int)response.StatusCode < 500)
                {
                    return new ExternalProbeResult(server, (int)response.StatusCode, $"服务器可访问，探测端点返回 HTTP {(int)response.StatusCode}");
                }

                errors.Add($"{url}: HTTP {(int)response.StatusCode}");
            }
            catch (Exception exc)
            {
                errors.Add($"{url}: {exc.Message}");
            }
        }

        throw new InvalidOperationException("认证服务器不可访问：" + string.Join("；", errors.TakeLast(2)));
    }

    public async Task<JsonObject> AuthenticateExternalAccountAsync(string serverUrl, string username, string password, string clientToken = "", CancellationToken cancellationToken = default)
    {
        var server = NormalizeAuthServer(serverUrl);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("外置登录需要用户名和密码。");
        }

        clientToken = string.IsNullOrWhiteSpace(clientToken) ? Guid.NewGuid().ToString() : clientToken;
        var payload = new JsonObject
        {
            ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = username.Trim(),
            ["password"] = password,
            ["clientToken"] = clientToken,
            ["requestUser"] = true,
        };
        using var response = await _httpClient.PostAsJsonAsync(ExternalAuthEndpoint(server, "authenticate"), payload, cancellationToken);
        await RaiseForExternalAuthAsync(response, "authenticate", cancellationToken);
        var data = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? [];
        var selected = data["selectedProfile"] as JsonObject ?? [];
        var uuid = selected["id"]?.GetValue<string>() ?? "";
        var name = selected["name"]?.GetValue<string>() ?? "";
        var accessToken = data["accessToken"]?.GetValue<string>() ?? "";
        if (uuid.Length == 0 || name.Length == 0 || accessToken.Length == 0)
        {
            throw new InvalidOperationException("外置登录服务器返回的数据不完整。");
        }

        return new JsonObject
        {
            ["server"] = server,
            ["username"] = name,
            ["display_name"] = name,
            ["uuid"] = uuid,
            ["access_token"] = accessToken,
            ["client_token"] = data["clientToken"]?.GetValue<string>() ?? clientToken,
        };
    }

    public async Task<JsonObject> RefreshExternalAccountAsync(JsonObject account, CancellationToken cancellationToken = default)
    {
        var server = NormalizeAuthServer(account["auth_server"]?.GetValue<string>() ?? account["server"]?.GetValue<string>() ?? "");
        var accessToken = account["access_token"]?.GetValue<string>() ?? "";
        var clientToken = account["client_token"]?.GetValue<string>() ?? "";
        if (accessToken.Length == 0)
        {
            throw new InvalidOperationException("外置登录账号缺少 Access Token，请重新登录。");
        }

        var payload = new JsonObject
        {
            ["accessToken"] = accessToken,
            ["clientToken"] = clientToken,
            ["requestUser"] = true,
        };
        using var response = await _httpClient.PostAsJsonAsync(ExternalAuthEndpoint(server, "refresh"), payload, cancellationToken);
        if ((int)response.StatusCode == 403)
        {
            using var validateResponse = await _httpClient.PostAsJsonAsync(ExternalAuthEndpoint(server, "validate"), new JsonObject
            {
                ["accessToken"] = accessToken,
                ["clientToken"] = clientToken,
            }, cancellationToken);
            await RaiseForExternalAuthAsync(validateResponse, "validate", cancellationToken);
            return JsonNode.Parse(account.ToJsonString())!.AsObject();
        }

        await RaiseForExternalAuthAsync(response, "refresh", cancellationToken);
        var data = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? [];
        var selected = data["selectedProfile"] as JsonObject ?? [];
        var refreshed = JsonNode.Parse(account.ToJsonString())!.AsObject();
        refreshed["access_token"] = data["accessToken"]?.GetValue<string>() ?? accessToken;
        refreshed["client_token"] = data["clientToken"]?.GetValue<string>() ?? clientToken;
        if (selected["id"] is not null) refreshed["uuid"] = selected["id"]!.GetValue<string>();
        if (selected["name"] is not null)
        {
            refreshed["username"] = selected["name"]!.GetValue<string>();
            refreshed["display_name"] = selected["name"]!.GetValue<string>();
        }

        return refreshed;
    }

    public async Task<AuthlibInjectorMetadata> GetAuthlibInjectorDownloadAsync(CancellationToken cancellationToken = default)
    {
        var data = await _httpClient.GetFromJsonAsync<JsonObject>(AuthlibInjectorMetadataUrl, cancellationToken) ?? [];
        var version = data["version"]?.GetValue<string>() ?? "latest";
        var checksums = data["checksums"] as JsonObject;
        var url = data["download_url"]?.GetValue<string>()
                  ?? data["downloadUrl"]?.GetValue<string>()
                  ?? data["url"]?.GetValue<string>()
                  ?? $"https://authlib-injector.yushi.moe/artifact/{version}/authlib-injector.jar";
        var filename = data["fileName"]?.GetValue<string>() ?? data["filename"]?.GetValue<string>() ?? $"authlib-injector-{version}.jar";
        return new AuthlibInjectorMetadata(url, filename, checksums?["sha256"]?.GetValue<string>() ?? "");
    }

    public async Task<AuthlibInjectorDownloadResult> DownloadAuthlibInjectorAsync(string targetDir, CancellationToken cancellationToken = default)
    {
        var metadata = await GetAuthlibInjectorDownloadAsync(cancellationToken);
        Directory.CreateDirectory(targetDir);
        var filename = FileUtilities.SanitizeFilename(metadata.Filename, "authlib-injector.jar");
        if (!filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            filename += ".jar";
        }

        var targetPath = Path.Combine(targetDir, filename);
        using var response = await _httpClient.GetAsync(metadata.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(targetPath))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(metadata.Sha256))
        {
            await using var file = File.OpenRead(targetPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
            if (!actual.Equals(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("authlib-injector SHA256 校验失败。");
            }
        }

        return new AuthlibInjectorDownloadResult(targetPath, metadata.Url, metadata.Sha256);
    }

    public static IReadOnlyList<string> AuthlibInjectorArgs(JsonObject account)
    {
        if (account["type"]?.GetValue<string>() != "external")
        {
            return [];
        }

        var injectorPath = account["authlib_injector_path"]?.GetValue<string>()?.Trim() ?? "";
        var server = NormalizeAuthServer(account["auth_server"]?.GetValue<string>() ?? "");
        if (injectorPath.Length == 0)
        {
            throw new InvalidOperationException("外置登录账号缺少 authlib-injector jar 路径。");
        }

        if (!File.Exists(injectorPath))
        {
            throw new InvalidOperationException($"authlib-injector jar 不存在：{injectorPath}");
        }

        return [$"-javaagent:{injectorPath}={server}", $"-Dauthlibinjector.yggdrasil.prefetched={server}"];
    }

    private static async Task RaiseForExternalAuthAsync(HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = "";
        try
        {
            var data = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
            detail = data?["errorMessage"]?.GetValue<string>() ?? data?["error"]?.GetValue<string>() ?? "";
        }
        catch
        {
            detail = (await response.Content.ReadAsStringAsync(cancellationToken))[..Math.Min(300, (await response.Content.ReadAsStringAsync(cancellationToken)).Length)].Trim();
        }

        throw new InvalidOperationException(ExternalAuthError(response, action, detail));
    }
}

public sealed record ExternalProbeResult(string Server, int Status, string Message);
public sealed record AuthlibInjectorMetadata(string Url, string Filename, string Sha256);
public sealed record AuthlibInjectorDownloadResult(string Path, string Url, string Sha256);
