using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McGo.Core.Updates;

public sealed class UpdaterService
{
    private const int BufferSize = 1024 * 1024;
    private static readonly Regex Sha256Regex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex SafeFilenameRegex = new("[^A-Za-z0-9._-]+", RegexOptions.Compiled);
    private static readonly Regex SemverRegex = new("^v?(?<core>\\d+(?:\\.\\d+)*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\\+.*)?$", RegexOptions.Compiled);
    private readonly HttpClient _httpClient;

    public UpdaterService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public static bool IsNewerVersion(string remoteVersion, string currentVersion)
    {
        var remoteSemver = SemverParts(remoteVersion);
        var currentSemver = SemverParts(currentVersion);
        if (remoteSemver is not null && currentSemver is not null)
        {
            return CompareSemver(remoteSemver.Value, currentSemver.Value) > 0;
        }

        return CompareVersionKey(remoteVersion, currentVersion) > 0;
    }

    public static string SanitizeFilenameComponent(object? value, string fallback = "download")
    {
        var result = SafeFilenameRegex.Replace((value?.ToString() ?? "").Trim(), "-").Trim('.', '-', '_', ' ');
        return result.Length == 0 ? fallback : result;
    }

    public async Task<JsonObject> FetchUpdateManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(manifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        JsonObject? data;
        try
        {
            data = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        }
        catch (Exception exc)
        {
            throw new InvalidOperationException("更新清单不是有效 JSON。", exc);
        }

        if (data is null)
        {
            throw new InvalidOperationException("更新清单格式无效。");
        }

        var version = data["version"]?.GetValue<string>()?.Trim() ?? "";
        var downloadUrl = data["url"]?.GetValue<string>()?.Trim()
                          ?? data["download_url"]?.GetValue<string>()?.Trim()
                          ?? data["downloadUrl"]?.GetValue<string>()?.Trim()
                          ?? "";
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("更新清单缺少 version。");
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException("更新清单缺少 url。");
        }

