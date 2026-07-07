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
    {
        var parts = libraryName.Split(':');
        if (parts.Length != 3) return null;
        return Path.Combine([.. parts[0].Split('.'), parts[1], parts[2], $"{parts[1]}-{parts[2]}.jar"]);
    }

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

    public async Task<JsonArray> GetFabricLoaderVersionsAsync(string gameVersion, string mirrorSource, CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<JsonArray>($"{MirrorRoot(mirrorSource)}/fabric-meta/v2/versions/loader/{gameVersion}", cancellationToken) ?? [];

    public async Task<JsonNode?> GetForgePromosAsync(string mirrorSource, CancellationToken cancellationToken = default)
    {
        var url = mirrorSource == "bmclapi"
            ? $"{MirrorRoot(mirrorSource)}/forge/promos"
            : "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        var data = await _httpClient.GetFromJsonAsync<JsonNode>(url, cancellationToken);
        return data?["promos"] ?? data;
    }

    public async Task<string?> GetForgeVersionForMcAsync(string gameVersion, string mirrorSource, CancellationToken cancellationToken = default)
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

            return candidates
                .OrderByDescending(item => item.Recommended)
                .ThenByDescending(item => item.Version, Comparer<string>.Create(CompareVersionKey))
                .FirstOrDefault().Version;
        }

        if (promos is JsonObject obj)
        {
            return obj[$"{gameVersion}-recommended"]?.GetValue<string>()
                   ?? obj[$"{gameVersion}-latest"]?.GetValue<string>();
        }

        return null;
    }

    public async Task<JsonArray> GetNeoForgeListAsync(string gameVersion, string mirrorSource, CancellationToken cancellationToken = default)
    {
        if (mirrorSource != "bmclapi")
        {
            throw new InvalidOperationException("NeoForge 安装当前仅支持 BMCLAPI 镜像。");
        }

        return await _httpClient.GetFromJsonAsync<JsonArray>($"{MirrorRoot(mirrorSource)}/neoforge/list/{gameVersion}", cancellationToken) ?? [];
    }

    public async Task<JsonArray> GetOptifineListAsync(string mirrorSource, CancellationToken cancellationToken = default)
    {
        if (mirrorSource != "bmclapi")
        {
            throw new InvalidOperationException("OptiFine 安装当前仅支持 BMCLAPI 镜像。");
        }

        return await _httpClient.GetFromJsonAsync<JsonArray>($"{MirrorRoot(mirrorSource)}/optifine/versionList", cancellationToken) ?? [];
    }

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
