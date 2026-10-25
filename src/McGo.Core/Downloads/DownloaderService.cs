using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using McGo.Core.Configuration;
using McGo.Core.Minecraft;
using McGo.Core.Utilities;

namespace McGo.Core.Downloads;

public sealed class DownloaderService
{
    private readonly HttpClient _httpClient;
    private readonly DownloadOptions? _options;

    public DownloaderService(HttpClient? httpClient = null, DownloadOptions? options = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _options = options;
    }

    /// <summary>
    /// 生效中的下载选项（核心/资源并发上限等）。未显式注入时，按默认配置路径实时读取，
    /// 因此界面修改 DOWNLOAD/max_core_threads、max_asset_threads 后无需重建服务即可生效。
    /// </summary>
    public DownloadOptions Options => _options ?? AppSettings.DefaultDownloadOptions();

    /// <summary>核心文件通道的并发上限。</summary>
    public int MaxCoreConcurrency => NormalizeConcurrency(Options.MaxCoreConcurrency);

    /// <summary>资源文件通道的并发上限。</summary>
    public int MaxAssetConcurrency => NormalizeConcurrency(Options.MaxAssetConcurrency);

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
                "asset")
            {
                Category = DownloadJobCategory.Asset,
            });
        }

        return jobs;
    }

    /// <summary>
    /// 并发下载一组任务。按 <see cref="DownloadJob.Category"/> 分流到两条通道：
    /// 核心文件使用 max_core_threads，资源文件使用 max_asset_threads，两条通道同时推进；
    /// 单条通道内部由 <see cref="SemaphoreSlim"/> 限制并发数。
    /// 任一任务（重试耗尽后）失败都会取消其余任务，并抛出首个失败异常——与并发前"整体失败"的语义一致。
    /// </summary>
    public async Task<DownloadSummary> DownloadJobsAsync(IEnumerable<DownloadJob> jobs, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var list = jobs.Where(job => !string.IsNullOrWhiteSpace(job.Url) && !string.IsNullOrWhiteSpace(job.TargetPath)).ToList();
        if (list.Count == 0)
        {
            return new DownloadSummary(0, 0, 0, 0);
        }

        var coreJobs = new List<DownloadJob>();
        var assetJobs = new List<DownloadJob>();
        foreach (var job in list)
        {
            (job.Category == DownloadJobCategory.Asset ? assetJobs : coreJobs).Add(job);
        }

        using var state = new DownloadRunState(list.Count, progress);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 读取一次配置：两条通道分别使用 max_core_threads / max_asset_threads。
        var options = Options;
        var lanes = new List<Task>(2);
        if (coreJobs.Count > 0)
        {
            lanes.Add(RunLaneAsync(coreJobs, NormalizeConcurrency(options.MaxCoreConcurrency), state, linked));
        }

        if (assetJobs.Count > 0)
        {
            lanes.Add(RunLaneAsync(assetJobs, NormalizeConcurrency(options.MaxAssetConcurrency), state, linked));
        }

        // lane 任务永不抛出：失败被记录在 state.Failure 中，取消则是静默收尾。
        await Task.WhenAll(lanes).ConfigureAwait(false);
        // 先补发终态进度，再决定是取消还是失败：两者都不应该吞掉最后一次进度上报。
        state.ReportFinal();
        cancellationToken.ThrowIfCancellationRequested();
        state.Failure?.Throw();
        return state.BuildSummary();
    }

    // 单条通道：SemaphoreSlim 限制并发，任务之间互不阻塞；一个任务失败即取消同批其余任务。
    private async Task RunLaneAsync(List<DownloadJob> jobs, int concurrency, DownloadRunState state, CancellationTokenSource linked)
    {
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var workers = new List<Task>(jobs.Count);
        try
        {
            foreach (var job in jobs)
            {
                await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                workers.Add(RunJobAsync(job, gate, state, linked));
            }
        }
        catch (OperationCanceledException)
        {
            // 取消（外部取消或其它任务失败后的快速退出）时不再派发新任务。
        }
        catch (Exception exception)
        {
            state.TryFail(exception);
            linked.Cancel();
        }

        await DrainWorkersAsync(workers).ConfigureAwait(false);
    }

    private async Task RunJobAsync(DownloadJob job, SemaphoreSlim gate, DownloadRunState state, CancellationTokenSource linked)
    {
        // 同一目标文件可能被多个任务引用（版本 json 里重复的库条目）。并发下必须串行化，
        // 否则共用同一个 .download 临时文件会互相破坏；串行化后后到者直接复用，与并发前一致。
        var pathGate = state.GateFor(job.TargetPath);
        var pathGateHeld = false;
        try
        {
            await pathGate.WaitAsync(linked.Token).ConfigureAwait(false);
            pathGateHeld = true;
            var jobCancellation = linked.Token;
            jobCancellation.ThrowIfCancellationRequested();
            if (MatchesFile(job.TargetPath, job.Size, job.Sha1))
            {
                state.Complete(job, reused: true, bytes: 0);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(job.TargetPath) ?? ".");
            var jobBytes = await DownloadSingleJobAsync(job, jobCancellation).ConfigureAwait(false);
            state.Complete(job, reused: false, bytes: jobBytes);
        }
        catch (OperationCanceledException)
        {
            // 取消不算任务失败，也不改变 summary 计数。
        }
        catch (Exception exception)
        {
            // 保留"任一任务失败即整体失败"的语义：记录首个失败并让其余任务尽快退出。
            state.TryFail(exception);
            linked.Cancel();
        }
        finally
        {
            if (pathGateHeld)
            {
                pathGate.Release();
            }

            gate.Release();
        }
    }

    // 等待已派发的任务收尾；异常已在 RunJobAsync 中记录，这里只负责不让它逃逸。
    private static async Task DrainWorkersAsync(List<Task> workers)
    {
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            // 收尾阶段忽略所有异常：真正的首个失败由 DownloadJobsAsync 重新抛出。
        }
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
                // 阶段一：先取资源索引（单个文件）才能知道资源列表。此阶段不回调节点进度，
                // 等资源任务数确定后再按统一总数重放，避免进度条出现「100% → 3%」的倒退。
                var indexSummary = await DownloadJobsAsync([assetIndexJob], null, cancellationToken);
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

                var rebased = RebasedProgress.For(progress, indexSummary);
                var restSummary = await DownloadJobsAsync(jobs, rebased, cancellationToken);
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

    private static int NormalizeConcurrency(int value) => Math.Clamp(value, 1, AppSettings.MaxConcurrencyLimit);

    /// <summary>
    /// 把"前置批次（资源索引）"的进度折算进后续批次的快照里：
    /// 总任务数与已下载字节数都加上前置批次，保证 UI 看到的进度单调前进、总数固定、不会倒退。
    /// </summary>
    private sealed class RebasedProgress(IProgress<DownloadProgress> inner, DownloadSummary baseline) : IProgress<DownloadProgress>
    {
        private readonly object _gate = new();
        private int _lastCompleted = baseline.CompletedJobs;
        private long _lastBytes = baseline.DownloadedBytes;

        public static IProgress<DownloadProgress>? For(IProgress<DownloadProgress>? inner, DownloadSummary baseline)
            => inner is null ? null : new RebasedProgress(inner, baseline);

        public void Report(DownloadProgress value)
        {
            // 生成与投递同锁完成，避免快照乱序投递造成进度回退。
            lock (_gate)
            {
                // 前置批次已完成的进度不能被子批次的较小值覆盖（防御性：正常路径下不会发生）。
                var completed = baseline.CompletedJobs + value.CompletedJobs;
                var bytes = baseline.DownloadedBytes + value.DownloadedBytes;
                _lastCompleted = Math.Max(_lastCompleted, completed);
                _lastBytes = Math.Max(_lastBytes, bytes);
                var total = baseline.TotalJobs + value.TotalJobs;
                var rebased = new DownloadProgress(
                    _lastCompleted,
                    total,
                    baseline.ReusedJobs + value.ReusedJobs,
                    _lastBytes,
                    value.CurrentFile,
                    value.Phase,
                    total <= 0 ? 1 : (double)_lastCompleted / total);
                try
                {
                    inner.Report(rebased);
                }
                catch
                {
                    // 进度回调（UI）异常不能中断下载。
                }
            }
        }
    }

    /// <summary>
    /// 并发下载期间共享的进度聚合器。所有计数器都用 <see cref="Interlocked"/> 更新，
    /// 保证多线程完成任务时：完成数/复用数/字节数都不重复计数、不丢失，进度单调不减。
    /// </summary>
    private sealed class DownloadRunState(int totalJobs, IProgress<DownloadProgress>? progress) : IDisposable
    {
        private readonly IProgress<DownloadProgress>? _progress = progress;
        private readonly int _totalJobs = totalJobs;
        // 串行化上报：避免多个任务并发回调 IProgress 造成乱序（进度回退）。
        private readonly object _progressGate = new();
        private int _completed;
        private int _reused;
        private long _downloadedBytes;
        private int _reportedCompleted;
        private long _reportedBytes;
        private readonly Dictionary<string, SemaphoreSlim> _pathGates = new(StringComparer.OrdinalIgnoreCase);
        private ExceptionDispatchInfo? _failure;
        private bool _disposed;

        /// <summary>首个（按发生顺序）失败的任务异常，供外层在收尾后重新抛出。</summary>
        public ExceptionDispatchInfo? Failure => Volatile.Read(ref _failure);

        // Interlocked.CompareExchange 保证并发失败时只保留第一个异常。
        public void TryFail(Exception exception) => Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(exception), null);

        /// <summary>同一目标路径共享一把锁，保证重复条目不会并发写同一个临时文件。</summary>
        public SemaphoreSlim GateFor(string targetPath)
        {
            lock (_pathGates)
            {
                if (!_pathGates.TryGetValue(targetPath, out var gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _pathGates[targetPath] = gate;
                }

                return gate;
            }
        }

        public void Complete(DownloadJob job, bool reused, long bytes)
        {
            // 先累加字节再累加完成数：外部观察者看到 CompletedJobs 增长时，字节数一定已经包含该任务。
            if (bytes > 0)
            {
                Interlocked.Add(ref _downloadedBytes, bytes);
            }

            if (reused)
            {
                Interlocked.Increment(ref _reused);
            }

            Report(Interlocked.Increment(ref _completed), job);
        }

        /// <summary>结束时补发一次终态进度（若最后一个任务的进度已上报则不重复）。</summary>
        public void ReportFinal() => Report(Volatile.Read(ref _completed), null);

        public DownloadSummary BuildSummary()
            => new(_totalJobs, Volatile.Read(ref _completed), Volatile.Read(ref _reused), Interlocked.Read(ref _downloadedBytes));

        private void Report(int completed, DownloadJob? job)
        {
            if (_progress is null)
            {
                return;
            }

            // 生成快照与投递快照必须在同一把锁内完成：否则两个任务可能"先生成小的、后生成大的"，
            // 却按相反顺序投递给 UI，导致进度倒退。回调内部只做 UI 转发，不会回调进下载器。
            lock (_progressGate)
            {
                if (_disposed)
                {
                    return;
                }

                // 两个任务的"累加计数"和"抢到锁的顺序"可能不一致（A 先加完成数、B 先上报），
                // 因此上报值一律对已上报值取最大值，保证完成数与字节数都只增不减。
                var bytes = Math.Max(Interlocked.Read(ref _downloadedBytes), _reportedBytes);
                var effectiveCompleted = Math.Max(completed, _reportedCompleted);
                if (effectiveCompleted == _reportedCompleted && bytes == _reportedBytes)
                {
                    return;
                }

                _reportedCompleted = effectiveCompleted;
                _reportedBytes = bytes;
                var snapshot = new DownloadProgress(
                    effectiveCompleted,
                    _totalJobs,
                    Volatile.Read(ref _reused),
                    bytes,
                    job?.TargetPath ?? "",
                    job?.Kind ?? "",
                    _totalJobs == 0 ? 1 : (double)effectiveCompleted / _totalJobs);
                try
                {
                    _progress.Report(snapshot);
                }
                catch
                {
                    // 进度回调（UI）异常不能中断下载。
                }
            }
        }

        public void Dispose()
        {
            lock (_progressGate)
            {
                _disposed = true;
            }

            lock (_pathGates)
            {
                foreach (var gate in _pathGates.Values)
                {
                    gate.Dispose();
                }

                _pathGates.Clear();
            }
        }
    }
}