        data["version"] = version;
        data["url"] = ValidateDownloadUrl(new Uri(new Uri(manifestUrl), downloadUrl).ToString());
        data["sha256"] = ManifestSha256(data["sha256"]?.GetValue<string>());
        data["size"] = ManifestSize(data["size"]?.ToString());
        data["notes"] = data["notes"]?.GetValue<string>()?.Trim()
                        ?? data["changelog"]?.GetValue<string>()?.Trim()
                        ?? "";
        return data;
    }

    public async Task<JsonObject> CheckForUpdateAsync(string manifestUrl, string currentVersion, Func<string>? currentBuildSha256 = null, CancellationToken cancellationToken = default)
    {
        var manifest = await FetchUpdateManifestAsync(manifestUrl, cancellationToken);
        manifest["current_version"] = currentVersion;
        var currentSha256 = currentBuildSha256?.Invoke() ?? CurrentBuildSha256();
        var remoteSha256 = manifest["sha256"]?.GetValue<string>() ?? "";
        var buildChanged = manifest["version"]?.GetValue<string>() == currentVersion
                           && currentSha256.Length > 0
                           && remoteSha256.Length > 0
                           && !string.Equals(currentSha256, remoteSha256, StringComparison.OrdinalIgnoreCase);
        manifest["current_sha256"] = currentSha256;
        manifest["build_changed"] = buildChanged;
        manifest["update_available"] = IsNewerVersion(manifest["version"]?.GetValue<string>() ?? "", currentVersion) || buildChanged;
        return manifest;
    }

    public async Task<JsonObject> DownloadUpdateAsync(JsonObject manifest, string? targetDir = null, IProgress<int>? progress = null, Action<string>? status = null, CancellationToken cancellationToken = default)
    {
        targetDir ??= Path.GetTempPath();
        Directory.CreateDirectory(targetDir);
        var downloadUrl = ValidateDownloadUrl(manifest["url"]?.GetValue<string>() ?? "");
        var expectedSha256 = ManifestSha256(manifest["sha256"]?.GetValue<string>());
        var filename = SanitizeFilenameComponent(Path.GetFileName(new Uri(downloadUrl).LocalPath), "McGo-update.exe");
        if (!filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            filename += ".exe";
        }

        var versionSuffix = SanitizeFilenameComponent(manifest["version"]?.GetValue<string>(), "latest");
        var stem = Path.GetFileNameWithoutExtension(filename);
        var extension = Path.GetExtension(filename);
        var targetPath = Path.Combine(targetDir, $"{stem}-{versionSuffix}{(extension.Length == 0 ? ".exe" : extension)}");
        var tempPath = targetPath + ".download";
        status?.Invoke($"正在下载 McGo {manifest["version"]?.GetValue<string>() ?? ""}...");
        progress?.Report(0);

        try
        {
            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            var expectedSize = ManifestSize(manifest["size"]?.ToString());
            if (expectedSize > 0 && total > 0 && total != expectedSize)
            {
                throw new InvalidOperationException("更新文件大小与清单不一致。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(tempPath);
            var buffer = new byte[BufferSize];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                if (total > 0)
                {
                    progress?.Report(Math.Min(100, (int)(downloaded * 100 / total)));
                }
            }

            await target.DisposeAsync();
            if (downloaded <= 0)
            {
                throw new InvalidOperationException("更新文件为空。");
            }

            if (total > 0 && downloaded != total || expectedSize > 0 && downloaded != expectedSize)
            {
                throw new InvalidOperationException("更新文件大小与清单不一致。");
            }

            if (!LooksLikeWindowsExecutable(tempPath))
            {
                throw new InvalidOperationException("更新文件不是有效的 Windows 可执行文件。");
            }

            var actualSha256 = FileSha256(tempPath);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("更新文件 SHA256 校验失败。");
            }

            File.Move(tempPath, targetPath, overwrite: true);
            progress?.Report(100);
            var payload = JsonNode.Parse(manifest.ToJsonString())!.AsObject();
            payload["path"] = targetPath;
            payload["sha256"] = actualSha256;
            payload["bytes"] = downloaded;
            return payload;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static string FileSha256(string path)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    public static bool LooksLikeWindowsExecutable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }
        catch
        {
            return false;
        }
    }

    public static string CurrentExecutablePath()
        => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;

    public static bool CanReplaceCurrentExecutable(string? path = null)
    {
        path ??= CurrentExecutablePath();
        var name = Path.GetFileName(path).ToLowerInvariant();
        return OperatingSystem.IsWindows() && name.EndsWith(".exe") && !name.Contains("python");
    }

    public static string CurrentBuildSha256(string? executablePath = null)
    {
        executablePath ??= CurrentExecutablePath();
        if (!CanReplaceCurrentExecutable(executablePath))
        {
            return "";
        }

        try
        {
            var digest = FileSha256(executablePath);
            return Sha256Regex.IsMatch(digest) ? digest : "";
        }
        catch
        {
            return "";
        }
    }

    public static UpdateScheduleResult ScheduleWindowsReplaceAndRestart(string downloadPath, string? executablePath = null, bool? targetDirectoryWritable = null)
    {
        executablePath = Path.GetFullPath(executablePath ?? CurrentExecutablePath());
        downloadPath = Path.GetFullPath(downloadPath);
        if (!CanReplaceCurrentExecutable(executablePath))
        {
            throw new InvalidOperationException("当前不是打包后的 McGo.exe，无法执行自更新替换。");
        }

        var logPath = Path.Combine(Path.GetTempPath(), "mcgo-update.log");
        var needsElevation = !(targetDirectoryWritable ?? TargetDirectoryWritable(executablePath));
        var applyCommand = BuildUpdateApplyCommand(downloadPath, executablePath, Environment.ProcessId, logPath, needsElevation);
        var encodedCommand = PowershellEncodedCommand(applyCommand);
        if (needsElevation)
        {
            encodedCommand = PowershellEncodedCommand(BuildElevationCommand(encodedCommand));
        }

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);
        Process.Start(startInfo);
        return new UpdateScheduleResult(needsElevation, logPath);
    }

    public static string PowershellEncodedCommand(string command)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    public static string BuildUpdateApplyCommand(string downloadPath, string executablePath, int parentPid, string logPath, bool elevated = false)
        => string.Join(Environment.NewLine, [
            "$Source = " + PowershellQuote(downloadPath),
            "$Destination = " + PowershellQuote(executablePath),
            "$ParentPid = " + parentPid,
            "$LogPath = " + PowershellQuote(logPath),
            "$Elevated = $" + elevated.ToString().ToLowerInvariant(),
            "$ErrorActionPreference = 'Stop'",
            "function Write-UpdateLog([string]$message) {",
            "  Add-Content -LiteralPath $LogPath -Value ((Get-Date -Format o) + ' ' + $message) -Encoding UTF8",
            "}",
            "try {",
            "  Wait-Process -Id $ParentPid -Timeout 120 -ErrorAction SilentlyContinue",
            "  Copy-Item -LiteralPath $Source -Destination $Destination -Force",
            "  Start-Process -FilePath $Destination",
            "  Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue",
            "} catch {",
            "  Write-UpdateLog (\"update failed: \" + $_.Exception.Message)",
            "  throw",
            "}",
        ]);

    public static string BuildElevationCommand(string encodedApplyCommand)
        => string.Join(Environment.NewLine, [
            "$ErrorActionPreference = 'Stop'",
            "$args = @(",
            "  '-NoProfile',",
            "  '-ExecutionPolicy', 'Bypass',",
            "  '-EncodedCommand', " + PowershellQuote(encodedApplyCommand),
            ")",
            "Start-Process -FilePath 'powershell.exe' -ArgumentList $args -Verb RunAs -WindowStyle Hidden",
        ]);

    private static string ValidateDownloadUrl(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("更新清单 url 必须是 HTTP 或 HTTPS 地址。");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("更新清单 url 缺少主机名。");
        }

        return downloadUrl;
    }

    private static long ManifestSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (!long.TryParse(value, out var size) || size < 0)
        {
            throw new InvalidOperationException("更新清单 size 格式无效。");
        }

        return size;
    }

    private static string ManifestSha256(string? value)
    {
        var digest = (value ?? "").Trim().ToLowerInvariant();
        if (digest.Length == 0)
        {
            throw new InvalidOperationException("更新清单缺少 sha256。");
        }

        if (!Sha256Regex.IsMatch(digest))
        {
            throw new InvalidOperationException("更新清单 sha256 格式无效。");
        }

        return digest;
    }

    private static (int[] Core, string[]? Pre)? SemverParts(string version)
    {
        var match = SemverRegex.Match((version ?? "").Trim());
        if (!match.Success)
        {
            return null;
        }

        var core = match.Groups["core"].Value.Split('.').Select(int.Parse).ToArray();
        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : null;
        return (core, prerelease);
    }

    private static int CompareSemver((int[] Core, string[]? Pre) left, (int[] Core, string[]? Pre) right)
    {
        var width = Math.Max(left.Core.Length, right.Core.Length);
        for (var index = 0; index < width; index++)
        {
            var leftValue = index < left.Core.Length ? left.Core[index] : 0;
            var rightValue = index < right.Core.Length ? right.Core[index] : 0;
            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return ComparePrerelease(left.Pre, right.Pre);
    }

    private static int ComparePrerelease(string[]? left, string[]? right)
    {
        if (left is null && right is null) return 0;
        if (left is null) return 1;
        if (right is null) return -1;
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = int.TryParse(left[index], out var leftNumber);
            var rightNumeric = int.TryParse(right[index], out var rightNumber);
            var comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : string.Compare(left[index], right[index], StringComparison.OrdinalIgnoreCase);
            if (leftNumeric != rightNumeric)
            {
                comparison = leftNumeric ? -1 : 1;
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int CompareVersionKey(string left, string right)
    {
        var leftParts = Regex.Split((left ?? "").Split('+')[0], "[.\\-_]").Where(part => part.Length > 0).ToArray();
        var rightParts = Regex.Split((right ?? "").Split('+')[0], "[.\\-_]").Where(part => part.Length > 0).ToArray();
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            var leftPart = index < leftParts.Length ? leftParts[index] : "";
            var rightPart = index < rightParts.Length ? rightParts[index] : "";
            var leftNumeric = int.TryParse(leftPart, out var leftNumber);
            var rightNumeric = int.TryParse(rightPart, out var rightNumber);
            var comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool TargetDirectoryWritable(string destination)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination)) ?? ".";
        var probe = Path.Combine(directory, $".mcgo-update-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "test", Encoding.ASCII);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private static string PowershellQuote(string value)
        => "'" + value.Replace("'", "''") + "'";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}

public sealed record UpdateScheduleResult(bool Elevated, string LogPath);
