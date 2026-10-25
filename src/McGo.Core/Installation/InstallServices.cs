using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McGo.Core.Storage;

namespace McGo.Core.Installation;

public sealed class InstallServices
{
    public static readonly IReadOnlyDictionary<string, string> MirrorSources = new Dictionary<string, string>
    {
        ["official"] = "https://launchermeta.mojang.com",
        ["bmclapi"] = "https://bmclapi2.bangbang93.com",
    };

    private readonly HttpClient _httpClient;

    public InstallServices(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public static string FallbackMirrorSource(string mirrorSource)
        => mirrorSource == "official" ? "bmclapi" : "official";

    public static IReadOnlyList<string> MirrorSourceSequence(string mirrorSource)
    {
        var primary = MirrorSources.ContainsKey(mirrorSource) ? mirrorSource : "official";
        var fallback = FallbackMirrorSource(primary);
        return fallback == primary ? [primary] : [primary, fallback];
    }

    public static string MirrorRoot(string mirrorSource)
        => MirrorSources.TryGetValue(mirrorSource, out var root) ? root : MirrorSources["official"];

    public static string RewriteDownloadUrlForMirror(string url, string mirrorSource)
    {
        var mirror = MirrorRoot(mirrorSource);
        if (mirrorSource == "bmclapi")
        {
            var replacements = new Dictionary<string, string>
            {
                ["https://piston-meta.mojang.com"] = mirror,
                ["https://piston-data.mojang.com"] = mirror,
                ["https://launcher.mojang.com"] = mirror,
                ["https://libraries.minecraft.net"] = mirror + "/maven",
                ["https://resources.download.minecraft.net"] = mirror + "/assets",
                ["https://launchermeta.mojang.com"] = mirror,
            };
            foreach (var (source, target) in replacements)
            {
                if (url.StartsWith(source, StringComparison.OrdinalIgnoreCase)) return target + url[source.Length..];
            }
        }
        else if (mirrorSource == "official")
        {
            var bmcl = MirrorSources["bmclapi"];
            var replacements = new Dictionary<string, string>
            {
                [bmcl + "/maven"] = "https://libraries.minecraft.net",
                [bmcl + "/assets"] = "https://resources.download.minecraft.net",
                [bmcl] = "https://piston-data.mojang.com",
            };
            foreach (var (source, target) in replacements)
            {
                if (url.StartsWith(source, StringComparison.OrdinalIgnoreCase)) return target + url[source.Length..];
            }
        }

        return url;
    }

    public async Task<IReadOnlyList<string>> GetRemoteVersionsAsync(string versionType, string mirrorSource, CancellationToken cancellationToken = default)
    {
        var manifest = await _httpClient.GetFromJsonAsync<JsonObject>($"{MirrorRoot(mirrorSource)}/mc/game/version_manifest.json", cancellationToken) ?? [];
        return manifest["versions"]?.AsArray().OfType<JsonObject>()
            .Where(version => version["type"]?.GetValue<string>() == versionType)
            .Select(version => version["id"]?.GetValue<string>() ?? "")
            .Where(id => id.Length > 0)
            .ToList() ?? [];
    }

    public async Task<string?> GetVersionUrlAsync(string versionId, string mirrorSource, CancellationToken cancellationToken = default)
    {
        var manifest = await _httpClient.GetFromJsonAsync<JsonObject>($"{MirrorRoot(mirrorSource)}/mc/game/version_manifest.json", cancellationToken) ?? [];
        return manifest["versions"]?.AsArray().OfType<JsonObject>()
            .FirstOrDefault(version => version["id"]?.GetValue<string>() == versionId)?["url"]?.GetValue<string>();
    }

    public async Task<(JsonObject Metadata, string MirrorSource)> GetVersionMetadataWithFallbackAsync(string versionId, string mirrorSource, Action<string>? status = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        foreach (var source in MirrorSourceSequence(mirrorSource))
        {
            try
            {
                if (source != mirrorSource) status?.Invoke($"主下载源失败，正在切换到 {source} 获取版本信息...");
                var versionUrl = await GetVersionUrlAsync(versionId, source, cancellationToken) ?? throw new InvalidOperationException($"无法获取版本 {versionId} 的下载地址");
                var rewritten = RewriteDownloadUrlForMirror(versionUrl, source);
                var metadata = await _httpClient.GetFromJsonAsync<JsonObject>(rewritten, cancellationToken) ?? [];
                return (metadata, source);
            }
            catch (Exception exc)
            {
                errors.Add($"{source}: {exc.Message}");
            }
        }

        throw new InvalidOperationException(errors.Count == 0 ? $"无法获取版本 {versionId} 的下载地址" : string.Join("；", errors));
    }

    public async Task StreamDownloadAsync(string url, string filePath, IProgress<DownloadSnapshot>? progress = null, string statusLabel = "下载中", string? mirrorSource = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
        HttpResponseMessage? response = null;
        Exception? lastError = null;
        foreach (var source in MirrorSourceSequence(mirrorSource ?? "official"))
        {
            var candidateUrl = RewriteDownloadUrlForMirror(url, source);
            try
            {
                response = await _httpClient.GetAsync(candidateUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                break;
            }
            catch (Exception exc)
            {
                response?.Dispose();
                response = null;
                lastError = exc;
            }
        }

        if (response is null) throw lastError ?? new InvalidOperationException("下载失败：" + url);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(filePath);
        var total = response.Content.Headers.ContentLength ?? 0;
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (total > 0)
            {
                progress?.Report(new DownloadSnapshot(Math.Min(1.0, (double)downloaded / total), statusLabel, Path.GetFileName(filePath), downloaded, total, 0, 0, 1, 0));
            }
        }

        progress?.Report(new DownloadSnapshot(1.0, statusLabel, Path.GetFileName(filePath), downloaded, total, 0, 1, 1, 0));
    }

    public static void ExtractZipBytes(byte[] zipBytes, string targetDir)
    {
        using var stream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(stream);
        archive.ExtractToDirectory(targetDir, overwriteFiles: true);
    }

    public static int CompareVersionKey(string left, string right)
    {
        var leftParts = Regex.Split(left ?? "", "[.\\-+_]");
        var rightParts = Regex.Split(right ?? "", "[.\\-+_]");
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            var leftPart = index < leftParts.Length ? leftParts[index] : "";
            var rightPart = index < rightParts.Length ? rightParts[index] : "";
            var leftNum = int.TryParse(leftPart, out var li);
            var rightNum = int.TryParse(rightPart, out var ri);
            var comparison = leftNum && rightNum ? li.CompareTo(ri) : string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0) return comparison;
        }

