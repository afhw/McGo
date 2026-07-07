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

    public static void ExtractJavaArchive(string archivePath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        if (LooksLikeZip(archivePath))
        {
            ZipFile.ExtractToDirectory(archivePath, targetDir, overwriteFiles: true);
            return;
        }

        if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
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
    {
        var output = RunJavaVersion(javaPath);
        var match = Regex.Match(output, @"version \""(?:(?<legacy>1)\.)?(?<major>\d+)");
        if (!match.Success)
        {
            return 0;
        }

        return int.Parse(match.Groups["major"].Value);
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

            process.WaitForExit(5000);
            return process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
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
