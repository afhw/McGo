using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using McGo.Core.Minecraft;
using McGo.Core.Utilities;

namespace McGo.Core.Downloads;

public sealed class DownloaderService
{
    private readonly HttpClient _httpClient;

    public DownloaderService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public static string RewriteUrl(string url, string mirrorSource)
    {
        if (string.Equals(mirrorSource, "bmclapi", StringComparison.OrdinalIgnoreCase))
        {
            return url
                .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
                .Replace("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
                .Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
                .Replace("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
                .Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
                .Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets", StringComparison.OrdinalIgnoreCase);
        }

        return url;
    }

    public static string? MavenLibraryPath(string name) => LauncherService.MavenLibraryPath(name);

    public static bool MatchesFile(string path, long expectedSize = 0, string expectedSha1 = "")
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (expectedSize > 0 && new FileInfo(path).Length != expectedSize)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(expectedSha1) || FileUtilities.Sha1File(path).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    public static List<DownloadJob> BuildCoreJobs(JsonObject versionJson, string gameDirectory, string versionId, string mirrorSource)
    {
        var jobs = new List<DownloadJob>();
        var client = versionJson["downloads"]?["client"] as JsonObject;
        if (client is not null)
        {
            jobs.Add(new DownloadJob(
                client["url"]?.GetValue<string>() ?? "",
                Path.Combine(gameDirectory, "versions", versionId, $"{versionId}.jar"),
                client["sha1"]?.GetValue<string>() ?? "",
                JsonLong(client["size"]),
                "client"));
        }

        foreach (var library in versionJson["libraries"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var artifact = library["downloads"]?["artifact"] as JsonObject;
            var path = artifact?["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path) && !IsNativeOnlyLibrary(library) && library["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name))
            {
                path = MavenLibraryPath(name);
            }

            var url = artifact?["url"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(path))
            {
                url = "https://libraries.minecraft.net/" + path.Replace('\\', '/');
            }

            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(url))
            {
                jobs.Add(new DownloadJob(
                    RewriteUrl(url, mirrorSource),
                    Path.Combine(gameDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar)),
                    artifact?["sha1"]?.GetValue<string>() ?? "",
                    JsonLong(artifact?["size"]),
                    "library"));
            }

            var native = NativeClassifierDownload(library);
            var nativePath = native?["path"]?.GetValue<string>();
            var nativeUrl = native?["url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(nativePath) && !string.IsNullOrWhiteSpace(nativeUrl))
            {
                jobs.Add(new DownloadJob(
                    RewriteUrl(nativeUrl, mirrorSource),
                    Path.Combine(gameDirectory, "libraries", nativePath.Replace('/', Path.DirectorySeparatorChar)),
                    native?["sha1"]?.GetValue<string>() ?? "",
                    JsonLong(native?["size"]),
                    "native"));
            }
        }

        return jobs;
    }

    public static List<MissingGameFile> CollectMissingGameFiles(JsonObject versionJson, string gameDirectory, string versionId, bool includeAssets = true)
    {
        var jobs = BuildCoreJobs(versionJson, gameDirectory, versionId, "official");
        var assetIndexJob = BuildAssetIndexJob(versionJson, gameDirectory, "official");
        if (assetIndexJob is not null)
        {
            jobs.Add(assetIndexJob);
            if (includeAssets && File.Exists(assetIndexJob.TargetPath))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(assetIndexJob.TargetPath)) is JsonObject assetIndexJson)
                    {
                        jobs.AddRange(BuildAssetJobs(assetIndexJson, gameDirectory, "official"));
                    }
                }
                catch
                {
                }
            }
        }

        return jobs
            .Where(job => !MatchesFile(job.TargetPath, job.Size, job.Sha1))
            .Select(job => new MissingGameFile(job.TargetPath, job.Kind))
            .ToList();
    }

    public static DownloadJob? BuildAssetIndexJob(JsonObject versionJson, string gameDirectory, string mirrorSource)
    {
        var assetIndex = versionJson["assetIndex"] as JsonObject;
        var id = assetIndex?["id"]?.GetValue<string>() ?? "";
        var url = assetIndex?["url"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return new DownloadJob(
            RewriteUrl(url, mirrorSource),
            Path.Combine(gameDirectory, "assets", "indexes", $"{id}.json"),
            assetIndex?["sha1"]?.GetValue<string>() ?? "",
            JsonLong(assetIndex?["size"]),
            "asset-index");
    }

    public static List<DownloadJob> BuildAssetJobs(JsonObject assetIndexJson, string gameDirectory, string mirrorSource)
    {
        var jobs = new List<DownloadJob>();
        var objects = assetIndexJson["objects"] as JsonObject;
        if (objects is null)
        {
            return jobs;
        }

        foreach (var pair in objects)
        {
            if (pair.Value is not JsonObject item)
            {
                continue;
            }

            var hash = item["hash"]?.GetValue<string>() ?? "";
            if (hash.Length < 2)
            {
                continue;
            }

            var prefix = hash[..2];
            var url = RewriteUrl($"https://resources.download.minecraft.net/{prefix}/{hash}", mirrorSource);
            jobs.Add(new DownloadJob(
                url,
                Path.Combine(gameDirectory, "assets", "objects", prefix, hash),
                hash,
                JsonLong(item["size"]),
                "asset"));
        }

        return jobs;
    }

    public async Task<DownloadSummary> DownloadJobsAsync(IEnumerable<DownloadJob> jobs, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var list = jobs.Where(job => !string.IsNullOrWhiteSpace(job.Url) && !string.IsNullOrWhiteSpace(job.TargetPath)).ToList();
        var completed = 0;
        var reused = 0;
        long downloadedBytes = 0;
        foreach (var job in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MatchesFile(job.TargetPath, job.Size, job.Sha1))
            {
                reused++;
                completed++;
                progress?.Report(new DownloadProgress(completed, list.Count, reused, downloadedBytes, job.TargetPath, job.Kind, 1));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(job.TargetPath) ?? ".");
            var jobBytes = await DownloadSingleJobAsync(job, cancellationToken);
            downloadedBytes += jobBytes;
            completed++;
            progress?.Report(new DownloadProgress(completed, list.Count, reused, downloadedBytes, job.TargetPath, job.Kind, list.Count == 0 ? 1 : (double)completed / list.Count));
        }

        return new DownloadSummary(list.Count, completed, reused, downloadedBytes);
    }

    // 单个下载任务：失败时按有限次数重试，返回本任务成功写入的字节数。
    // 取消不重试；瞬时网络/IO 错误与校验失败均可重试，最后一次仍失败则抛出。
    private async Task<long> DownloadSingleJobAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        var tempPath = job.TargetPath + ".download";
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jobBytes = 0L;
            try
            {
                using var response = await _httpClient.GetAsync(job.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = File.Create(tempPath))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        jobBytes += read;
                    }
                }

                if (!MatchesFile(tempPath, job.Size, job.Sha1))
                {
                    throw new InvalidOperationException($"下载文件校验失败：{Path.GetFileName(job.TargetPath)}");
                }

                File.Move(tempPath, job.TargetPath, overwrite: true);
                return jobBytes;
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch when (attempt < maxAttempts)
            {
                TryDelete(tempPath);
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), cancellationToken);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
    }

    public async Task<DownloadSummary> DownloadGameFilesAsync(JsonObject versionJson, string gameDirectory, string versionId, string mirrorSource, bool includeAssets = true, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var versionDir = Path.Combine(gameDirectory, "versions", versionId);
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(Path.Combine(versionDir, $"{versionId}.json"), versionJson.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellationToken);

        var jobs = BuildCoreJobs(versionJson, gameDirectory, versionId, mirrorSource);
        if (includeAssets)
        {
            var assetIndexJob = BuildAssetIndexJob(versionJson, gameDirectory, mirrorSource);
            if (assetIndexJob is not null)
            {
                var indexSummary = await DownloadJobsAsync([assetIndexJob], progress, cancellationToken);
                JsonObject? assetIndexJson = null;
                try
                {
                    assetIndexJson = JsonNode.Parse(await File.ReadAllTextAsync(assetIndexJob.TargetPath, cancellationToken)) as JsonObject;
                }
                catch
                {
                    assetIndexJson = null;
                }

                if (assetIndexJson is not null)
                {
                    jobs.AddRange(BuildAssetJobs(assetIndexJson, gameDirectory, mirrorSource));
                }

                var restSummary = await DownloadJobsAsync(jobs, progress, cancellationToken);
                return new DownloadSummary(
                    indexSummary.TotalJobs + restSummary.TotalJobs,
                    indexSummary.CompletedJobs + restSummary.CompletedJobs,
                    indexSummary.ReusedJobs + restSummary.ReusedJobs,
                    indexSummary.DownloadedBytes + restSummary.DownloadedBytes);
            }
        }

        return await DownloadJobsAsync(jobs, progress, cancellationToken);
    }

    public async Task<RepairSummary> RepairGameFilesAsync(JsonObject versionJson, string gameDirectory, string versionId, string mirrorSource, bool includeAssets = true, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(gameDirectory);
        var resolvedVersionId = versionJson["id"]?.GetValue<string>() ?? versionId;
        var versionDir = Path.Combine(gameDirectory, "versions", resolvedVersionId);
        Directory.CreateDirectory(versionDir);
        var versionJsonPath = Path.Combine(versionDir, $"{resolvedVersionId}.json");
        if (!File.Exists(versionJsonPath))
        {
            await File.WriteAllTextAsync(versionJsonPath, versionJson.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        }

        DownloadSummary combined = new(0, 0, 0, 0);
        var jobs = BuildCoreJobs(versionJson, gameDirectory, resolvedVersionId, mirrorSource);
        if (includeAssets)
        {
            var assetIndexJob = BuildAssetIndexJob(versionJson, gameDirectory, mirrorSource);
            if (assetIndexJob is not null)
            {
                combined = Combine(combined, await DownloadJobsAsync([assetIndexJob], progress, cancellationToken));
                if (JsonNode.Parse(await File.ReadAllTextAsync(assetIndexJob.TargetPath, cancellationToken)) is JsonObject assetIndexJson)
                {
                    jobs.AddRange(BuildAssetJobs(assetIndexJson, gameDirectory, mirrorSource));
                }
            }
        }

        combined = Combine(combined, await DownloadJobsAsync(jobs, progress, cancellationToken));
        var nativeFiles = ExtractNatives(versionJson, gameDirectory, resolvedVersionId);
        return new RepairSummary(combined.TotalJobs, combined.CompletedJobs, combined.ReusedJobs, combined.DownloadedBytes, nativeFiles, Path.Combine(gameDirectory, "versions", resolvedVersionId, $"{resolvedVersionId}-natives"));
    }

    public static int ExtractNatives(JsonObject versionJson, string gameDirectory, string versionId)
    {
        var nativesDirectory = Path.Combine(gameDirectory, "versions", versionId, $"{versionId}-natives");
        Directory.CreateDirectory(nativesDirectory);
        var nativesRoot = Path.GetFullPath(nativesDirectory);
        var nativesRootWithSeparator = nativesRoot.EndsWith(Path.DirectorySeparatorChar)
            ? nativesRoot
            : nativesRoot + Path.DirectorySeparatorChar;
        var extracted = 0;

        foreach (var library in versionJson["libraries"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var native = NativeClassifierDownload(library);
            var nativePath = native?["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(nativePath))
            {
                continue;
            }

            var libraryPath = Path.Combine(gameDirectory, "libraries", nativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(libraryPath))
            {
                continue;
            }

            using var archive = ZipFile.OpenRead(libraryPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("META-INF\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetPath = Path.GetFullPath(Path.Combine(nativesDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!targetPath.StartsWith(nativesRootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? nativesDirectory);
                entry.ExtractToFile(targetPath, overwrite: true);
                extracted++;
            }
        }

        return extracted;
    }

    private static DownloadSummary Combine(DownloadSummary left, DownloadSummary right)
        => new(
            left.TotalJobs + right.TotalJobs,
            left.CompletedJobs + right.CompletedJobs,
            left.ReusedJobs + right.ReusedJobs,
            left.DownloadedBytes + right.DownloadedBytes);

    private static JsonObject? NativeClassifierDownload(JsonObject library)
    {
        var key = NativeClassifierKey(library);
        return string.IsNullOrWhiteSpace(key) ? null : library["downloads"]?["classifiers"]?[key] as JsonObject;
    }

    private static bool IsNativeOnlyLibrary(JsonObject library)
        => library["natives"] is not null &&
           library["downloads"]?["classifiers"] is JsonObject &&
           library["downloads"]?["artifact"] is null;

    private static string NativeClassifierKey(JsonObject library)
    {
        if (library["downloads"]?["classifiers"] is not JsonObject classifiers)
        {
            return "";
        }

        var osName = CurrentNativeOsName();
        var arch = CurrentNativeArchSuffix();
        var template = library["natives"]?[osName]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(template))
        {
            var keyed = template.Replace("${arch}", arch, StringComparison.Ordinal);
            if (classifiers.ContainsKey(keyed))
            {
                return keyed;
            }

            var fallback = template.Replace("${arch}", "", StringComparison.Ordinal);
            if (classifiers.ContainsKey(fallback))
            {
                return fallback;
            }
        }

        foreach (var key in new[] { $"natives-{osName}-{arch}", $"natives-{osName}" })
        {
            if (classifiers.ContainsKey(key))
            {
                return key;
            }
        }

        return "";
    }

    private static string CurrentNativeOsName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "osx";
        }

        return OperatingSystem.IsLinux() ? "linux" : "windows";
    }

    private static string CurrentNativeArchSuffix()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "32",
            Architecture.X64 => "64",
            Architecture.Arm => "arm32",
            Architecture.Arm64 => "arm64",
            _ => Environment.Is64BitProcess ? "64" : "32",
        };
    }

    private static long JsonLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        return value.TryGetValue<double>(out var doubleValue) ? (long)doubleValue : 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

public sealed record DownloadJob(string Url, string TargetPath, string Sha1, long Size, string Kind);
public sealed record MissingGameFile(string Path, string Kind);
public sealed record DownloadProgress(int CompletedJobs, int TotalJobs, int ReusedJobs, long DownloadedBytes, string CurrentFile, string Phase, double Progress);
public sealed record DownloadSummary(int TotalJobs, int CompletedJobs, int ReusedJobs, long DownloadedBytes);
public sealed record RepairSummary(int TotalJobs, int CompletedJobs, int ReusedJobs, long DownloadedBytes, int NativeFilesExtracted, string NativesDirectory);
