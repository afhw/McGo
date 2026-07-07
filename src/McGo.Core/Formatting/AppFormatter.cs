using System.Globalization;

namespace McGo.Core.Formatting;

public static class AppFormatter
{
    private static readonly IReadOnlyDictionary<string, string> ResourceSourceLabels = new Dictionary<string, string>
    {
        ["modrinth"] = "Modrinth",
        ["curseforge"] = "CurseForge",
        ["local"] = "本地",
    };

    public static string FormatBytes(long byteCount)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, byteCount);
        var scaled = (double)value;
        foreach (var unit in units)
        {
            if (scaled < 1024 || unit == units[^1])
            {
                return unit == "B"
                    ? $"{(long)scaled} {unit}"
                    : $"{scaled.ToString("0.0", CultureInfo.InvariantCulture)} {unit}";
            }

            scaled /= 1024;
        }

        return "0 B";
    }

    public static string FormatResourceHitLabel(IReadOnlyDictionary<string, object?> hit)
    {
        var title = StringValue(hit, "title")
                    ?? StringValue(hit, "slug")
                    ?? StringValue(hit, "project_id")
                    ?? "未命名";
        var sourceKey = StringValue(hit, "source") ?? "modrinth";
        var source = ResourceSourceLabels.TryGetValue(sourceKey, out var mapped) ? mapped : sourceKey;
        var downloads = Value(hit, "downloads") ?? 0;
        var follows = Value(hit, "follows") ?? 0;
        var downloadsText = sourceKey == "local" && long.TryParse(downloads.ToString(), out var localBytes)
            ? FormatBytes(localBytes)
            : downloads.ToString() ?? "0";
        var description = (StringValue(hit, "description") ?? string.Empty)
            .ReplaceLineEndings(" ")
            .Trim();

        var compatibility = "";
        if (sourceKey == "modrinth")
        {
            var targetGameVersion = StringValue(hit, "target_game_version") ?? "";
            var targetLoader = StringValue(hit, "target_loader") ?? "";
            var loaderSuffix = string.IsNullOrWhiteSpace(targetLoader) ? "" : $" / {targetLoader}";
            if (BoolValue(hit, "compatibility_checking"))
            {
                compatibility = $" | 正在验证 {targetGameVersion}{loaderSuffix}";
            }
            else if (!hit.ContainsKey("compatible") || BoolValue(hit, "compatible", true))
            {
                compatibility = $" | 兼容 {targetGameVersion}{loaderSuffix}";
                var versionLabel = StringValue(hit, "compatible_version") ?? "";
                if (!string.IsNullOrWhiteSpace(versionLabel))
                {
                    compatibility += $" | 文件 {versionLabel}";
                }
                else if (BoolValue(hit, "compatibility_unverified"))
                {
                    compatibility += " | 等待验证";
                }
            }
            else
            {
                compatibility = $" | 不兼容 {targetGameVersion}{loaderSuffix}";
            }
        }

        var label = $"[{source}] {title} | 下载 {downloadsText} | 收藏 {follows}{compatibility}";
        if (!string.IsNullOrWhiteSpace(description))
        {
            label += "\n" + (description.Length > 160 ? description[..160] : description);
        }

        return label;
    }

    private static object? Value(IReadOnlyDictionary<string, object?> data, string key)
        => data.TryGetValue(key, out var value) ? value : null;

    private static string? StringValue(IReadOnlyDictionary<string, object?> data, string key)
        => Value(data, key)?.ToString();

    private static bool BoolValue(IReadOnlyDictionary<string, object?> data, string key, bool fallback = false)
    {
        var value = Value(data, key);
        if (value is null)
        {
            return fallback;
        }

        if (value is bool flag)
        {
            return flag;
        }

        return bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }
}
