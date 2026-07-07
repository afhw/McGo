namespace McGo.Core.Personalization;

public sealed class HomeContentService
{
    private readonly HttpClient _httpClient;

    public HomeContentService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<HomeContentResult> LoadContentAsync(string source, bool allowNetwork, CancellationToken cancellationToken = default)
    {
        var trimmed = source.Trim();
        if (trimmed.Length == 0)
        {
            return new HomeContentResult("", "未配置自定义主页。", false);
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (!allowNetwork)
            {
                throw new InvalidOperationException("联网主页未启用。");
            }

            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return new HomeContentResult(trimmed, LimitText(text), true);
        }

        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(trimmed));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("主页内容文件不存在。", path);
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        return new HomeContentResult(path, LimitText(content), false);
    }

    private static string LimitText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return normalized.Length <= 64 * 1024 ? normalized : normalized[..(64 * 1024)] + "\n...";
    }
}

public sealed record HomeContentResult(string Source, string Text, bool FromNetwork);
