using System.IO.Compression;
using System.Text.Json.Nodes;
using McGo.Core.Minecraft;
using McGo.Core.Utilities;

namespace McGo.Core.Capsules;

public sealed class CapsuleService
{
    public const string CapsuleManifest = "mcgo-capsule.json";
    public const int CapsuleSchemaVersion = 1;

    private static readonly HashSet<string> AllowedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "mods", "config", "resourcepacks", "shaderpacks", "datapacks",
    };

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "__pycache__", "cache", "caches", "logs", "crash-reports", "screenshots", "saves",
    };

    private static readonly string[] SkipSuffixes = [".log", ".tmp", ".lock", ".lck", ".bak"];

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts.json", "launcher_accounts.json", "launcher_profiles.json", "options.txt", "servers.dat", "usercache.json",
    };

    private readonly LauncherService _launcher;
    private readonly VersionSettingsService _versionSettings;

    public CapsuleService(LauncherService? launcher = null, VersionSettingsService? versionSettings = null)
    {
        _launcher = launcher ?? new LauncherService();
        _versionSettings = versionSettings ?? new VersionSettingsService(_launcher);
    }

    public static bool ShouldIncludeCapsuleFile(string relativePath)
    {
        var normalized = NormalizeRelative(relativePath);
        if (normalized.Length == 0)
        {
            return false;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !AllowedRoots.Contains(parts[0]))
        {
            return false;
        }

        if (parts.Any(part => part is "." or ".." || SkipDirs.Contains(part)))
        {
            return false;
        }

        var loweredName = parts[^1].ToLowerInvariant();
        return !SensitiveNames.Contains(loweredName) && !SkipSuffixes.Any(suffix => loweredName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<CapsuleFile> IterCapsuleFiles(string runtimeDir)
    {
        foreach (var rootName in AllowedRoots.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var rootDir = Path.Combine(runtimeDir, rootName);
            if (!Directory.Exists(rootDir))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                var relative = NormalizeRelative(Path.GetRelativePath(runtimeDir, path));
                if (ShouldIncludeCapsuleFile(relative))
                {
                    yield return new CapsuleFile(path, relative);
                }
            }
        }
    }

    public CapsuleExportResult ExportCapsule(string gameDir, JsonObject settings, string versionId, string targetPath, JsonObject? p2pInfo = null, bool globalIsolation = false)
    {
        var runtimeDir = VersionSettingsService.RuntimeDirectoryForVersion(gameDir, settings, versionId, globalIsolation);
        if (!Directory.Exists(runtimeDir))
        {
            throw new InvalidOperationException($"运行目录不存在：{runtimeDir}");
        }

        var files = new JsonArray();
        var entry = settings[versionId] as JsonObject;
        var capsuleName = entry?["alias"]?.GetValue<string>()?.Trim() is { Length: > 0 } alias ? alias : versionId;
        var manifest = new JsonObject
        {
            ["schema"] = "mcgo.capsule",
            ["schemaVersion"] = CapsuleSchemaVersion,
            ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["sourceVersion"] = versionId,
            ["name"] = capsuleName,
            ["minecraft"] = _versionSettings.ResolveBaseMinecraftVersion(gameDir, versionId),
            ["loader"] = DetectExportLoader(gameDir, versionId).Loader,
            ["loaderVersion"] = DetectExportLoader(gameDir, versionId).Version,
            ["p2p"] = p2pInfo is null ? new JsonObject() : JsonNode.Parse(p2pInfo.ToJsonString()),
            ["files"] = files,
        };

        var versionJson = _launcher.GetVersionJson(gameDir, versionId);
        if (versionJson is not null)
        {
            manifest["versionJson"] = JsonNode.Parse(versionJson.ToJsonString());
        }

        long totalSize = 0;
        using var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create);
        foreach (var file in IterCapsuleFiles(runtimeDir))
        {
            var size = new FileInfo(file.Path).Length;
            totalSize += size;
            files.Add(new JsonObject
            {
                ["path"] = file.RelativePath,
                ["size"] = size,
                ["sha256"] = FileUtilities.Sha256File(file.Path),
            });
            archive.CreateEntryFromFile(file.Path, file.RelativePath.Replace('\\', '/'), CompressionLevel.Optimal);
        }

        var manifestEntry = archive.CreateEntry(CapsuleManifest, CompressionLevel.Optimal);
        using (var writer = new StreamWriter(manifestEntry.Open()))
        {
            writer.Write(manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        return new CapsuleExportResult(capsuleName, versionId, manifest["minecraft"]?.GetValue<string>() ?? "", manifest["loader"]?.GetValue<string>() ?? "", files.Count, totalSize, targetPath);
    }

    public static JsonObject ReadCapsuleManifest(string capsulePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(capsulePath);
            var entry = archive.GetEntry(CapsuleManifest) ?? throw new InvalidOperationException("胶囊缺少 mcgo-capsule.json。");
            using var reader = new StreamReader(entry.Open());
            var manifest = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new InvalidOperationException("胶囊格式不受支持。");
            if (manifest["schema"]?.GetValue<string>() != "mcgo.capsule")
            {
                throw new InvalidOperationException("胶囊格式不受支持。");
            }

            return manifest;
        }
        catch (InvalidDataException exc)
        {
            throw new InvalidOperationException("不是有效的 McGo 开黑胶囊文件。", exc);
        }
    }

    public CapsuleImportResult ImportCapsule(string capsulePath, string gameDir, JsonObject settings, string targetVersionId = "", bool globalIsolation = true)
    {
        var manifest = ReadCapsuleManifest(capsulePath);
        var sourceVersion = manifest["sourceVersion"]?.GetValue<string>() ?? "capsule";
        var capsuleName = manifest["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } name ? name : sourceVersion;
        var versionId = string.IsNullOrWhiteSpace(targetVersionId) ? FileUtilities.SanitizeFilename(capsuleName, "McGoCapsule") : targetVersionId.Trim();
        var runtimeDir = Path.Combine(gameDir, "versions", versionId);
        Directory.CreateDirectory(runtimeDir);

        if (manifest["versionJson"] is JsonObject versionJson)
        {
            var versionDir = Path.Combine(gameDir, "versions", versionId);
            Directory.CreateDirectory(versionDir);
            var copiedJson = JsonNode.Parse(versionJson.ToJsonString())!.AsObject();
            copiedJson["id"] = versionId;
            File.WriteAllText(Path.Combine(versionDir, $"{versionId}.json"), copiedJson.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        var entry = settings[versionId] as JsonObject ?? [];
        entry["alias"] ??= capsuleName;
        entry["alias_auto"] ??= true;
        entry["use_isolated_directory"] = true;
        entry["source_capsule"] ??= Path.GetFullPath(capsulePath);
        settings[versionId] = entry;

        var files = manifest["files"] as JsonArray ?? [];
        var missingFiles = CapsuleMissingFiles(runtimeDir, files);
        return new CapsuleImportResult(versionId, capsuleName, manifest["minecraft"]?.GetValue<string>() ?? "", manifest["loader"]?.GetValue<string>() ?? "", manifest["p2p"] as JsonObject ?? [], files, missingFiles, runtimeDir);
    }

    public static List<JsonObject> CapsuleMissingFiles(string runtimeDir, JsonArray files)
    {
        var missing = new List<JsonObject>();
        foreach (var item in files.OfType<JsonObject>())
        {
            var relative = NormalizeRelative(item["path"]?.GetValue<string>() ?? "");
            if (!ShouldIncludeCapsuleFile(relative))
            {
                continue;
            }

            var target = SafeJoin(runtimeDir, relative);
            var expectedSha256 = item["sha256"]?.GetValue<string>() ?? "";
            if (!File.Exists(target) || expectedSha256.Length > 0 && FileUtilities.Sha256File(target) != expectedSha256)
            {
                missing.Add(JsonNode.Parse(item.ToJsonString())!.AsObject());
            }
        }

        return missing;
    }

    public static string CapsuleTargetPath(string runtimeDir, string relativePath)
    {
        var relative = NormalizeRelative(relativePath);
        if (!ShouldIncludeCapsuleFile(relative))
        {
            throw new InvalidOperationException($"胶囊文件路径不允许：{relativePath}");
        }

        return SafeJoin(runtimeDir, relative);
    }

    private (string Loader, string Version) DetectExportLoader(string gameDir, string versionId)
    {
        var type = _versionSettings.DetectVersionType(gameDir, versionId);
        return type == "原版" ? ("vanilla", "") : (type.ToLowerInvariant(), "");
    }

    private static string NormalizeRelative(string relativePath)
        => relativePath.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Aggregate("", (current, part) => current.Length == 0 ? part : current + "/" + part);

    private static string SafeJoin(string root, string relativePath)
    {
        var target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootAbs = Path.GetFullPath(root);
        if (!target.StartsWith(rootAbs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && target != rootAbs)
        {
            throw new InvalidOperationException($"胶囊包含不安全路径：{relativePath}");
        }

        return target;
    }
}

public sealed record CapsuleFile(string Path, string RelativePath);
public sealed record CapsuleExportResult(string Name, string Version, string Minecraft, string Loader, int FileCount, long TotalSize, string TargetPath);
public sealed record CapsuleImportResult(string Version, string Name, string Minecraft, string Loader, JsonObject P2P, JsonArray Files, IReadOnlyList<JsonObject> MissingFiles, string RuntimeDir);
