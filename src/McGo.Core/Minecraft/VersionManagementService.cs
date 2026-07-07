namespace McGo.Core.Minecraft;

public static class VersionManagementService
{
    public static VersionDeleteResult DeleteVersion(string gameDirectory, string versionId)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("游戏目录不能为空。", nameof(gameDirectory));
        }

        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new ArgumentException("版本 ID 不能为空。", nameof(versionId));
        }

        var versionsRoot = Path.GetFullPath(Path.Combine(gameDirectory, "versions"));
        var target = Path.GetFullPath(Path.Combine(versionsRoot, versionId));
        if (!Path.GetDirectoryName(target)!.Equals(versionsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝删除 versions 目录之外的路径。");
        }

        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException($"版本目录不存在：{target}");
        }

        Directory.Delete(target, recursive: true);
        return new VersionDeleteResult(versionId, target);
    }
}

public sealed record VersionDeleteResult(string VersionId, string DeletedPath);
