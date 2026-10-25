using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace McGo.Core.Java;

public static class JavaRuntimeService
{
    public static string CurrentPlatformName()
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";

    public static string CurrentArchName()
        => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x32",
            Architecture.Arm64 => "aarch64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

    public static string JavaRuntimeDownloadUrl(int majorVersion)
        => $"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/{CurrentPlatformName()}/{CurrentArchName()}/jre/hotspot/normal/eclipse";

    public static string? FindJavaInDirectory(string rootDir)
    {
        if (!Directory.Exists(rootDir))
        {
            return null;
        }

        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return Directory.EnumerateFiles(rootDir, executable, SearchOption.AllDirectories)
            .OrderBy(path => Path.GetFileName(Path.GetDirectoryName(path) ?? "").Equals("bin", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// 查找 Minecraft 官方启动器自带的 Java 运行时。
    /// 官方启动器把 JRE 解压到 <c>.minecraft\runtime\&lt;jre&gt;\&lt;平台&gt;\&lt;jre&gt;\bin\java.exe</c>，
    /// 商店版启动器则放在包容器的 LocalCache 下；这两处都不在 PATH 里，必须单独枚举。
    /// </summary>
    /// <param name="roamingAppDataRoot">用于测试注入；为空时取 <see cref="Environment.SpecialFolder.ApplicationData"/>。</param>
    /// <param name="localAppDataRoot">用于测试注入；为空时取 <see cref="Environment.SpecialFolder.LocalApplicationData"/>。</param>
    public static IReadOnlyList<string> FindBundledRuntimeCandidates(
        string? roamingAppDataRoot = null,
        string? localAppDataRoot = null)
    {
        var appData = string.IsNullOrWhiteSpace(roamingAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : roamingAppDataRoot;
        var localAppData = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataRoot;

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var minecraft = Path.Combine(appData, ".minecraft");
            roots.Add(Path.Combine(minecraft, "runtime"));
            // 部分版本把下载过的运行时缓存留在 cache 下，存在时才值得扫。
            roots.Add(Path.Combine(minecraft, "cache"));
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            // Microsoft Store 版启动器被包容器重定向，.minecraft 落在 LocalCache\Roaming 下。
            roots.Add(Path.Combine(
                localAppData,
                "Packages",
                "Microsoft.4297127D64EC6_8wekyb3d8bbwe",
                "LocalCache",
                "Roaming",
                ".minecraft",
                "runtime"));
        }

        var candidates = new List<string>();
        foreach (var root in roots)
        {
            candidates.AddRange(FindJavasUnderRuntimeRoot(root));
        }

        return candidates
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
    }

    /// <summary>
    /// 逐个子目录找 java：官方启动器的布局有
    /// <c>runtime\&lt;jre&gt;\windows-x64\&lt;jre&gt;\bin\java.exe</c>（当前）与
    /// <c>runtime\&lt;jre&gt;\windows\&lt;jre&gt;\bin\java.exe</c>（旧版）两种，
    /// 这里不硬编码平台目录名，按 jre 目录整体搜索，新增平台布局也能命中。
    /// </summary>
    private static IEnumerable<string> FindJavasUnderRuntimeRoot(string runtimeRoot)
    {
        if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
        {
            yield break;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(runtimeRoot);
        }
        catch
        {
            yield break;
        }

        foreach (var child in children)
        {
            var javaPath = FindJavaInDirectory(child);
            if (!string.IsNullOrWhiteSpace(javaPath))
            {
                yield return javaPath;
            }
        }
    }

    public static void ExtractJavaArchive(string archivePath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        if (LooksLikeZip(archivePath))
        {
            ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
            return;
        }

        // 优先按内容嗅探 gzip 魔数（1F 8B）。部分下载源（如 Adoptium 的 /eclipse 端点）
        // URL 与文件名不含扩展名，若响应缺少 content-disposition，仅靠扩展名判断会在
        // 非 Windows 平台把 tar.gz 兜底误判为 zip，进而抛"格式不受支持"。
        if (LooksLikeGzip(archivePath) ||
            archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, targetDir, overwriteFiles: true);
            return;
        }

        if (archivePath.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            TarFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
            return;
        }

        throw new InvalidOperationException("Java 运行时压缩包格式不受支持。");
    }

    public static async Task<JavaRuntimeInstallResult> DownloadJavaRuntimeAsync(
        int majorVersion,
        string installRoot,
        HttpClient? httpClient = null,
        IProgress<JavaRuntimeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? downloadUrl = null)
    {
        if (majorVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(majorVersion), "Java 主版本必须大于 0。");
        }

        Directory.CreateDirectory(installRoot);
        var existing = FindJavaInDirectory(Path.Combine(installRoot, $"java-{majorVersion}")) ?? "";
        if (existing.Length > 0)
        {
            progress?.Report(new JavaRuntimeDownloadProgress("复用 Java", 1, 1, existing));
            return new JavaRuntimeInstallResult(existing, Path.GetFullPath(Path.Combine(installRoot, $"java-{majorVersion}")), Reused: true);
        }

        using var ownedClient = httpClient is null ? new HttpClient() : null;
        var client = httpClient ?? ownedClient!;
        var url = downloadUrl ?? JavaRuntimeDownloadUrl(majorVersion);
        progress?.Report(new JavaRuntimeDownloadProgress($"下载 Java {majorVersion}", 0, 0, url));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var filename = DownloadFilename(response, url);
        var extension = ArchiveExtension(filename);
        var archivePath = Path.Combine(installRoot, $"java-{majorVersion}-{Guid.NewGuid():N}{extension}");
        var extractDir = Path.Combine(installRoot, $"java-{majorVersion}-{Guid.NewGuid():N}");
        try
        {
            var total = response.Content.Headers.ContentLength ?? 0;
            var readTotal = 0L;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(archivePath))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    progress?.Report(new JavaRuntimeDownloadProgress($"下载 Java {majorVersion}", readTotal, total, filename));
                }
            }

