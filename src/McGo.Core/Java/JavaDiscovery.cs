using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace McGo.Core.Java;

/// <summary>
/// Java 运行时候选发现。
///
/// 抽出来的原因：启动页和设置页原本各有一份几乎相同的实现，
/// 任何一处补路径另一处就漏。这里统一收口，也便于单独测试。
/// </summary>
public static class JavaDiscovery
{
    private const int MaxCandidates = 40;

    /// <summary>
    /// 按优先级枚举可用的 Java 路径：
    /// 1) 用户明确配置的路径
    /// 2) PATH 上的 java
    /// 3) 游戏目录下 McGo 自己管理的运行时
    /// 4) 官方启动器自带的运行时（.minecraft\runtime）
    /// 5) Program Files 下各厂商安装的 JDK
    /// </summary>
    public static IReadOnlyList<string> FindCandidates(string gameDirectory, string configuredPath)
        => FindCandidates(gameDirectory, configuredPath, null);

    /// <summary>Discover candidates with optional Program Files roots for isolated tests.</summary>
    public static IReadOnlyList<string> FindCandidates(
        string gameDirectory,
        string configuredPath,
        IEnumerable<string>? windowsJavaRoots)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, configuredPath);
        AddCandidate(candidates, "java");

        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            AddInstallationCandidates(candidates, Path.Combine(gameDirectory, "java_runtimes"));
        }

        try
        {
            foreach (var bundled in JavaRuntimeService.FindBundledRuntimeCandidates())
            {
                AddCandidate(candidates, bundled);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Continue with PATH and vendor installations when bundled runtimes are inaccessible.
        }

        foreach (var pathDir in EnumeratePathDirectories())
        {
            var javaPath = Path.Combine(pathDir, JavaExecutableName());
            if (File.Exists(javaPath))
            {
                AddCandidate(candidates, javaPath);
            }
        }

        if (OperatingSystem.IsWindows() || windowsJavaRoots is not null)
        {
            foreach (var root in windowsJavaRoots ?? EnumerateWindowsJavaRoots())
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                AddInstallationCandidates(candidates, Path.Combine(root, "Java"));
                AddInstallationCandidates(candidates, Path.Combine(root, "Eclipse Adoptium"));
                AddInstallationCandidates(candidates, Path.Combine(root, "Microsoft"));
            }
        }

        return candidates
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>
    /// 从候选里挑出与大版本匹配的 Java；精确匹配优先，不匹配返回 null。
    /// Java 的向后兼容在 Mod 环境里不可靠，因此不做"高版本也能跑"的推断。
    /// </summary>
    public static string? PickMatchingMajor(IReadOnlyList<string> candidates, int requiredMajor)
    {
        if (requiredMajor <= 0 || candidates.Count == 0)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (JavaRuntimeService.GetJavaMajorVersion(candidate) == requiredMajor)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>把候选里能识别出大版本的项汇总成「Java 8、Java 17」这样的描述。</summary>
    public static string DescribeDetectedMajors(IReadOnlyList<string> candidates)
    {
        var majors = candidates
            .Select(JavaRuntimeService.GetJavaMajorVersion)
            .Where(major => major > 0)
            .Distinct()
            .OrderBy(major => major)
            .Select(major => $"Java {major}")
            .ToList();
        return majors.Count == 0 ? "未检测到可用 Java" : string.Join("、", majors);
    }

    private static string JavaExecutableName() => OperatingSystem.IsWindows() ? "java.exe" : "java";

    private static IEnumerable<string> EnumeratePathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> EnumerateWindowsJavaRoots() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(path => !string.IsNullOrWhiteSpace(path));

    private static void AddInstallationCandidates(List<string> candidates, string root)
    {
        if (candidates.Count >= MaxCandidates || !Directory.Exists(root))
        {
            return;
        }

        // Keep support for a runtime extracted directly into the installation root.
        var directJava = Path.Combine(root, "bin", JavaExecutableName());
        if (!File.Exists(directJava))
        {
            directJava = Path.Combine(root, JavaExecutableName());
        }
        if (File.Exists(directJava))
        {
            AddCandidate(candidates, directJava);
        }

        string[] installations;
        try
        {
            installations = Directory.GetDirectories(root, "*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;
        }

        foreach (var installation in installations.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (candidates.Count >= MaxCandidates)
            {
                break;
            }

            try
            {
                AddCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(installation));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // An unavailable installation must not hide its siblings.
            }
        }
    }

    private static void AddCandidate(List<string> candidates, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path.Trim()))
        {
            path = Path.GetFullPath(path.Trim());
        }
        if (candidates.Count < MaxCandidates && !string.IsNullOrWhiteSpace(path)
            && !candidates.Contains(path.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(path.Trim());
        }
    }
}
