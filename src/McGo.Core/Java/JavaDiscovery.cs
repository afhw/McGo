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
    {
        var candidates = new List<string>();
        AddCandidate(candidates, configuredPath);
        AddCandidate(candidates, "java");

        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            AddCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(Path.Combine(gameDirectory, "java_runtimes")));
        }

        foreach (var bundled in JavaRuntimeService.FindBundledRuntimeCandidates())
        {
            AddCandidate(candidates, bundled);
        }

        foreach (var pathDir in EnumeratePathDirectories())
        {
            AddCandidate(candidates, Path.Combine(pathDir, JavaExecutableName()));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in EnumerateWindowsJavaRoots())
            {
                AddCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(Path.Combine(root, "Java")));
                AddCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(Path.Combine(root, "Eclipse Adoptium")));
                AddCandidate(candidates, JavaRuntimeService.FindJavaInDirectory(Path.Combine(root, "Microsoft")));
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

    private static void AddCandidate(List<string> candidates, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.Add(path.Trim());
        }
    }
}