            progress?.Report(new JavaRuntimeDownloadProgress("解压 Java", readTotal, total, filename));
            ExtractJavaArchive(archivePath, extractDir);
            var javaPath = FindJavaInDirectory(extractDir)
                ?? throw new InvalidOperationException("Java 运行时已解压，但没有找到 java 可执行文件。");
            progress?.Report(new JavaRuntimeDownloadProgress("Java 已就绪", total == 0 ? 1 : total, total == 0 ? 1 : total, javaPath));
            return new JavaRuntimeInstallResult(javaPath, extractDir, Reused: false);
        }
        catch
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(extractDir);
            throw;
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    public static int GetJavaMajorVersion(string javaPath)
        => ParseJavaMajorVersion(RunJavaVersion(javaPath));

    // 从 `java -version` 输出解析主版本号：兼容旧式 1.8.0（返回 8）与新式 17.0.1（返回 17）。
    public static int ParseJavaMajorVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }

        var match = Regex.Match(output, @"version \""(?:(?<legacy>1)\.)?(?<major>\d+)");
        return match.Success ? int.Parse(match.Groups["major"].Value) : 0;
    }

    private static string RunJavaVersion(string javaPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(javaPath, "-version")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "";
            }

            // 先异步读取两路输出，避免管道缓冲区写满导致的死锁；
            // 若进程在超时后仍未退出，强制结束以保证 5 秒上限真正生效。
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                process.WaitForExit(1000);
            }

            return SafeResult(stderrTask) + SafeResult(stdoutTask);
        }
        catch
        {
            return "";
        }
    }

    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.Wait(1000) ? task.Result : "";
        }
        catch
        {
            return "";
        }
    }

    private static bool LooksLikeZip(string archivePath)
    {
        try
        {
            using var file = File.OpenRead(archivePath);
            Span<byte> signature = stackalloc byte[4];
            return file.Read(signature) == 4 &&
                   signature[0] == 0x50 &&
                   signature[1] == 0x4B &&
                   signature[2] is 0x03 or 0x05 or 0x07 &&
                   signature[3] is 0x04 or 0x06 or 0x08;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeGzip(string archivePath)
    {
        try
        {
            using var file = File.OpenRead(archivePath);
            Span<byte> signature = stackalloc byte[2];
            return file.Read(signature) == 2 && signature[0] == 0x1F && signature[1] == 0x8B;
        }
        catch
        {
            return false;
        }
    }

    private static string DownloadFilename(HttpResponseMessage response, string url)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var headerName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(headerName))
        {
            return headerName.Trim('"');
        }

        var uriName = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.LocalPath) : "";
        if (!string.IsNullOrWhiteSpace(uriName))
        {
            return uriName;
        }

        return OperatingSystem.IsWindows() ? "java.zip" : "java.tar.gz";
    }

    private static string ArchiveExtension(string filename)
    {
        if (filename.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return ".tar.gz";
        }

        if (filename.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            return ".tgz";
        }

        if (filename.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            return ".tar";
        }

        return ".zip";
    }

    private static void TryDeleteFile(string path)
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

public sealed record JavaRuntimeDownloadProgress(string Phase, long DownloadedBytes, long TotalBytes, string CurrentItem)
{
    public double Progress => TotalBytes <= 0 ? 0 : Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1);
}

public sealed record JavaRuntimeInstallResult(string JavaPath, string InstallDirectory, bool Reused);
