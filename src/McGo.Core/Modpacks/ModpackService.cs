using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McGo.Core.Minecraft;
using McGo.Core.Utilities;

namespace McGo.Core.Modpacks;

public sealed class ModpackService
{
    private readonly LauncherService _launcher;
    private readonly VersionSettingsService _versionSettings;
    private readonly HttpClient _httpClient;

    public ModpackService(LauncherService? launcher = null, HttpClient? httpClient = null)
    {
        _launcher = launcher ?? new LauncherService();
        _versionSettings = new VersionSettingsService(_launcher);
        _httpClient = httpClient ?? new HttpClient();
    }

    public static bool ShouldSkipPackFile(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        var lowered = normalized.ToLowerInvariant();
        return normalized.Length == 0
               || lowered.StartsWith(".git/")
               || lowered.StartsWith("__pycache__/")
               || lowered.StartsWith("logs/")
               || lowered.StartsWith("crash-reports/")
               || lowered is "options.txt" or "launcher_profiles.json"
               || lowered.EndsWith(".log") || lowered.EndsWith(".tmp") || lowered.EndsWith(".lock");
    }

    public static IEnumerable<(string Path, string Relative)> IterPackFiles(string rootDir)
    {
        if (!Directory.Exists(rootDir))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(rootDir, path).Replace('\\', '/');
            if (!ShouldSkipPackFile(relative))
            {
                yield return (path, relative);
            }
        }
    }

    public (string Loader, string Version) DetectExportLoader(string gameDir, string versionId)
    {
        var versionJson = _launcher.GetVersionJson(gameDir, versionId) ?? [];
        var libraries = string.Join(' ', versionJson["libraries"]?.AsArray().OfType<JsonObject>().Select(item => item["name"]?.GetValue<string>() ?? "") ?? []);
        var text = string.Join(' ', versionId, versionJson["id"]?.GetValue<string>() ?? "", versionJson["inheritsFrom"]?.GetValue<string>() ?? "", libraries).ToLowerInvariant();
        return MatchLoader(text, "fabric-loader")
               ?? MatchLoader(text, "neoforge")
               ?? (text.Contains("net.minecraftforge") || text.Contains("forge-") ? MatchLoader(text, "forge") : null)
               ?? MatchLoader(text, "quilt-loader")
               ?? ("", "");
    }

    public int ExportModpack(string gameDir, JsonObject settings, string versionId, string targetPath, string packFormat = "modrinth", bool globalIsolation = false)
    {
        var runtimeDir = VersionSettingsService.RuntimeDirectoryForVersion(gameDir, settings, versionId, globalIsolation);
        if (!Directory.Exists(runtimeDir))
        {
            throw new InvalidOperationException($"运行目录不存在：{runtimeDir}");
        }

        var minecraftVersion = _versionSettings.ResolveBaseMinecraftVersion(gameDir, versionId);
        var packName = VersionSettingsService.VersionDisplayName(settings, versionId);
        if (Path.GetExtension(targetPath).Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
        {
            packFormat = "modrinth";
        }

        var (loaderKey, loaderVersion) = DetectExportLoader(gameDir, versionId);
        var added = 0;
        using var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create);
        foreach (var (path, relative) in IterPackFiles(runtimeDir))
        {
            archive.CreateEntryFromFile(path, "overrides/" + relative, CompressionLevel.Optimal);
            added++;
        }

        if (packFormat == "modrinth")
        {
            var dependencies = new JsonObject { ["minecraft"] = minecraftVersion };
            if (!string.IsNullOrWhiteSpace(loaderKey) && !string.IsNullOrWhiteSpace(loaderVersion)) dependencies[loaderKey] = loaderVersion;
            var index = new JsonObject
            {
                ["formatVersion"] = 1,
                ["game"] = "minecraft",
                ["versionId"] = FileUtilities.SanitizeFilename(versionId, "version"),
                ["name"] = packName,
                ["summary"] = $"Exported by McGo from {versionId}",
                ["files"] = new JsonArray(),
                ["dependencies"] = dependencies,
            };
            WriteZipText(archive, "modrinth.index.json", index.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else if (packFormat == "curseforge")
        {
            var loaderId = loaderKey == "fabric-loader" ? (loaderVersion.Length > 0 ? $"fabric-{loaderVersion}" : "fabric") : loaderKey.Length > 0 ? (loaderVersion.Length > 0 ? $"{loaderKey}-{loaderVersion}" : loaderKey) : "";
            var manifest = new JsonObject
            {
                ["minecraft"] = new JsonObject
                {
                    ["version"] = minecraftVersion,
                    ["modLoaders"] = loaderId.Length > 0 ? new JsonArray(new JsonObject { ["id"] = loaderId, ["primary"] = true }) : new JsonArray(),
                },
                ["manifestType"] = "minecraftModpack",
                ["manifestVersion"] = 1,
                ["name"] = packName,
                ["version"] = "1.0.0",
                ["author"] = "McGo",
                ["files"] = new JsonArray(),
                ["overrides"] = "overrides",
            };
            WriteZipText(archive, "manifest.json", manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        return added;
    }

    public async Task<ModpackImportResult> ImportModpackAsync(
        string packPath,
        string gameDir,
        JsonObject settings,
        string targetVersionId = "",
        bool globalIsolation = true,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packPath))
        {
            throw new FileNotFoundException("整合包文件不存在。", packPath);
        }

        try
        {
            using var archive = ZipFile.OpenRead(packPath);
            var modrinthEntry = FindEntry(archive, ["modrinth.index.json"]);
            var curseForgeEntry = FindEntry(archive, ["manifest.json"]);
            if (modrinthEntry is not null)
            {
                return await ImportModrinthPackAsync(archive, modrinthEntry, packPath, gameDir, settings, targetVersionId, globalIsolation, status, cancellationToken);
            }

            if (curseForgeEntry is not null)
            {
                return await ImportCurseForgePackAsync(archive, curseForgeEntry, packPath, gameDir, settings, targetVersionId, globalIsolation, status, cancellationToken);
            }

            return ImportPlainZip(archive, packPath, gameDir, settings, targetVersionId);
        }
        catch (InvalidDataException exc)
        {
            throw new InvalidOperationException("当前仅支持 zip/mrpack 格式整合包。", exc);
        }
    }

    public ModpackImportPreview PreviewImportModpack(string packPath, string gameDir, JsonObject settings, string targetVersionId = "", bool globalIsolation = true)
    {
        if (!File.Exists(packPath))
        {
            throw new FileNotFoundException("整合包文件不存在。", packPath);
        }

        using var archive = ZipFile.OpenRead(packPath);
        var modrinthEntry = FindEntry(archive, ["modrinth.index.json"]);
        var curseForgeEntry = FindEntry(archive, ["manifest.json"]);
        string packName;
        string minecraftVersion;
        string versionId;
        string runtimeDir;
        List<string> relativeFiles;

        if (modrinthEntry is not null)
        {
            var index = ReadJson(modrinthEntry);
            var dependencies = index["dependencies"] as JsonObject ?? [];
            minecraftVersion = dependencies["minecraft"]?.GetValue<string>() ?? "";
            packName = index["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(packPath);
            versionId = ResolveImportedVersionId(targetVersionId, packName, "Modrinth-" + minecraftVersion);
            runtimeDir = PreviewRuntimeDir(gameDir, settings, versionId, globalIsolation);
            var basePrefix = EntryDirectory(modrinthEntry);
            relativeFiles = PreviewPrefixFiles(archive, [basePrefix + "overrides", basePrefix + "client-overrides"]);
        }
        else if (curseForgeEntry is not null)
        {
            var manifest = ReadJson(curseForgeEntry);
            var minecraft = manifest["minecraft"] as JsonObject ?? [];
            minecraftVersion = minecraft["version"]?.GetValue<string>() ?? "";
            packName = manifest["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(packPath);
            versionId = ResolveImportedVersionId(targetVersionId, packName, "CurseForge-" + minecraftVersion);
            runtimeDir = PreviewRuntimeDir(gameDir, settings, versionId, globalIsolation);
            var overrideDir = manifest["overrides"]?.GetValue<string>()?.Trim('/').Trim('\\') is { Length: > 0 } overrides ? overrides : "overrides";
            relativeFiles = PreviewPrefixFiles(archive, [EntryDirectory(curseForgeEntry) + overrideDir]);
        }
        else
        {
            packName = Path.GetFileNameWithoutExtension(packPath);
            minecraftVersion = "";
            versionId = ResolveImportedVersionId(targetVersionId, packName, "ImportedPack");
            runtimeDir = PreviewRuntimeDir(gameDir, settings, versionId, true);
            relativeFiles = PreviewPrefixFiles(archive, [""]);
        }

        var conflicts = relativeFiles
            .Where(relative => File.Exists(SafeJoin(runtimeDir, relative)))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();
        return new ModpackImportPreview(
            versionId,
            packName,
            minecraftVersion,
            runtimeDir,
            Directory.Exists(Path.Combine(gameDir, "versions", versionId)),
            relativeFiles.Count,
            conflicts);
    }

    private static readonly (string Title, string[] Keywords, string Advice)[] CrashRules =
    [
        ("Java 版本不兼容", ["unsupported class file major version", "has been compiled by a more recent version", "java.lang.unsupportedclassversionerror"],
            "该整合包/Mod 需要不同的 Java 版本，请在 Java 管理页切换到匹配的版本后重试。"),
        ("Mod 缺少依赖", ["mod requires", "requires version", "missing dependencies", "modresolutionexception", "requires mod", "requires the mod"],
            "根据日志补齐缺失的前置 Mod，或在资源市场自动解析依赖后重新安装。"),
        ("Mod 冲突或加载失败", ["failed to load mod", "mod loading has failed", "exception loading mod", "broken mod"],
            "逐个禁用可疑 Mod 定位冲突项，确认所有 Mod 与当前游戏/加载器版本匹配。"),
        ("Mixin 注入失败", ["mixin apply failed", "invalidmixinexception", "mixintransformererror", "mixinapplyerror"],
            "通常由 Mod 版本不匹配或冲突引起，更新相关 Mod 或核对加载器版本。"),
        ("重复或不兼容的 Mod", ["duplicate mod", "found a duplicate mod", "incompatiblemodset", "conflicting mod"],
            "删除重复的 Mod jar，同一个 Mod 只保留一个版本。"),
        ("显卡驱动 / OpenGL 问题", ["opengl", "glfw error", "pixel format not accelerated", "failed to create window", "lwjgl"],
            "更新显卡驱动，安装/更新 Sodium 或 OptiFine，必要时关闭光影或降低画质。"),
        ("原生库加载失败", ["unsatisfiedlinkerror", "failed to locate library", "no lwjgl in java.library.path", "openal"],
            "在下载页对该版本执行“修复文件”，确认 natives 已正确解压。"),
        ("内存不足", ["outofmemoryerror", "java heap space", "unable to allocate", "gc overhead limit"],
            "在版本设置中增大最大内存(-Xmx)，或减少 Mod 数量与区块加载距离。"),
        ("Java 位数或堆设置错误", ["could not reserve enough space for", "invalid maximum heap size", "32-bit"],
            "改用 64 位 Java，或把最大内存调整到系统可分配的范围内。"),
        ("认证或网络问题", ["invalid session", "connection timed out", "unknownhostexception", "failed to verify username", "authentication failed"],
            "重新登录账号刷新令牌，检查网络连通性或下载镜像设置。"),
        ("端口被占用", ["address already in use", "bindexception"],
            "关闭占用该端口的程序，或更换服务器/联机端口后重试。"),
        ("磁盘空间不足", ["no space left on device", "not enough space", "enospc"],
            "清理磁盘空间后重试。"),
        ("资源包或配置损坏", ["malformed", "jsonparseexception", "invalid byte", "could not parse", "unexpected character"],
            "删除损坏的配置或资源包文件，或从整合包重新导入覆盖。"),
        ("存档或世界损坏", ["failed to load level", "corrupted chunk", "leveldbexception"],
            "先备份存档再尝试修复，可用区域文件工具排查损坏的区块。"),
    ];

    public static string AnalyzeCrashLogs(string gameDir, string versionId, string runtimeDir)
    {
        var candidates = new List<string>();
        foreach (var baseDir in new[] { runtimeDir, Path.Combine(gameDir, "versions", versionId), gameDir }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var latest = Path.Combine(baseDir, "logs", "latest.log");
            if (File.Exists(latest)) candidates.Add(latest);
            var crashDir = Path.Combine(baseDir, "crash-reports");
            if (Directory.Exists(crashDir))
            {
                candidates.AddRange(Directory.GetFiles(crashDir, "*.txt").OrderByDescending(File.GetLastWriteTimeUtc).Take(3));
            }
        }

        var unique = candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        if (unique.Count == 0) return "未找到 latest.log 或 crash-reports。";

        var segments = new List<string>();
        foreach (var path in unique.Take(4))
        {
            try
            {
                var content = File.ReadAllText(path);
                if (content.Length > 120000) content = content[^120000..];
                segments.Add(content);
            }
            catch (Exception exc)
            {
                segments.Add($"[无法读取 {path}: {exc.Message}]");
            }
        }

        var text = string.Join('\n', segments);
        var lowered = text.ToLowerInvariant();
        var findings = CrashRules.Where(rule => rule.Keywords.Any(lowered.Contains)).ToList();
        var keyLines = ExtractKeyCrashLines(text);

        var result = new List<string> { $"分析文件：{unique.Count} 个" };
        if (findings.Count > 0)
        {
            result.Add("可能原因：" + string.Join("；", findings.Select(item => item.Title)));
            result.Add("建议处理：");
            result.AddRange(findings.Select(item => $"  - {item.Title}：{item.Advice}"));
        }
        else
        {
            result.Add("未匹配到常见崩溃类型，请查看下方关键行。");
        }

        if (keyLines.Count > 0)
        {
            result.Add("关键行：");
            result.AddRange(keyLines.Select(line => "  " + line));
        }

        result.Add($"最新文件：{unique[0]}");
        return string.Join('\n', result);
    }

    private static List<string> ExtractKeyCrashLines(string text)
    {
        var keyLines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length is 0 or > 400) continue;
            var isKey = line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Caused by:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Exception in thread", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(line, @"(?:[a-z0-9_]+\.)+[A-Za-z0-9_$]*(?:Exception|Error)\b");
            if (!isKey) continue;
            if (seen.Add(line)) keyLines.Add(line);
            if (keyLines.Count >= 6) break;
        }

        return keyLines;
    }

    private static (string Loader, string Version)? MatchLoader(string text, string key)
    {
        if (!text.Contains(key, StringComparison.Ordinal)) return null;
        var match = Regex.Match(text, Regex.Escape(key) + @"[:/-]([0-9][^:\s/]*)");
        return (key, match.Success ? match.Groups[1].Value : "");
    }

    private static void WriteZipText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }

    private async Task<ModpackImportResult> ImportModrinthPackAsync(
        ZipArchive archive,
        ZipArchiveEntry indexEntry,
        string packPath,
        string gameDir,
        JsonObject settings,
        string targetVersionId,
        bool globalIsolation,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        var index = ReadJson(indexEntry);
        var dependencies = index["dependencies"] as JsonObject ?? [];
        var minecraftVersion = dependencies["minecraft"]?.GetValue<string>() ?? "";
        var packName = index["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(packPath);
        var versionId = ResolveImportedVersionId(targetVersionId, packName, "Modrinth-" + minecraftVersion);
        var runtimeDir = PrepareImportedVersion(gameDir, settings, versionId, packName, minecraftVersion, globalIsolation);

        status?.Invoke("正在解压 Modrinth overrides...");
        var basePrefix = EntryDirectory(indexEntry);
        var copied = ExtractOverrides(archive, runtimeDir, [basePrefix + "overrides", basePrefix + "client-overrides"]);
        var missing = new JsonArray();
        var files = index["files"]?.AsArray() ?? [];
        var downloaded = 0;
        foreach (var item in files.OfType<JsonObject>())
        {
            var relative = item["path"]?.GetValue<string>() ?? "";
            var downloads = item["downloads"]?.AsArray().Select(node => node?.GetValue<string>() ?? "").Where(value => value.Length > 0).ToList() ?? [];
            if (relative.Length == 0 || downloads.Count == 0)
            {
                continue;
            }

            status?.Invoke("正在下载整合包文件：" + Path.GetFileName(relative));
            try
            {
                await DownloadFileAsync(downloads[0], SafeJoin(runtimeDir, relative), cancellationToken);
                downloaded++;
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                missing.Add(new JsonObject { ["path"] = relative, ["reason"] = exc.Message, ["downloads"] = JsonNode.Parse(item["downloads"]?.ToJsonString() ?? "[]") });
            }
        }

        var reportPath = WriteMissingReport(gameDir, versionId, missing);
        return new ModpackImportResult(versionId, packName, minecraftVersion, DetectModrinthLoader(dependencies), runtimeDir, copied, downloaded, missing.Count, reportPath);
    }

    private async Task<ModpackImportResult> ImportCurseForgePackAsync(
        ZipArchive archive,
        ZipArchiveEntry manifestEntry,
        string packPath,
        string gameDir,
        JsonObject settings,
        string targetVersionId,
        bool globalIsolation,
        Action<string>? status,
        CancellationToken cancellationToken)
    {
        var manifest = ReadJson(manifestEntry);
        var minecraft = manifest["minecraft"] as JsonObject ?? [];
        var minecraftVersion = minecraft["version"]?.GetValue<string>() ?? "";
        var packName = manifest["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(packPath);
        var versionId = ResolveImportedVersionId(targetVersionId, packName, "CurseForge-" + minecraftVersion);
        var runtimeDir = PrepareImportedVersion(gameDir, settings, versionId, packName, minecraftVersion, globalIsolation);
        var overrideDir = manifest["overrides"]?.GetValue<string>()?.Trim('/').Trim('\\') is { Length: > 0 } overrides ? overrides : "overrides";
        var basePrefix = EntryDirectory(manifestEntry);
        status?.Invoke("正在解压 CurseForge overrides...");
        var copied = ExtractPrefix(archive, basePrefix + overrideDir, runtimeDir);
        var downloaded = 0;
        var missing = new JsonArray();
        var files = manifest["files"]?.AsArray() ?? [];
        foreach (var item in files.OfType<JsonObject>())
        {
            var projectId = item["projectID"]?.GetValue<int?>() ?? item["projectId"]?.GetValue<int?>() ?? 0;
            var fileId = item["fileID"]?.GetValue<int?>() ?? item["fileId"]?.GetValue<int?>() ?? 0;
            var required = item["required"]?.GetValue<bool?>() ?? true;
            if (projectId <= 0 || fileId <= 0)
            {
                continue;
            }

            if (!required)
            {
                missing.Add(new JsonObject { ["projectID"] = projectId, ["fileID"] = fileId, ["reason"] = "可选文件，未自动下载" });
                continue;
            }

            status?.Invoke($"正在下载 CurseForge 文件：{projectId}/{fileId}");
            var filename = $"{projectId}-{fileId}.jar";
            try
            {
                await DownloadFileAsync(CurseForgeFileUrl(fileId), Path.Combine(runtimeDir, "mods", filename), cancellationToken);
                downloaded++;
            }
            catch (Exception exc) when (exc is not OperationCanceledException)
            {
                missing.Add(new JsonObject { ["projectID"] = projectId, ["fileID"] = fileId, ["reason"] = exc.Message, ["manual_url"] = $"https://www.curseforge.com/minecraft/mc-mods/{projectId}/files/{fileId}" });
            }
        }

        var loader = minecraft["modLoaders"]?.AsArray().OfType<JsonObject>().FirstOrDefault(item => item["primary"]?.GetValue<bool>() == true)
            ?? minecraft["modLoaders"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
        var loaderId = loader?["id"]?.GetValue<string>() ?? "";
        var reportPath = WriteMissingReport(gameDir, versionId, missing);
        return new ModpackImportResult(versionId, packName, minecraftVersion, loaderId, runtimeDir, copied, downloaded, missing.Count, reportPath);
    }

    private ModpackImportResult ImportPlainZip(ZipArchive archive, string packPath, string gameDir, JsonObject settings, string targetVersionId)
    {
        var packName = Path.GetFileNameWithoutExtension(packPath);
        var versionId = ResolveImportedVersionId(targetVersionId, packName, "ImportedPack");
        var runtimeDir = PrepareImportedVersion(gameDir, settings, versionId, packName, "", true);
        var copied = ExtractPrefix(archive, "", runtimeDir);
        return new ModpackImportResult(versionId, packName, "", "", runtimeDir, copied, 0, 0, "");
    }

    private static string PreviewRuntimeDir(string gameDir, JsonObject settings, string versionId, bool globalIsolation)
    {
        if (settings[versionId] is JsonObject entry)
        {
            var customRuntime = entry["runtime_directory"]?.GetValue<string>()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(customRuntime))
            {
                return Path.GetFullPath(customRuntime);
            }

            if (entry["use_isolated_directory"]?.GetValue<bool>() == true)
            {
                return VersionSettingsService.IsolatedRuntimeDirectory(gameDir, versionId);
            }
        }

        return globalIsolation ? VersionSettingsService.IsolatedRuntimeDirectory(gameDir, versionId) : gameDir;
    }

    private static List<string> PreviewPrefixFiles(ZipArchive archive, IReadOnlyList<string> prefixes)
    {
        var files = new List<string>();
        foreach (var prefix in prefixes.Select(NormalizePrefix))
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = prefix.Length > 0 ? name[prefix.Length..] : name;
                if (relative.Length > 0)
                {
                    files.Add(relative);
                }
            }
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string PrepareImportedVersion(string gameDir, JsonObject settings, string versionId, string packName, string minecraftVersion, bool globalIsolation)
    {
        var versionDir = Path.Combine(gameDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);
        var versionJsonPath = Path.Combine(versionDir, $"{versionId}.json");
        if (!File.Exists(versionJsonPath))
        {
            var versionJson = new JsonObject { ["id"] = versionId, ["type"] = "release" };
            if (!string.IsNullOrWhiteSpace(minecraftVersion) && !minecraftVersion.Equals(versionId, StringComparison.OrdinalIgnoreCase))
            {
                versionJson["inheritsFrom"] = minecraftVersion;
            }

            File.WriteAllText(versionJsonPath, versionJson.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        var entry = settings[versionId] as JsonObject ?? [];
        entry["alias"] = packName;
        entry["alias_auto"] = true;
        entry["use_isolated_directory"] = true;
        entry["imported_modpack"] = true;
        entry["global_isolation_at_import"] = globalIsolation;
        settings[versionId] = entry;
        return versionDir;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var exact = archive.Entries.FirstOrDefault(entry => entry.FullName.Replace('\\', '/').Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return archive.Entries.FirstOrDefault(entry => candidates.Any(candidate => entry.FullName.Replace('\\', '/').EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase)));
    }

    private static JsonObject ReadJson(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? [];
    }

    private static string EntryDirectory(ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        var index = name.LastIndexOf('/');
        return index < 0 ? "" : name[..(index + 1)];
    }

    private static int ExtractOverrides(ZipArchive archive, string runtimeDir, IReadOnlyList<string> prefixes)
    {
        var copied = 0;
        foreach (var prefix in prefixes.Select(NormalizePrefix))
        {
            copied += ExtractPrefix(archive, prefix, runtimeDir);
        }

        return copied;
    }

    private static int ExtractPrefix(ZipArchive archive, string prefix, string targetDir)
    {
        prefix = NormalizePrefix(prefix);
        var copied = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = prefix.Length > 0 ? name[prefix.Length..] : name;
            if (relative.Length == 0)
            {
                continue;
            }

            var target = SafeJoin(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            copied++;
        }

        return copied;
    }

    private static string NormalizePrefix(string prefix)
    {
        prefix = prefix.Replace('\\', '/').Trim('/');
        return prefix.Length == 0 ? "" : prefix + "/";
    }

    private async Task DownloadFileAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(targetPath);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static string WriteMissingReport(string gameDir, string versionId, JsonArray missing)
    {
        if (missing.Count == 0)
        {
            return "";
        }

        var reportDir = Path.Combine(gameDir, "versions", versionId);
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "missing-modpack-files.json");
        File.WriteAllText(reportPath, missing.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return reportPath;
    }

    private static string SafeJoin(string root, string relativePath)
    {
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootAbs = Path.GetFullPath(root);
        if (!target.StartsWith(rootAbs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && target != rootAbs)
        {
            throw new InvalidOperationException($"整合包包含不安全路径：{relativePath}");
        }

        return target;
    }

    private static string ResolveImportedVersionId(string targetVersionId, string packName, string fallback)
        => string.IsNullOrWhiteSpace(targetVersionId) ? FileUtilities.SanitizeFilename(packName, fallback) : targetVersionId.Trim();

    private static string DetectModrinthLoader(JsonObject dependencies)
        => dependencies.ContainsKey("fabric-loader") ? "fabric-loader"
            : dependencies.ContainsKey("forge") ? "forge"
            : dependencies.ContainsKey("neoforge") ? "neoforge"
            : dependencies.ContainsKey("quilt-loader") ? "quilt-loader"
            : "";

    private static string CurseForgeFileUrl(int fileId)
    {
        var value = fileId.ToString();
        var prefix = value.Length > 3 ? value[..^3] : "0";
        var suffix = value.Length > 3 ? value[^3..] : value.PadLeft(3, '0');
        return $"https://edge.forgecdn.net/files/{prefix}/{suffix}/{fileId}";
    }
}

public sealed record ModpackImportResult(string Version, string Name, string Minecraft, string Loader, string RuntimeDir, int OverridesCopied, int FilesDownloaded, int MissingFiles, string MissingReportPath);

public sealed record ModpackImportPreview(string Version, string Name, string Minecraft, string RuntimeDir, bool VersionExists, int FilesToCopy, IReadOnlyList<string> Conflicts);