        return 0;
    }

    public static string? LibraryRelativePath(string libraryName)
        // 复用启动/下载侧同一份 Maven 坐标解析,支持 classifier 与扩展名
        // (如 group:artifact:version:universal),避免带 classifier 的
        // 安装器依赖被判为 null 而在安装时被静默跳过,导致模组加载器缺库。
        => McGo.Core.Minecraft.LauncherService.MavenLibraryPath(libraryName);

    public static string MirrorLibraryBase(string libraryUrl, string mirrorSource)
    {
        if (mirrorSource == "bmclapi"
            && (libraryUrl.Contains("maven.fabricmc.net", StringComparison.OrdinalIgnoreCase)
                || libraryUrl.Contains("maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
                || libraryUrl.Contains("libraries.minecraft.net", StringComparison.OrdinalIgnoreCase)
                || libraryUrl.Contains("maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase)))
        {
            return MirrorRoot(mirrorSource) + "/maven";
        }

        return libraryUrl.TrimEnd('/');
    }

    public async Task DownloadProfileLibrariesAsync(JsonObject profileJson, string gameDir, string mirrorSource, IProgress<DownloadSnapshot>? progress = null, CancellationToken cancellationToken = default)
    {
        var libraries = profileJson["libraries"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var completed = 0;
        foreach (var library in libraries)
        {
            var name = library["name"]?.GetValue<string>() ?? "";
            var relative = LibraryRelativePath(name);
            if (relative is null) continue;
            var urlBase = MirrorLibraryBase(library["url"]?.GetValue<string>() ?? "https://libraries.minecraft.net/", mirrorSource);
            var targetPath = Path.Combine(gameDir, "libraries", relative);
            if (File.Exists(targetPath))
            {
                completed++;
                progress?.Report(new DownloadSnapshot(Math.Min(1.0, (double)completed / Math.Max(libraries.Count, 1)), "复用安装依赖", Path.GetFileName(targetPath), completed, libraries.Count, 0, completed, libraries.Count, completed));
                continue;
            }

            await StreamDownloadAsync($"{urlBase}/{relative.Replace('\\', '/')}", targetPath, progress, "下载安装依赖", mirrorSource, cancellationToken);
            completed++;
        }
    }

    /// <summary>
    /// Fabric 的加载器元数据不在 Mojang 的 launchermeta 上：
    /// 官方源必须走 meta.fabricmc.net，BMCLAPI 源才有 /fabric-meta 镜像。
    /// 以前统一拼 launchermeta.mojang.com/fabric-meta/...，官方源下必然是 404
    /// （"The specified blob does not exist."），这就是"刷新安装器版本"失败的原因。
    /// </summary>
    public static string FabricMetaRoot(string mirrorSource)
        => mirrorSource == "bmclapi"
            ? MirrorRoot(mirrorSource) + "/fabric-meta"
            : "https://meta.fabricmc.net";

    /// <summary>Fabric 官方支持的游戏版本清单（用于区分"该版本没有 Fabric"和"网络出错"）。</summary>
    public async Task<IReadOnlyList<string>> GetFabricSupportedGameVersionsAsync(string mirrorSource, CancellationToken cancellationToken = default)
    {
        var data = await _httpClient.GetFromJsonAsync<JsonArray>($"{FabricMetaRoot(mirrorSource)}/v2/versions/game", cancellationToken);
        return data?.OfType<JsonObject>()
            .Select(item => item["version"]?.GetValue<string>() ?? "")
            .Where(value => value.Length > 0)
            .ToList() ?? [];
    }

    /// <summary>
    /// 返回该 Minecraft 版本可用的 Fabric Loader 列表。
    /// 两个源对"该版本没有 Fabric"的表达方式不同：官方 meta.fabricmc.net 返回 400，
    /// BMCLAPI 返回 404，都不是空数组。这里统一吞成空列表，
    /// 让调用方能把"没有版本"和"真的请求失败"区分开。
    /// </summary>
    public async Task<JsonArray> GetFabricLoaderVersionsAsync(string gameVersion, string mirrorSource, CancellationToken cancellationToken = default)
    {
        var url = FabricLoaderUrl(gameVersion, mirrorSource);
        try
        {
            return await _httpClient.GetFromJsonAsync<JsonArray>(url, cancellationToken) ?? [];
        }
        catch (HttpRequestException exc) when (exc.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            return [];
        }
    }

    /// <summary>某个 Minecraft 版本的 Fabric Loader 元数据地址。</summary>
    public static string FabricLoaderUrl(string gameVersion, string mirrorSource)
        => $"{FabricMetaRoot(mirrorSource)}/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}";

    public async Task<JsonNode?> GetForgePromosAsync(string mirrorSource, CancellationToken cancellationToken = default)
    {
        // 两个源的返回结构不一样：官方 promotions_slim.json 是 {"promos": {...}}，
        // BMCLAPI 的 /forge/promos 直接是 [{name, build:{mcversion, version}}] 数组。
        // 以前无条件 data["promos"]，遇到数组节点会抛
        // "The node must be of type 'JsonObject'"——这正是切到 bmclapi 时列表刷新失败的原因。
        var url = mirrorSource == "bmclapi"
            ? $"{MirrorRoot(mirrorSource)}/forge/promos"
            : "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        var data = await _httpClient.GetFromJsonAsync<JsonNode>(url, cancellationToken);
        return data switch
        {
            JsonArray array => array,
            JsonObject obj => obj["promos"] ?? obj,
            _ => null,
        };
    }

    public async Task<string?> GetForgeVersionForMcAsync(string gameVersion, string mirrorSource, CancellationToken cancellationToken = default)
    {
        var versions = await GetForgeVersionsForMcAsync(gameVersion, mirrorSource, cancellationToken);
        return versions.FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> GetForgeVersionsForMcAsync(string gameVersion, string mirrorSource, CancellationToken cancellationToken = default)
    {
        var promos = await GetForgePromosAsync(mirrorSource, cancellationToken);
        if (promos is JsonArray array)
        {
            var candidates = new List<(string Version, bool Recommended)>();
            foreach (var item in array.OfType<JsonObject>())
            {
                var build = item["build"] as JsonObject;
                if (build?["mcversion"]?.GetValue<string>() != gameVersion)
                {
                    continue;
                }

                var version = build["version"]?.GetValue<string>() ?? "";
                if (version.Length > 0)
                {
                    candidates.Add((version, (item["name"]?.GetValue<string>() ?? "").Contains("recommended", StringComparison.OrdinalIgnoreCase)));
                }
            }

            if (candidates.Count > 0)
            {
                return candidates
                    .OrderByDescending(item => item.Recommended)
                    .ThenByDescending(item => item.Version, Comparer<string>.Create(CompareVersionKey))
                    .Select(item => item.Version)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // promos 只收录 recommended/latest 两类构建，老的正式版（1.13.2 等）
            // 往往只有 latest 一条甚至完全没有。这时退回 Maven 元数据，
            // 否则明明能装的版本会被当成"没有 Forge"。
            return await GetForgeMavenVersionsForMcAsync(gameVersion, mirrorSource, cancellationToken);
        }

        if (promos is JsonObject obj)
        {
            var versions = new List<string>();
            var recommended = obj[$"{gameVersion}-recommended"]?.GetValue<string>();
            var latest = obj[$"{gameVersion}-latest"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(recommended))
            {
                versions.Add(recommended);
            }

            if (!string.IsNullOrWhiteSpace(latest))
            {
                versions.Add(latest);
            }

            if (versions.Count > 0)
            {
                return versions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            return await GetForgeMavenVersionsForMcAsync(gameVersion, mirrorSource, cancellationToken);
        }

        return [];
    }

    /// <summary>
    /// 从 Forge 的 Maven 元数据里筛出指定 Minecraft 版本的构建号（5000+ 条，可覆盖 promos 缺失的老版本）。
    /// 元数据一次约 200KB，切换游戏版本时会被反复请求，所以按镜像缓存原文。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetForgeMavenVersionsForMcAsync(string gameVersion, string mirrorSource, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return [];
        }

        var xml = await GetForgeMavenMetadataAsync(mirrorSource, cancellationToken);
        var prefix = gameVersion + "-";
        var versions = new List<string>();
        foreach (Match match in Regex.Matches(xml, "<version>([^<]+)</version>"))
        {
            var value = match.Groups[1].Value.Trim();
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                versions.Add(value[prefix.Length..]);
            }
        }

        return versions
            .Where(value => value.Length > 0 && char.IsDigit(value[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value, Comparer<string>.Create(CompareVersionKey))
            .ToList();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ForgeMavenMetadataCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task<string> GetForgeMavenMetadataAsync(string mirrorSource, CancellationToken cancellationToken)
    {
        if (ForgeMavenMetadataCache.TryGetValue(mirrorSource, out var cached))
        {
            return cached;
        }

        var xml = await _httpClient.GetStringAsync($"{ForgeMavenRoot(mirrorSource)}/net/minecraftforge/forge/maven-metadata.xml", cancellationToken);
        ForgeMavenMetadataCache[mirrorSource] = xml;
        return xml;
    }

    /// <summary>Forge 的 Maven 根地址：官方是 maven.minecraftforge.net，BMCLAPI 是其 /maven 镜像。</summary>
    public static string ForgeMavenRoot(string mirrorSource)
        => mirrorSource == "bmclapi" ? MirrorRoot("bmclapi") + "/maven" : "https://maven.minecraftforge.net";

    /// <summary>
    /// NeoForge / OptiFine 没有官方 JSON API，目前只有 BMCLAPI 提供列表。
    /// 官方源下也要去 BMCLAPI 取元数据：这只是一个很小的 JSON，
    /// 而"官方源直接报错说仅支持 BMCLAPI"会让用户在默认设置下完全装不了。
    /// </summary>
    public static string LoaderMetaRoot()
        => MirrorRoot("bmclapi");

    public async Task<JsonArray> GetNeoForgeListAsync(string gameVersion, string mirrorSource, CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<JsonArray>($"{LoaderMetaRoot()}/neoforge/list/{Uri.EscapeDataString(gameVersion)}", cancellationToken) ?? [];

    public async Task<JsonArray> GetOptifineListAsync(string mirrorSource, CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<JsonArray>($"{LoaderMetaRoot()}/optifine/versionList", cancellationToken) ?? [];

    /// <summary>
    /// NeoForge 安装器地址。BMCLAPI 返回的是 /maven/... 形式的相对路径，
    /// 直接拼在当前镜像根上；官方源要换成 NeoForged 的 Maven 主机，
    /// 否则会把 /maven/... 拼到 launchermeta.mojang.com 上（必然 404）。
    /// </summary>
    public static string NeoForgeInstallerUrl(string installerPath, string mirrorSource)
    {
        var path = installerPath.Trim();
        if (path.Length == 0)
        {
            return "";
        }

        if (mirrorSource == "bmclapi")
        {
            return MirrorRoot("bmclapi") + path;
        }

        return path.StartsWith("/maven/", StringComparison.OrdinalIgnoreCase)
            ? NeoForgedMavenRoot + "/" + path["/maven/".Length..]
            : path;
    }

    public const string NeoForgedMavenRoot = "https://maven.neoforged.net/releases";

    /// <summary>
    /// 没有 installerPath 时按 Maven 布局推导 NeoForge 安装器地址。
    /// 1.20.1 时代的 NeoForge 发布在 net/neoforged/forge 下（版本形如
    /// 1.20.1-47.1.5 / 1.20.1-forge-47.1.105），而 1.20.2 之后是
    /// net/neoforged/neoforge（版本形如 20.2.93）。硬套后者会让 1.20.1 直接 404。
    /// </summary>
    public static string NeoForgeInstallerUrl(string minecraftVersion, string rawVersion, string version, string mirrorSource)
    {
        var artifact = (rawVersion.Length > 0 ? rawVersion : version).Trim();
        if (artifact.Length == 0)
        {
            return "";
        }

        var legacy = artifact.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase);
        var root = mirrorSource == "bmclapi" ? MirrorRoot("bmclapi") + "/maven" : NeoForgedMavenRoot;
        return legacy
            ? $"{root}/net/neoforged/forge/{artifact}/forge-{artifact}-installer.jar"
            : $"{root}/net/neoforged/neoforge/{artifact}/neoforge-{artifact}-installer.jar";
    }

    /// <summary>OptiFine 安装包只有 BMCLAPI 有加速镜像，官方源也走这里（optifine.net 有防盗链）。</summary>
    public static string OptifineInstallerUrl(string minecraftVersion, string type, string patch, string mirrorSource = "official")
        => $"{LoaderMetaRoot()}/optifine/{minecraftVersion}/{type}/{patch}";

    public async Task<JsonArray> GetFabricApiVersionsAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        var url = "https://api.modrinth.com/v2/project/fabric-api/version"
                  + "?loaders=%5B%22fabric%22%5D"
                  + "&game_versions=%5B%22" + Uri.EscapeDataString(gameVersion) + "%22%5D";
        var data = await _httpClient.GetFromJsonAsync<JsonNode>(url, cancellationToken);
        return data switch
        {
            JsonArray array => array,
            JsonObject obj when obj["value"] is JsonArray value => value,
            _ => [],
        };
    }

    public static string BuildOptifineVersionId(string minecraftVersion, JsonObject selected)
    {
        var filename = selected["filename"]?.GetValue<string>()?.Trim() ?? "";
        if (filename.Length > 0)
        {
            var name = filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? filename[..^4] : filename;
            if (name.StartsWith("preview_", StringComparison.Ordinal)) name = name["preview_".Length..];
            var prefix = $"OptiFine_{minecraftVersion}_";
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return $"{minecraftVersion}-OptiFine_{name[prefix.Length..]}";
        }

        var type = selected["type"]?.GetValue<string>()?.Trim() ?? "HD_U";
        var patch = selected["patch"]?.GetValue<string>()?.Trim() ?? "";
        return $"{minecraftVersion}-OptiFine_{(patch.Length == 0 ? type : $"{type}_{patch}")}";
    }

    public static string BuildOptifineLibraryVersion(JsonObject selected)
    {
        var filename = selected["filename"]?.GetValue<string>()?.Trim() ?? "";
        if (filename.Length > 0)
        {
            var name = filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? filename[..^4] : filename;
            if (name.StartsWith("preview_", StringComparison.Ordinal)) name = name["preview_".Length..];
            return name.StartsWith("OptiFine_", StringComparison.Ordinal) ? name["OptiFine_".Length..] : name;
        }

        var minecraftVersion = selected["mcversion"]?.GetValue<string>()?.Trim() ?? "";
        var type = selected["type"]?.GetValue<string>()?.Trim() ?? "HD_U";
        var patch = selected["patch"]?.GetValue<string>()?.Trim() ?? "";
        return patch.Length == 0 ? $"{minecraftVersion}_{type}" : $"{minecraftVersion}_{type}_{patch}";
    }

    public static JsonObject? SelectOptifineCandidate(IEnumerable<JsonObject> candidates)
    {
        var list = candidates.ToList();
        return list.Where(item => !(item["filename"]?.GetValue<string>() ?? "").StartsWith("preview_", StringComparison.Ordinal)).FirstOrDefault()
               ?? list.FirstOrDefault();
    }
}

public sealed record DownloadSnapshot(double Progress, string Phase, string CurrentFile, long DownloadedBytes, long TotalBytes, long SpeedBytes, int CompletedFiles, int TotalFiles, int ReusedFiles);
