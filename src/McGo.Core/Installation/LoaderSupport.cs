using System.Text.RegularExpressions;

namespace McGo.Core.Installation;

/// <summary>某个加载器对某个 Minecraft 版本的可用性。</summary>
public enum LoaderSupport
{
    /// <summary>不确定：保持可用，让用户自己试（宁可让用户试，也不要错误禁用）。</summary>
    Unknown = 0,

    /// <summary>确认存在可用版本。</summary>
    Available = 1,

    /// <summary>确认不存在可用版本。</summary>
    Unavailable = 2,
}

/// <summary>
/// 「加载器 × 游戏版本」可用性的纯规则判断（不联网）。
/// 只回答"确定不支持"的情况：任何不确定都返回 <see cref="LoaderSupport.Unknown"/>，
/// 由调用方继续保持控件可用——错误置灰一个本来能装的加载器，比让用户点一下再看到
/// "未找到版本"更糟。
/// </summary>
public static class LoaderSupportRules
{
    public static readonly IReadOnlyList<string> LoaderTypes = ["fabric", "forge", "neoforge", "optifine"];

    // 全部用三段版本号构造：Version 的四段构造会把 Revision 设成 0，
    // 与三段版本比较时 -1 < 0 会让 1.20.1 被判成小于 1.20.1。
    private static readonly Version FabricFirstVersion = new(1, 14, 0);
    private static readonly Version NeoForgeFirstVersion = new(1, 20, 1);
    private static readonly Version OptifineFirstVersion = new(1, 7, 2);
    private static readonly Version ForgeFirstVersion = new(1, 1, 0);
    private static readonly Version AlphaBeta = new(0, 0, 0);

    /// <summary>
    /// 规则判断：
    /// Fabric 从 1.14 起才有正式支持（1.13.2 及更早两个镜像都查不到）；
    /// NeoForge 是 1.20.1 从 Forge 分叉出来的，更早的版本确定没有；
    /// OptiFine 最早只做到 1.7.2；Forge 自 1.1 起几乎每个正式版都有构建，
    /// 但老版本情况复杂，因此超过 1.1 后一律交给真实元数据判断。
    /// </summary>
    public static LoaderSupport Judge(string installType, string minecraftVersion)
    {
        var version = ParseVersion(minecraftVersion);
        if (version is null)
        {
            return LoaderSupport.Unknown;
        }

        return installType switch
        {
            "fabric" => version < FabricFirstVersion ? LoaderSupport.Unavailable : LoaderSupport.Available,
            "neoforge" => version < NeoForgeFirstVersion ? LoaderSupport.Unavailable : LoaderSupport.Available,
            "optifine" => version < OptifineFirstVersion ? LoaderSupport.Unavailable : LoaderSupport.Unknown,
            // Alpha/Beta 时代只有零散的第三方移植，官方构建是否存在无法用规则断言，
            // 保持可用交给真实元数据判断，避免把能装的版本错误置灰。
            "forge" => version == AlphaBeta ? LoaderSupport.Unknown : version < ForgeFirstVersion ? LoaderSupport.Unavailable : LoaderSupport.Unknown,
            _ => LoaderSupport.Unknown,
        };
    }

    /// <summary>规则层面确定不支持的加载器集合。</summary>
    public static IReadOnlySet<string> UnavailableLoaders(string minecraftVersion)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loader in LoaderTypes)
        {
            if (Judge(loader, minecraftVersion) == LoaderSupport.Unavailable)
            {
                result.Add(loader);
            }
        }

        return result;
    }

    /// <summary>
    /// 解析发布版版本号（1.20.1 / 1.20 / 1.20-pre4 / 26.3）。
    /// 快照（24w14a）无法可靠排序，返回 null 表示"不知道"，由元数据决定。
    /// </summary>
    public static Version? ParseVersion(string? minecraftVersion)
    {
        var text = (minecraftVersion ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        // Alpha / Beta（a1.2.6、b1.7.3）早于 1.0，没有任何现代加载器支持。
        if (Regex.IsMatch(text, "^[ab]\\d", RegexOptions.IgnoreCase))
        {
            return AlphaBeta;
        }

        var match = Regex.Match(text, @"^(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success)
        {
            return null;
        }

        if (!int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor))
        {
            return null;
        }

        var patch = 0;
        if (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, out patch))
        {
            return null;
        }

        return new Version(major, minor, patch);
    }
}
