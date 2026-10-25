using System.Text.Json.Nodes;
using McGo.Core.Downloads;
using McGo.Core.Java;

namespace McGo.Core.Minecraft;

public sealed class LaunchPreflightService
{
    private readonly LauncherService _launcher;
    private readonly Func<string, int> _javaMajorVersionProvider;

    public LaunchPreflightService(LauncherService? launcher = null, Func<string, int>? javaMajorVersionProvider = null)
    {
        _launcher = launcher ?? new LauncherService();
        _javaMajorVersionProvider = javaMajorVersionProvider ?? JavaRuntimeService.GetJavaMajorVersion;
    }

    public LaunchPreflightResult Validate(string gameDirectory, string versionId, string javaPath, bool includeAssets = true)
    {
        var issues = new List<LaunchPreflightIssue>();
        if (string.IsNullOrWhiteSpace(versionId))
        {
            issues.Add(new LaunchPreflightIssue("version", "请先选择本地游戏版本。"));
            return new LaunchPreflightResult(issues, 0, 0);
        }

        if (string.IsNullOrWhiteSpace(javaPath))
        {
            issues.Add(new LaunchPreflightIssue("java", "请先选择 Java 路径。"));
        }
        else
        {
            var requiredJava = _launcher.InferRequiredJavaVersion(gameDirectory, versionId);
            var actualJava = _javaMajorVersionProvider(javaPath);
            if (actualJava == 0)
            {
                issues.Add(new LaunchPreflightIssue("java", $"无法识别 Java 版本：{javaPath}"));
            }
            else if (actualJava != requiredJava)
            {
                // 不做「更高版本也能跑」的放行：Java 向后兼容在 Mod 环境下很差
                // （例如 Java 21 跑 1.12.2 的 Forge 基本必崩），这里只接受精确匹配。
                var direction = actualJava < requiredJava ? "过低" : "过高";
                issues.Add(new LaunchPreflightIssue(
                    "java",
                    $"Java 版本{direction}：Minecraft {versionId} 需要 Java {requiredJava}，当前选择的是 Java {actualJava}。请换用 Java {requiredJava}。"));
            }
        }

        var chain = _launcher.GetVersionInheritanceChain(gameDirectory, versionId);
        if (chain.Count == 0)
        {
            issues.Add(new LaunchPreflightIssue("manifest", $"未找到版本清单：{versionId}"));
            return new LaunchPreflightResult(issues, 0, issues.Count);
        }

        foreach (var versionJson in chain.AsEnumerable().Reverse())
        {
            var currentVersionId = versionJson["id"]?.GetValue<string>() ?? versionId;
            try
            {
                var missing = DownloaderService.CollectMissingGameFiles(versionJson, gameDirectory, currentVersionId, includeAssets);
                foreach (var item in missing.Take(20))
                {
                    var reason = File.Exists(item.Path) ? "校验失败" : "缺失";
                    issues.Add(new LaunchPreflightIssue("file", $"{currentVersionId}: {reason} {RelativeToGameDirectory(gameDirectory, item.Path)}"));
                }

                if (missing.Count > 20)
                {
                    issues.Add(new LaunchPreflightIssue("file", $"{currentVersionId}: 还有 {missing.Count - 20} 个文件未列出"));
                }
            }
            catch (Exception exc)
            {
                issues.Add(new LaunchPreflightIssue("file", $"{currentVersionId}: 完整性检查失败：{exc.Message}"));
            }

            if (HasNativeLibrary(versionJson) && !HasExtractedNatives(gameDirectory, currentVersionId))
            {
                issues.Add(new LaunchPreflightIssue("native", $"{currentVersionId}: natives 未解压，请先使用补全/校验文件。"));
            }
        }

        return new LaunchPreflightResult(issues, _launcher.InferRequiredJavaVersion(gameDirectory, versionId), issues.Count);
    }

    private static bool HasNativeLibrary(JsonObject versionJson)
    {
        foreach (var library in versionJson["libraries"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (library["downloads"]?["classifiers"] is not JsonObject classifiers)
            {
                continue;
            }

            if (classifiers.Any(pair => pair.Key.StartsWith("natives-", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasExtractedNatives(string gameDirectory, string versionId)
    {
        var nativesDir = Path.Combine(gameDirectory, "versions", versionId, $"{versionId}-natives");
        return Directory.Exists(nativesDir) && Directory.EnumerateFileSystemEntries(nativesDir).Any();
    }

    private static string RelativeToGameDirectory(string gameDirectory, string path)
    {
        try
        {
            return Path.GetRelativePath(gameDirectory, path);
        }
        catch
        {
            return path;
        }
    }
}

public sealed record LaunchPreflightIssue(string Kind, string Message);
public sealed record LaunchPreflightResult(IReadOnlyList<LaunchPreflightIssue> Issues, int RequiredJavaVersion, int IssueCount)
{
    public bool IsOk => Issues.Count == 0;
}
