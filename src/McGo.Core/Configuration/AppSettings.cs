using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McGo.Core.App;
using McGo.Core.Security;
using McGo.Core.Storage;

namespace McGo.Core.Configuration;

public sealed class AppSettings
{
    public IniDocument Config { get; private set; } = new();
    public string ConfigPath { get; }
    public string AccountsPath { get; }

    public AppSettings(string? configPath = null, string? accountsPath = null)
    {
        ConfigPath = configPath ?? AppConstants.ConfigFile;
        AccountsPath = accountsPath ?? AppConstants.AccountsFile;
    }

    public void LoadConfig()
    {
        Config = IniDocument.Load(ConfigPath);
        Config.EnsureDefaults(DefaultSections);
        SaveConfig();
    }

    public void SaveConfig() => Config.Save(ConfigPath);

    public static (ulong TotalMb, ulong AvailableMb) SystemMemoryMb()
    {
        var info = GC.GetGCMemoryInfo();
        var total = (ulong)(info.TotalAvailableMemoryBytes / 1024 / 1024);
        return (total, total);
    }

    public static int RecommendedMemoryMb()
    {
        var (totalMb, availableMb) = SystemMemoryMb();
        if (availableMb <= 0)
        {
            return 4096;
        }

        var reserveMb = totalMb >= 8192 ? 2048 : 1536;
        var recommended = Math.Max(2048, Math.Min(8192, (int)availableMb - reserveMb));
        if (totalMb is > 0 and <= 4096)
        {
            recommended = Math.Max(1536, Math.Min(2048, (int)availableMb - 1024));
        }

        return Math.Max(1024, recommended / 256 * 256);
    }

    public static string AccountLabel(IReadOnlyDictionary<string, string> account)
    {
        var labels = new Dictionary<string, string>
        {
            ["microsoft"] = "Microsoft",
            ["external"] = "外置登录",
            ["offline"] = "离线",
        };
        var type = account.TryGetValue("type", out var rawType) ? rawType : "offline";
        var label = labels.TryGetValue(type, out var mapped) ? mapped : type;
        var displayName = account.TryGetValue("display_name", out var display) && !string.IsNullOrWhiteSpace(display)
            ? display
            : account.TryGetValue("username", out var username) && !string.IsNullOrWhiteSpace(username)
                ? username
                : "未命名";
        return $"{displayName} ({label})";
    }

    public List<JsonObject> LoadAccounts()
    {
        if (File.Exists(AccountsPath))
        {
            var node = JsonNode.Parse(File.ReadAllText(AccountsPath));
            var accounts = node?.AsArray()
                .OfType<JsonObject>()
                .Select(SecureStore.HydrateAccountTokens)
                .ToList() ?? [];
            return accounts;
        }

        var migrated = new List<JsonObject>();
        var username = Config.Get("USER", "username").Trim();
        if (!string.IsNullOrWhiteSpace(username))
        {
            migrated.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["type"] = "offline",
                ["display_name"] = username,
                ["username"] = username,
                ["uuid"] = Config.Get("USER", "uuid").Trim(),
                ["access_token"] = Config.Get("USER", "accessToken").Trim(),
                ["refresh_token"] = "",
            });
        }

        var refreshToken = Config.Get("AUTH", "refresh_token").Trim();
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            migrated.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["type"] = "microsoft",
                ["display_name"] = "Microsoft 账号",
                ["username"] = "",
                ["uuid"] = "",
                ["access_token"] = "",
                ["refresh_token"] = refreshToken,
            });
        }

        SaveAccounts(migrated);
        return migrated;
    }

    public void SaveAccounts(IEnumerable<JsonObject> accounts)
        => AtomicStorage.SaveJsonAtomic(AccountsPath, SecureStore.RedactAccounts(accounts), indented: true);

    public DownloadOptions ReadDownloadOptions()
        => new(
            Math.Max(1, Config.GetInt("DOWNLOAD", "max_core_threads", 12)),
            Math.Max(1, Config.GetInt("DOWNLOAD", "max_asset_threads", 24)),
            Math.Max(0, Config.GetInt("DOWNLOAD", "speed_limit_kbps", 0)),
            Config.Get("DOWNLOAD", "cache_strategy", "reuse"));

    public static string ConciseDownloadError(string? message)
    {
        var text = (message ?? "").Trim();
        if (text.Length == 0)
        {
            return "下载任务失败，请查看日志获取详细信息。";
        }

        var parts = text.Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && parts.All(part => part.Contains("404", StringComparison.Ordinal)))
        {
            var urlMatch = Regex.Match(parts[0], @"https?://[^\s'；,]+");
            var filename = "";
            if (urlMatch.Success && Uri.TryCreate(urlMatch.Value.TrimEnd(':', ',', ';', '，', '；'), UriKind.Absolute, out var uri))
            {
                filename = Path.GetFileName(uri.LocalPath);
            }

            if (string.IsNullOrEmpty(filename))
            {
                var jarMatch = Regex.Match(parts[0], @"([^/\\；:'""]+\.jar)");
                filename = jarMatch.Success ? jarMatch.Groups[1].Value : "目标文件";
            }

            return $"{filename} 在当前镜像源和官方源均不存在（404）。请切换镜像源或重新刷新版本信息后重试。";
        }

        return text.Length <= 240 ? text : text[..237] + "...";
    }

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> DefaultSections =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["USER"] = new Dictionary<string, string> { ["username"] = "", ["uuid"] = "", ["accessToken"] = "" },
            ["DOWNLOAD"] = new Dictionary<string, string>
            {
                ["mirror_source"] = "official",
                ["download_preset"] = "均衡",
                ["max_core_threads"] = "12",
                ["max_asset_threads"] = "24",
                ["speed_limit_kbps"] = "0",
                ["cache_strategy"] = "reuse",
                ["modrinth_api_base"] = "",
                ["modrinth_cdn_base"] = "",
            },
            ["AUTH"] = new Dictionary<string, string>
            {
                ["use_microsoft_login"] = "False",
                ["refresh_token"] = "",
                ["auto_open_browser"] = "True",
            },
            ["GAME"] = new Dictionary<string, string>
            {
                ["directory"] = AppConstants.DefaultGameDirectory,
                ["enable_resource_isolation"] = "True",
            },
            ["UI"] = new Dictionary<string, string> { ["advanced_mode"] = "False", ["theme"] = "dark", ["theme_image"] = "" },
            ["HOME"] = new Dictionary<string, string> { ["content_source"] = "", ["allow_network"] = "False" },
            ["MUSIC"] = new Dictionary<string, string> { ["path"] = "", ["enabled"] = "False", ["volume"] = "35", ["pause_on_launch"] = "True" },
            ["UPDATE"] = new Dictionary<string, string> { ["auto_check"] = "True", ["manifest_url"] = AppConstants.DefaultUpdateManifestUrl },
            ["FEATURES"] = new Dictionary<string, string> { ["show_download"] = "True", ["show_manage"] = "True" },
            ["SERVERS"] = new Dictionary<string, string> { ["items"] = "" },
            ["P2P"] = new Dictionary<string, string>
            {
                ["relay_host"] = "flyliq.cn",
                ["relay_port"] = "10721",
                ["room"] = "",
                ["secret"] = "",
                ["host_port"] = "25565",
                ["join_port"] = "25565",
                ["udp_enabled"] = "True",
            },
            ["ACCOUNTS"] = new Dictionary<string, string> { ["selected_account_id"] = "" },
        };
}

public sealed record DownloadOptions(
    int MaxCoreConcurrency,
    int MaxAssetConcurrency,
    int SpeedLimitKbps,
    string CacheStrategy);
