using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McGo.Core.App;
using McGo.Core.Security;
using McGo.Core.Storage;

namespace McGo.Core.Configuration;

public sealed class AppSettings
{
    public const int DefaultMaxCoreThreads = 12;
    public const int DefaultMaxAssetThreads = 24;
    public const int MaxConcurrencyLimit = 64;

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
        if (string.Equals(
                Config.Get("UPDATE", "manifest_url"),
                AppConstants.LegacyUpdateManifestUrl,
                StringComparison.OrdinalIgnoreCase))
        {
            Config.Set("UPDATE", "manifest_url", AppConstants.DefaultUpdateManifestUrl);
        }

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
            var accounts = AtomicStorage.LoadJsonFile(AccountsPath, new List<JsonObject>())
                .Select(SecureStore.HydrateAccountTokens)
                .ToList();
            if (!File.Exists(AccountsPath))
            {
                SaveAccounts(accounts);
            }

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

    /// <summary>核心文件（客户端 jar、libraries、natives 等）的下载并发上限，来自 DOWNLOAD/max_core_threads。</summary>
    public int MaxCoreConcurrency => NormalizeConcurrency(Config.GetInt("DOWNLOAD", "max_core_threads", DefaultMaxCoreThreads));

    /// <summary>资源文件（assets 对象）的下载并发上限，来自 DOWNLOAD/max_asset_threads。</summary>
    public int MaxAssetConcurrency => NormalizeConcurrency(Config.GetInt("DOWNLOAD", "max_asset_threads", DefaultMaxAssetThreads));

    public DownloadOptions ReadDownloadOptions()
        => new(
            MaxCoreConcurrency,
            MaxAssetConcurrency,
            Math.Max(0, Config.GetInt("DOWNLOAD", "speed_limit_kbps", 0)),
            Config.Get("DOWNLOAD", "cache_strategy", "reuse"));

    /// <summary>
    /// 不依赖 <see cref="LoadConfig"/>（因此也不会回写配置文件）地从默认配置路径读取下载选项。
    /// 供没有 <see cref="AppSettings"/> 实例的核心层服务（例如 Downloads.DownloaderService）使用。
    /// </summary>
    public static DownloadOptions DefaultDownloadOptions()
    {
        try
        {
            return ReadDownloadOptions(IniDocument.Load(AppConstants.ConfigFile));
        }
        catch
        {
            return DownloadOptions.Default;
        }
    }

    private static DownloadOptions ReadDownloadOptions(IniDocument document)
        => new(
            NormalizeConcurrency(document.GetInt("DOWNLOAD", "max_core_threads", DefaultMaxCoreThreads)),
            NormalizeConcurrency(document.GetInt("DOWNLOAD", "max_asset_threads", DefaultMaxAssetThreads)),
            Math.Max(0, document.GetInt("DOWNLOAD", "speed_limit_kbps", 0)),
            document.Get("DOWNLOAD", "cache_strategy", "reuse"));

    // 并发数为 0/负数会退化成串行或死锁，过大则可能耗尽套接字与线程池，因此统一夹到有效区间。
    private static int NormalizeConcurrency(int value) => Math.Clamp(value, 1, MaxConcurrencyLimit);

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
    string CacheStrategy)
{
    /// <summary>配置不可读时使用的默认值（与 DefaultSections 中的默认配置保持一致）。</summary>
    public static DownloadOptions Default { get; } = new(
        AppSettings.DefaultMaxCoreThreads,
        AppSettings.DefaultMaxAssetThreads,
        0,
        "reuse");
}
