using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json.Nodes;
using McGo.Core.Utilities;

namespace McGo.Core.Resources;

public sealed class ResourceMarketService
{
    private static readonly IReadOnlyDictionary<string, string[]> ResourceExtensions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["mod"] = [".jar", ".jar.disabled"],
        ["mods"] = [".jar", ".jar.disabled"],
        ["resourcepack"] = [".zip", ".zip.disabled"],
        ["resourcepacks"] = [".zip", ".zip.disabled"],
        ["shader"] = [".zip", ".zip.disabled"],
        ["shaderpack"] = [".zip", ".zip.disabled"],
        ["shaderpacks"] = [".zip", ".zip.disabled"],
        ["datapack"] = [".zip", ".zip.disabled"],
        ["datapacks"] = [".zip", ".zip.disabled"],
    };

    public string ModrinthApiBase { get; set; } = "https://api.modrinth.com/v2";
    public string ModrinthCdnBase { get; set; } = "";
    private readonly HttpClient _httpClient;

    public ResourceMarketService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public string ModrinthApiUrl(string path)
        => $"{ModrinthApiBase.TrimEnd('/')}/{path.TrimStart('/')}";

    public string RewriteModrinthDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(ModrinthCdnBase) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (!uri.Host.Contains("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return ModrinthCdnBase.TrimEnd('/') + uri.PathAndQuery;
    }

    public async Task<JsonArray> SearchModrinthResourcesAsync(string query, string resourceType, string gameVersion = "", string loader = "", int limit = 25, string index = "relevance", CancellationToken cancellationToken = default)
    {
        var attempts = new List<(string GameVersion, string Loader)> { (gameVersion, loader) };
        if (!string.IsNullOrWhiteSpace(loader))
        {
            attempts.Add((gameVersion, ""));
        }

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            attempts.Add(("", loader));
        }

        attempts.Add(("", ""));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attempt in attempts)
        {
            var key = $"{attempt.GameVersion}\n{attempt.Loader}";
            if (!seen.Add(key))
            {
                continue;
            }

            var hits = await SearchModrinthResourcesStrictAsync(query, resourceType, attempt.GameVersion, attempt.Loader, limit, index, cancellationToken);
            if (hits.Count == 0)
            {
                continue;
            }

            var relaxed = !string.Equals(attempt.GameVersion, gameVersion, StringComparison.OrdinalIgnoreCase)
                          || !string.Equals(attempt.Loader, loader, StringComparison.OrdinalIgnoreCase);
            foreach (var hit in hits.OfType<JsonObject>())
            {
                hit["source"] = "modrinth";
                hit["relaxed_search"] = relaxed;
                hit["target_game_version"] = gameVersion;
                hit["target_loader"] = loader;
                hit["compatible"] = true;
                hit["compatibility_unverified"] = true;
                hit["compatibility_checking"] = true;
                if (!HitHasModrinthCompatibility(hit, resourceType, gameVersion, loader))
                {
                    hit["compatibility_hint"] = "搜索结果未列出目标版本或加载器，安装时会重新确认";
                }
            }

            return hits;
        }

        return [];
    }

    public async Task<JsonArray> SearchModrinthResourcesStrictAsync(string query, string resourceType, string gameVersion = "", string loader = "", int limit = 25, string index = "relevance", CancellationToken cancellationToken = default)
    {
        var facets = new List<string>();
        if (!string.IsNullOrWhiteSpace(resourceType))
        {
            facets.Add($"[\"project_type:{resourceType}\"]");
        }

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            facets.Add($"[\"versions:{gameVersion}\"]");
        }

        if (resourceType == "mod" && !string.IsNullOrWhiteSpace(loader))
        {
            facets.Add($"[\"categories:{loader}\"]");
        }

        var url = ModrinthApiUrl($"search?query={Uri.EscapeDataString(query)}&limit={limit}&index={Uri.EscapeDataString(index)}");
        if (facets.Count > 0)
        {
            url += "&facets=" + Uri.EscapeDataString("[" + string.Join(',', facets) + "]");
        }

        var response = await _httpClient.GetFromJsonAsync<JsonObject>(url, cancellationToken);
        var hits = response?["hits"] as JsonArray ?? [];
        foreach (var hit in hits.OfType<JsonObject>())
        {
            hit["source"] = "modrinth";
        }

        return hits;
    }

    public async Task<JsonArray> GetModrinthVersionsAsync(string projectId, string resourceType, string gameVersion = "", string loader = "", CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query.Add("game_versions=" + Uri.EscapeDataString("[\"" + gameVersion + "\"]"));
        }

        if (resourceType == "mod" && !string.IsNullOrWhiteSpace(loader))
        {
            query.Add("loaders=" + Uri.EscapeDataString("[\"" + loader + "\"]"));
        }

        var url = ModrinthApiUrl($"project/{Uri.EscapeDataString(projectId)}/version");
        if (query.Count > 0)
        {
            url += "?" + string.Join('&', query);
        }

        return await _httpClient.GetFromJsonAsync<JsonArray>(url, cancellationToken) ?? [];
    }

    public static bool ModrinthVersionIsCompatible(JsonObject version, string resourceType, string gameVersion = "", string loader = "")
    {
        var gameVersions = version["game_versions"]?.AsArray()
            .Select(item => item?.GetValue<string>()?.ToLowerInvariant() ?? "")
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var loaders = version["loaders"]?.AsArray()
            .Select(item => item?.GetValue<string>()?.ToLowerInvariant() ?? "")
            .ToHashSet(StringComparer.Ordinal) ?? [];
        if (!string.IsNullOrWhiteSpace(gameVersion) && !gameVersions.Contains(gameVersion.ToLowerInvariant()))
        {
            return false;
        }

        if (resourceType == "mod" && !string.IsNullOrWhiteSpace(loader) && !loaders.Contains(loader.ToLowerInvariant()))
        {
            return false;
        }

        return true;
    }

    public static bool HitHasModrinthCompatibility(JsonObject hit, string resourceType, string gameVersion = "", string loader = "")
    {
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            var versions = hit["versions"]?.AsArray()
                .Select(item => item?.GetValue<string>()?.ToLowerInvariant() ?? "")
                .ToHashSet(StringComparer.Ordinal) ?? [];
            if (versions.Count > 0 && !versions.Contains(gameVersion.ToLowerInvariant()))
            {
                return false;
            }
        }

        if (resourceType == "mod" && !string.IsNullOrWhiteSpace(loader))
        {
            var categories = hit["categories"]?.AsArray()
                .Select(item => item?.GetValue<string>()?.ToLowerInvariant() ?? "")
                .ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var item in hit["display_categories"]?.AsArray() ?? [])
            {
                var category = item?.GetValue<string>()?.ToLowerInvariant() ?? "";
                if (category.Length > 0)
                {
                    categories.Add(category);
                }
            }

            if (categories.Count > 0 && !categories.Contains(loader.ToLowerInvariant()))
            {
                return false;
            }
        }

        return true;
    }

    public async Task<ModrinthCompatibilityResult> CheckModrinthCompatibilityAsync(string projectId, string resourceType, string gameVersion, string loader, CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await FindCompatibleModrinthVersionAsync(projectId, resourceType, gameVersion, loader, cancellationToken);
            return new ModrinthCompatibilityResult(
                version is not null,
                version?["version_number"]?.GetValue<string>() ?? "",
                "");
        }
        catch (Exception exc)
        {
            return new ModrinthCompatibilityResult(false, "", exc.Message);
        }
    }

    public async Task<JsonObject?> FindCompatibleModrinthVersionAsync(string projectId, string resourceType, string gameVersion, string loader, CancellationToken cancellationToken = default)
    {
        var versions = await GetModrinthVersionsAsync(projectId, resourceType, gameVersion, loader, cancellationToken);
        if (versions.Count == 0 && (!string.IsNullOrWhiteSpace(gameVersion) || !string.IsNullOrWhiteSpace(loader)))
        {
            versions = await GetModrinthVersionsAsync(projectId, resourceType, "", "", cancellationToken);
            versions = new JsonArray(versions.OfType<JsonObject>()
                .Where(item => ModrinthVersionIsCompatible(item, resourceType, gameVersion, loader))
                .Select(item => JsonNode.Parse(item.ToJsonString()))
                .ToArray());
        }

        return versions.OfType<JsonObject>()
                   .Where(item => item["version_type"]?.GetValue<string>() == "release")
                   .FirstOrDefault()
               ?? versions.OfType<JsonObject>().FirstOrDefault();
    }

    public async Task<JsonObject> SelectModrinthVersionAsync(string projectId, string resourceType, string gameVersion, string loader, CancellationToken cancellationToken = default)
    {
        var selected = await FindCompatibleModrinthVersionAsync(projectId, resourceType, gameVersion, loader, cancellationToken);
        if (selected is not null)
        {
            return selected;
        }

        var target = string.IsNullOrWhiteSpace(gameVersion) ? "当前 Minecraft 版本" : $"Minecraft {gameVersion}";
        if (resourceType == "mod" && !string.IsNullOrWhiteSpace(loader))
        {
            target += $" / {loader}";
        }

        throw new InvalidOperationException($"这个资源没有适配 {target} 的可安装文件。");
    }

    public async Task<RemoteResourceDetail> GetModrinthResourceDetailAsync(string projectId, string resourceType, string gameVersion, string loader, bool cacheScreenshots = true, CancellationToken cancellationToken = default)
    {
        var project = await _httpClient.GetFromJsonAsync<JsonObject>(ModrinthApiUrl($"project/{Uri.EscapeDataString(projectId)}"), cancellationToken)
                      ?? throw new InvalidOperationException("Modrinth 项目详情为空。");
        var versions = await GetModrinthVersionsAsync(projectId, resourceType, gameVersion, loader, cancellationToken);
        var selected = versions.OfType<JsonObject>().FirstOrDefault();
        var screenshots = new List<ResourceScreenshot>();
        foreach (var item in project["gallery"]?.AsArray().OfType<JsonObject>().Take(5) ?? [])
        {
            var url = item["url"]?.GetValue<string>() ?? "";
            if (url.Length == 0)
            {
                continue;
            }

            var cached = "";
            if (cacheScreenshots)
            {
                try
                {
                    cached = await CacheResourceScreenshotAsync(url, cancellationToken);
                }
                catch
                {
                    cached = "";
                }
            }

            screenshots.Add(new ResourceScreenshot(url, cached));
        }

        var dependencies = selected?["dependencies"]?.AsArray().OfType<JsonObject>()
            .Where(item => item["dependency_type"]?.GetValue<string>() is "required" or "optional")
            .Select(item => new ResourceDependency(
                item["dependency_type"]?.GetValue<string>() ?? "",
                item["project_id"]?.GetValue<string>() ?? "",
                item["version_id"]?.GetValue<string>() ?? ""))
            .ToList() ?? [];
        var versionSummaries = versions.OfType<JsonObject>()
            .Take(8)
            .Select(item => new ResourceVersionSummary(
                item["version_number"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? "",
                item["version_type"]?.GetValue<string>() ?? "",
                string.Join(", ", item["game_versions"]?.AsArray().Select(value => value?.GetValue<string>() ?? "").Where(value => value.Length > 0) ?? []),
                string.Join(", ", item["loaders"]?.AsArray().Select(value => value?.GetValue<string>() ?? "").Where(value => value.Length > 0) ?? [])))
            .ToList();
        var projectType = project["project_type"]?.GetValue<string>() ?? resourceType;
        var slug = project["slug"]?.GetValue<string>() ?? projectId;
        return new RemoteResourceDetail(
            "modrinth",
            project["title"]?.GetValue<string>() ?? projectId,
            project["description"]?.GetValue<string>() ?? "",
            project["body"]?.GetValue<string>() ?? "",
            project["downloads"]?.GetValue<int>() ?? 0,
            project["followers"]?.GetValue<int>() ?? 0,
            $"https://modrinth.com/{projectType}/{slug}",
            screenshots,
            dependencies,
            versionSummaries,
            "");
    }

    public async Task<string> CacheResourceScreenshotAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return "";
        }

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif"))
        {
            extension = ".jpg";
        }

        var cacheDir = Path.Combine(Path.GetTempPath(), "mcgo-resource-screenshots");
        Directory.CreateDirectory(cacheDir);
        var targetPath = Path.Combine(cacheDir, FileUtilities.SanitizeFilename(FileUtilities.Sha1Text(url), "screenshot") + extension);
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        {
            return targetPath;
        }

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(targetPath);
        await source.CopyToAsync(target, cancellationToken);
        return targetPath;
    }

    public async Task<ModrinthInstallResult> InstallModrinthResourceAsync(
        string projectId,
        string resourceType,
        string gameVersion,
        string loader,
        string targetDir,
        bool installDependencies = false,
        HashSet<string>? installed = null,
        Action<string>? status = null,
        IProgress<ResourceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        installed ??= [];
        var projectKey = "modrinth:" + projectId;
        if (!installed.Add(projectKey))
        {
            return new ModrinthInstallResult("", "", "", []);
        }

        var selected = await SelectModrinthVersionAsync(projectId, resourceType, gameVersion, loader, cancellationToken);
        var dependenciesInstalled = new List<string>();
        if (installDependencies)
        {
            foreach (var dependency in selected["dependencies"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (dependency["dependency_type"]?.GetValue<string>() != "required")
                {
                    continue;
                }

                var depProjectId = dependency["project_id"]?.GetValue<string>() ?? "";
                if (depProjectId.Length == 0)
                {
                    continue;
                }

                status?.Invoke($"正在安装依赖：{depProjectId}");
                var depPayload = await InstallModrinthResourceAsync(depProjectId, resourceType, gameVersion, loader, targetDir, true, installed, status, progress, cancellationToken);
                if (!string.IsNullOrWhiteSpace(depPayload.Filename))
                {
                    dependenciesInstalled.Add(depPayload.Filename);
                }

                dependenciesInstalled.AddRange(depPayload.DependenciesInstalled);
            }
        }

        var files = selected["files"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var primary = files.FirstOrDefault(item => item["primary"]?.GetValue<bool>() == true) ?? files.FirstOrDefault();
        var downloadUrl = primary?["url"]?.GetValue<string>() ?? "";
        if (downloadUrl.Length == 0)
        {
            throw new InvalidOperationException("资源版本缺少可下载文件。");
        }

        var filename = FileUtilities.SanitizeFilename(primary?["filename"]?.GetValue<string>() ?? $"{projectId}.jar", $"{projectId}.jar");
        Directory.CreateDirectory(targetDir);
        var targetPath = Path.Combine(targetDir, filename);
        status?.Invoke($"正在下载资源：{filename}");
        await StreamDownloadAsync(RewriteModrinthDownloadUrl(downloadUrl), targetPath, progress, "下载资源", cancellationToken);
        var expectedSha1 = primary?["hashes"]?["sha1"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrWhiteSpace(expectedSha1) && !FileUtilities.Sha1File(targetPath).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("资源文件 SHA1 校验失败。");
        }

        return new ModrinthInstallResult(filename, targetPath, selected["version_number"]?.GetValue<string>() ?? "", dependenciesInstalled);
    }

    public async Task<ModrinthDependencyInstallResult> InstallMissingModrinthDependenciesAsync(string gameVersion, string loader, string targetDir, Action<string>? status = null, IProgress<ResourceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var plan = await ResolveModrinthDependencyPlanAsync(targetDir, cancellationToken);
        var installedProjects = plan.InstalledProjectIds.Select(projectId => "modrinth:" + projectId).ToHashSet(StringComparer.Ordinal);
        var installedFiles = new List<string>();
        var total = Math.Max(1, plan.MissingDependencies.Count);
        for (var index = 0; index < plan.MissingDependencies.Count; index++)
        {
            var dependency = plan.MissingDependencies[index];
            status?.Invoke($"正在安装缺失前置 {index + 1}/{plan.MissingDependencies.Count}：{dependency.ProjectId}");
            var payload = await InstallModrinthResourceAsync(
                dependency.ProjectId,
                "mod",
                gameVersion,
                loader,
                targetDir,
                true,
                installedProjects,
                status,
                new Progress<ResourceDownloadProgress>(snapshot =>
                {
                    progress?.Report(snapshot with { Progress = (index + snapshot.Progress) / total });
                }),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload.Filename))
            {
                installedFiles.Add(payload.Filename);
            }

            installedFiles.AddRange(payload.DependenciesInstalled);
        }

        progress?.Report(new ResourceDownloadProgress(1, "Mod 前置补齐完成", ""));
        return new ModrinthDependencyInstallResult(plan, installedFiles);
    }

    public static string ResourceDirectoryForType(string gameDir, JsonObject settings, string versionId, string resourceType, bool globalIsolation = false)
    {
        var runtimeDir = Minecraft.VersionSettingsService.RuntimeDirectoryForVersion(gameDir, settings, versionId, globalIsolation);
        return resourceType switch
        {
            "mod" or "mods" => Path.Combine(runtimeDir, "mods"),
            "resourcepack" or "resourcepacks" => Path.Combine(runtimeDir, "resourcepacks"),
            "shader" or "shaderpack" or "shaderpacks" => Path.Combine(runtimeDir, "shaderpacks"),
            "datapack" or "datapacks" => Path.Combine(runtimeDir, "datapacks"),
            _ => runtimeDir,
        };
    }

    public static IReadOnlyList<LocalResourceInfo> ListLocalResources(string query, string resourceType, string targetDir)
    {
        if (!Directory.Exists(targetDir))
        {
            return [];
        }

        var loweredQuery = query.Trim().ToLowerInvariant();
        var extensions = ResourceExtensions.TryGetValue(resourceType, out var configured) ? configured : [".jar", ".zip", ".disabled"];
        var hits = new List<LocalResourceInfo>();
        foreach (var path in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
        {
            var filename = Path.GetFileName(path);
            if (!extensions.Any(extension => filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(targetDir, path);
            if (loweredQuery.Length > 0
                && !filename.Contains(loweredQuery, StringComparison.OrdinalIgnoreCase)
                && !relativePath.Contains(loweredQuery, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(path);
            hits.Add(new LocalResourceInfo(
                filename,
                path,
                relativePath,
                info.Length,
                info.LastWriteTime,
                filename.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? "禁用" : "启用"));
        }

        return hits.OrderByDescending(item => item.ModifiedAt).Take(100).ToList();
    }

    public static LocalResourceDetail GetLocalResourceDetail(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("资源文件不存在。", path);
        }

        var info = new FileInfo(path);
        var hints = AnalyzeLocalModFile(path, Path.GetDirectoryName(path) ?? "");
        return new LocalResourceDetail(
            info.Name,
            info.FullName,
            info.Length,
            info.LastWriteTime,
            info.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? "禁用" : "启用",
            FileUtilities.Sha1File(info.FullName),
            hints);
    }

    public static IReadOnlyList<string> AnalyzeLocalModFile(string path, string modsDir)
    {
        var filename = Path.GetFileName(path);
        var lowered = filename.ToLowerInvariant();
        var hints = new List<string>();
        if (lowered.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
        {
            hints.Add("当前不会被 Minecraft 加载");
        }

        if (!lowered.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            && !lowered.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
        {
            return hints;
        }

        var available = Directory.Exists(modsDir)
            ? Directory.EnumerateFiles(modsDir, "*", SearchOption.TopDirectoryOnly)
                .Select(item => Path.GetFileName(item).ToLowerInvariant().Replace(".disabled", ""))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        foreach (var dependency in ReadJarDependencyIds(path))
        {
            if (DependencyLooksPresent(dependency, available))
            {
                continue;
            }

            hints.Add($"可能缺少前置：{dependency}");
        }

        if (hints.Count == 0 && !available.Any(item => item.Contains("fabric-api", StringComparison.Ordinal)) && lowered.Contains("fabric", StringComparison.Ordinal))
        {
            hints.Add("可能需要 Fabric API");
        }

        return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string SetLocalResourceEnabled(string path, bool enabled)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("资源文件不存在。", path);
        }

        var targetPath = enabled
            ? path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^".disabled".Length] : path
            : path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path : path + ".disabled";
        if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return targetPath;
        }

        if (File.Exists(targetPath))
        {
            throw new IOException("目标文件已存在：" + targetPath);
        }

        File.Move(path, targetPath);
        return targetPath;
    }

    public static void DeleteLocalResource(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("资源文件不存在。", path);
        }

        File.Delete(path);
    }

    public async Task<ModrinthDependencyPlan> ResolveModrinthDependencyPlanAsync(string modsDir, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(modsDir))
        {
            return new ModrinthDependencyPlan([], []);
        }

        var hashes = Directory.GetFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(FileUtilities.Sha1File)
            .Where(hash => hash.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (hashes.Count == 0)
        {
            return new ModrinthDependencyPlan([], []);
        }

        var response = await _httpClient.PostAsJsonAsync(ModrinthApiUrl("version_files"), new JsonObject
        {
            ["hashes"] = new JsonArray(hashes.Select(hash => JsonValue.Create(hash)).ToArray()),
            ["algorithm"] = "sha1",
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? [];
        var installedProjectIds = data.Select(pair => pair.Value?["project_id"]?.GetValue<string>() ?? "")
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var installedSet = installedProjectIds.ToHashSet(StringComparer.Ordinal);
        var missing = new Dictionary<string, MissingModrinthDependency>(StringComparer.Ordinal);

        foreach (var version in data.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var requiredBy = version["name"]?.GetValue<string>()
                             ?? version["project_id"]?.GetValue<string>()
                             ?? "未知 Mod";
            foreach (var dependency in version["dependencies"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (dependency["dependency_type"]?.GetValue<string>() != "required")
                {
                    continue;
                }

                var projectId = dependency["project_id"]?.GetValue<string>() ?? "";
                if (projectId.Length == 0 || installedSet.Contains(projectId))
                {
                    continue;
                }

                var versionId = dependency["version_id"]?.GetValue<string>() ?? "";
                if (!missing.TryGetValue(projectId, out var existing))
                {
                    existing = new MissingModrinthDependency(projectId, versionId, []);
                    missing[projectId] = existing;
                }

                if (!existing.RequiredBy.Contains(requiredBy))
                {
                    existing.RequiredBy.Add(requiredBy);
                }
            }
        }

        return new ModrinthDependencyPlan(installedProjectIds, missing.Values.OrderBy(item => item.ProjectId, StringComparer.Ordinal).ToList());
    }

    private static IReadOnlyList<string> ReadJarDependencyIds(string path)
    {
        var dependencies = new List<string>();
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var fabricMetadata = archive.GetEntry("fabric.mod.json");
            if (fabricMetadata is not null)
            {
                using var reader = new StreamReader(fabricMetadata.Open());
                var metadata = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
                foreach (var dependency in metadata?["depends"]?.AsObject() ?? [])
                {
                    if (dependency.Key is "java" or "minecraft" or "fabricloader")
                    {
                        continue;
                    }

                    dependencies.Add(dependency.Key);
                }
            }

            foreach (var entryName in new[] { "META-INF/mods.toml", "META-INF/neoforge.mods.toml" })
            {
                var forgeMetadata = archive.GetEntry(entryName);
                if (forgeMetadata is null)
                {
                    continue;
                }

                using var reader = new StreamReader(forgeMetadata.Open());
                var text = reader.ReadToEnd();
                foreach (var dependency in ExtractForgeDependencyIds(text))
                {
                    dependencies.Add(dependency);
                }
            }
        }
        catch
        {
        }

        return dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> ExtractForgeDependencyIds(string text)
    {
        using var reader = new StringReader(text);
        var inDependency = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[[dependencies.", StringComparison.Ordinal))
            {
                inDependency = true;
                continue;
            }

            if (trimmed.StartsWith("[[", StringComparison.Ordinal))
            {
                inDependency = false;
                continue;
            }

            if (!inDependency || !trimmed.StartsWith("modId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed.Split('=', 2).Last().Trim().Trim('"', '\'');
            if (value is not ("minecraft" or "forge" or "neoforge") && value.Length > 0)
            {
                yield return value;
            }
        }
    }

    private static bool DependencyLooksPresent(string dependency, IReadOnlySet<string> available)
    {
        var normalized = dependency.Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        return available.Any(filename => filename.Contains(normalized, StringComparison.Ordinal)
                                         || filename.Contains(dependency.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private async Task StreamDownloadAsync(string url, string filePath, IProgress<ResourceDownloadProgress>? progress, string phase, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        long downloaded = 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(filePath);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            progress?.Report(new ResourceDownloadProgress(total > 0 ? Math.Min(1, (double)downloaded / total) : 0, phase, Path.GetFileName(filePath)));
        }

        progress?.Report(new ResourceDownloadProgress(1, phase, Path.GetFileName(filePath)));
    }
}

public sealed record ModrinthDependencyPlan(IReadOnlyList<string> InstalledProjectIds, IReadOnlyList<MissingModrinthDependency> MissingDependencies);

public sealed record MissingModrinthDependency(string ProjectId, string VersionId, List<string> RequiredBy);

public sealed record ResourceDownloadProgress(double Progress, string Phase, string CurrentFile);

public sealed record ModrinthInstallResult(string Filename, string Path, string Version, IReadOnlyList<string> DependenciesInstalled);

public sealed record ModrinthDependencyInstallResult(ModrinthDependencyPlan Plan, IReadOnlyList<string> DependenciesInstalled);

public sealed record LocalResourceInfo(string Title, string Path, string RelativePath, long SizeBytes, DateTime ModifiedAt, string Status);

public sealed record LocalResourceDetail(string Title, string Path, long SizeBytes, DateTime ModifiedAt, string Status, string Sha1, IReadOnlyList<string> Hints);

public sealed record ModrinthCompatibilityResult(bool Compatible, string VersionNumber, string Error);

public sealed record RemoteResourceDetail(
    string Source,
    string Title,
    string Description,
    string Body,
    int Downloads,
    int Followers,
    string ProjectUrl,
    IReadOnlyList<ResourceScreenshot> Screenshots,
    IReadOnlyList<ResourceDependency> Dependencies,
    IReadOnlyList<ResourceVersionSummary> Versions,
    string Status);

public sealed record ResourceScreenshot(string Url, string LocalPath);

public sealed record ResourceDependency(string Type, string ProjectId, string VersionId);

public sealed record ResourceVersionSummary(string Name, string ReleaseType, string GameVersions, string Loaders);