public enum DownloadJobCategory
{
    /// <summary>核心文件：客户端 jar、libraries、natives、版本/资源索引等，使用 max_core_threads。</summary>
    Core = 0,

    /// <summary>资源文件：assets 索引中 objects 下的文件，使用 max_asset_threads。</summary>
    Asset = 1,
}

public sealed record DownloadJob(string Url, string TargetPath, string Sha1, long Size, string Kind)
{
    /// <summary>
    /// 并发通道分类。默认按 <see cref="Kind"/> 推断（"asset" 归资源通道，其余归核心通道），
    /// 需要时可在对象初始化器中显式覆盖。
    /// </summary>
    public DownloadJobCategory Category { get; init; } = ClassifyKind(Kind);

    public static DownloadJobCategory ClassifyKind(string? kind)
        => string.Equals(kind, "asset", StringComparison.OrdinalIgnoreCase)
            ? DownloadJobCategory.Asset
            : DownloadJobCategory.Core;
}

public sealed record MissingGameFile(string Path, string Kind);
public sealed record DownloadProgress(int CompletedJobs, int TotalJobs, int ReusedJobs, long DownloadedBytes, string CurrentFile, string Phase, double Progress);
public sealed record DownloadSummary(int TotalJobs, int CompletedJobs, int ReusedJobs, long DownloadedBytes);
public sealed record RepairSummary(int TotalJobs, int CompletedJobs, int ReusedJobs, long DownloadedBytes, int NativeFilesExtracted, string NativesDirectory);
