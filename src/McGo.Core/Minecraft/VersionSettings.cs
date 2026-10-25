using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McGo.Core.Storage;

namespace McGo.Core.Minecraft;

public sealed class VersionSettingsService
{
    public const string VersionSettingsFile = "version_settings.json";
    private readonly LauncherService _launcher;

    public VersionSettingsService(LauncherService? launcher = null)
    {
        _launcher = launcher ?? new LauncherService();
    }

    public JsonObject LoadVersionSettings(string path = VersionSettingsFile)
    {
        if (File.Exists(path))
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (node is not null)
            {
                node["_meta"] ??= new JsonObject();
                return node;
            }
        }

        return new JsonObject { ["_meta"] = new JsonObject { ["last_launched_version"] = "" } };
    }

    public void SaveVersionSettings(JsonObject settings, string path = VersionSettingsFile)
        => AtomicStorage.SaveJsonAtomic(path, settings, indented: true);

    public static JsonObject VersionSettingsEntry(JsonObject settings, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return [];
        }

        if (settings[versionId] is not JsonObject entry)
        {
            entry = [];
            settings[versionId] = entry;
        }

        return entry;
    }

    public static string VersionDisplayName(JsonObject settings, string versionId)
    {
        var entry = VersionSettingsEntry(settings, versionId);
        var alias = entry["alias"]?.GetValue<string>()?.Trim() ?? "";
        var icon = entry["icon"]?.GetValue<string>()?.Trim() ?? "";
        var prefix = "";
        if (entry["favorite"]?.GetValue<bool>() == true)
        {
            prefix += "* ";
        }

        if (!string.IsNullOrWhiteSpace(icon) && icon != "自动")
        {
            prefix += icon + " ";
        }

        var display = string.IsNullOrWhiteSpace(alias) ? versionId : $"{alias} [{versionId}]";
        return prefix + display;
    }

    public static string IsolatedRuntimeDirectory(string gameDir, string versionId)
        => Path.Combine(gameDir, "versions", versionId);

    public static string RuntimeDirectoryForVersion(string gameDir, JsonObject settings, string versionId, bool globalIsolation = false)
    {
        var entry = VersionSettingsEntry(settings, versionId);
        var customRuntime = entry["runtime_directory"]?.GetValue<string>()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(customRuntime))
        {
            return Path.GetFullPath(customRuntime);
        }

        return globalIsolation || entry["use_isolated_directory"]?.GetValue<bool>() == true
            ? IsolatedRuntimeDirectory(gameDir, versionId)
            : gameDir;
    }

    public static string ModsDirectoryForVersion(string gameDir, JsonObject settings, string versionId, bool globalIsolation = false)
    {
        var entry = VersionSettingsEntry(settings, versionId);
        var customMods = entry["mods_directory"]?.GetValue<string>()?.Trim() ?? "";
        return !string.IsNullOrWhiteSpace(customMods)
            ? Path.GetFullPath(customMods)
            : Path.Combine(RuntimeDirectoryForVersion(gameDir, settings, versionId, globalIsolation), "mods");
    }

    public static VersionLaunchOptions LaunchOptionsForVersion(string gameDir, JsonObject settings, string versionId, bool globalIsolation = false)
    {
        var entry = VersionSettingsEntry(settings, versionId);
        var extraJvmArgs = SplitArgs(entry["jvm_args"]?.GetValue<string>());
        var extraGameArgs = SplitArgs(entry["game_args"]?.GetValue<string>());
        var quickPlaySingleplayer = entry["quick_play_singleplayer"]?.GetValue<string>()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(quickPlaySingleplayer))
        {
            extraGameArgs.Add("--quickPlaySingleplayer");
            extraGameArgs.Add(quickPlaySingleplayer);
        }

        return new VersionLaunchOptions(
            RuntimeDirectoryForVersion(gameDir, settings, versionId, globalIsolation),
            extraJvmArgs,
            extraGameArgs,
            entry["manual_memory"]?.GetValue<bool>() == true,
            PositiveInt(entry["min_memory_mb"], 0),
            PositiveInt(entry["max_memory_mb"], 0),
            PositiveInt(entry["window_width"], 0),
            PositiveInt(entry["window_height"], 0),
            entry["pre_launch_command"]?.GetValue<string>()?.Trim() ?? "",
            (entry["gc_strategy"]?.GetValue<string>()?.Trim() ?? "G1GC") is { Length: > 0 } gc ? gc : "G1GC");
    }

    public string ResolveBaseMinecraftVersion(string gameDir, string versionId)
    {
        var versionJson = _launcher.GetVersionJson(gameDir, versionId);
        if (versionJson is not null)
        {
            var inherited = versionJson["inheritsFrom"]?.GetValue<string>();
            if (LooksLikeMinecraftVersion(inherited))
            {
                return inherited!;
            }

            foreach (var key in new[] { "minecraftVersion", "mcVersion", "releaseTarget", "clientVersion" })
            {
                var value = versionJson[key]?.GetValue<string>()?.Trim();
                if (LooksLikeMinecraftVersion(value))
                {
                    return value!;
                }
            }

            // 注意：assetIndex.id 不能当版本号用！
            // 1.8.9 的资源索引 id 就是 "1.8"，用它会把 1.8.9 误判成 1.8，
            // 导致 Forge 安装器装到错误的基础版本上（真实事故）。
            // 1.16.5 的索引 id 甚至是纯数字 "5"，更不可能等于版本号。
        }

        var detected = ExtractMinecraftVersionFromText(versionId, strict: true);
        return string.IsNullOrWhiteSpace(detected) ? versionId : detected;
    }

    public string DetectVersionType(string gameDir, string versionId)
    {
        var versionJson = _launcher.GetVersionJson(gameDir, versionId) ?? [];
        var textParts = new List<string>
        {
            versionId,
            versionJson["id"]?.GetValue<string>() ?? "",
            versionJson["mainClass"]?.GetValue<string>() ?? "",
            versionJson["inheritsFrom"]?.GetValue<string>() ?? "",
            versionJson["jar"]?.GetValue<string>() ?? "",
            versionJson["type"]?.GetValue<string>() ?? "",
        };

        foreach (var library in versionJson["libraries"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            textParts.Add(library["name"]?.GetValue<string>() ?? "");
            textParts.Add(library["downloads"]?["artifact"]?["path"]?.GetValue<string>() ?? "");
        }

        var lowered = string.Join(' ', textParts).ToLowerInvariant();
        if (lowered.Contains("fabric-loader-") || lowered.Contains(" net.fabricmc:") || lowered.Contains("fabric-loader"))
        {
            return "Fabric";
        }

        if (lowered.Contains("neoforge") || lowered.Contains(" net.neoforged:") || lowered.Contains("--launchtarget neoforgeclient"))
        {
            return "NeoForge";
        }

        if (lowered.Contains("net.minecraftforge") || lowered.Contains("forgeclient") || lowered.Contains("forge-"))
        {
            return "Forge";
        }

        if (lowered.Contains("optifine"))
        {
            return "OptiFine";
        }

        // 没有加载器时，按 version.json 的 type 区分正式版 / 快照 / 远古版本，
        // 全部归成「原版」会让用户看不出这是快照（Java 需求也不一样）。
        var officialType = versionJson["type"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
        var byType = officialType switch
        {
            "snapshot" => "快照",
            "old_beta" => "远古 Beta",
            "old_alpha" => "远古 Alpha",
            _ => "",
        };
        if (byType.Length > 0)
        {
            return byType;
        }

        // 有些版本清单没写 type，靠版本号形态兜底（如 24w14a、b1.7.3、a1.2.6）
        var trimmedId = versionId.Trim();
        if (Regex.IsMatch(trimmedId, @"^\d{2}w\d{2}[a-z]$", RegexOptions.IgnoreCase))
        {
            return "快照";
        }

        if (Regex.IsMatch(trimmedId, @"^[ab]\d+\.\d+", RegexOptions.IgnoreCase))
        {
            return trimmedId.StartsWith("b", StringComparison.OrdinalIgnoreCase) ? "远古 Beta" : "远古 Alpha";
        }

        return "正式版";
    }

    public bool VersionMatchesCategory(string gameDir, string versionId, string category)
        => category switch
        {
            "全部版本" or "收藏" or "隐藏" => true,
            "原版" => DetectVersionType(gameDir, versionId) == "正式版",
            "快照" => DetectVersionType(gameDir, versionId) == "快照",
            "远古版本" => DetectVersionType(gameDir, versionId) is "远古 Beta" or "远古 Alpha",
            "仅 OptiFine" => DetectVersionType(gameDir, versionId) == "OptiFine",
            "可安装 Mod" => new[] { "Fabric", "Forge", "NeoForge", "OptiFine" }.Contains(DetectVersionType(gameDir, versionId)),
            _ => true,
        };

    private static int PositiveInt(JsonNode? value, int fallback)
        => int.TryParse(value?.ToString(), out var parsed) && parsed > 0 ? parsed : fallback;

    private static List<string> SplitArgs(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : LauncherService.SplitCommandLine(value.Trim()).ToList();

    private static bool LooksLikeMinecraftVersion(string? value)
        => Regex.IsMatch(value ?? "", @"^(?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?|(?:\d{2}w\d{2}[a-z])$");

    private static string ExtractMinecraftVersionFromText(string text, bool strict)
    {
        var match = Regex.Match(text, @"(?<![\d.])(?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?(?![\d.])", RegexOptions.IgnoreCase);
        if (!strict && match.Success)
        {
            return match.Value;
        }

        var snapshot = Regex.Match(text, @"(?<![0-9a-z])(\d{2}w\d{2}[a-z])(?![0-9a-z])", RegexOptions.IgnoreCase);
        return snapshot.Success ? snapshot.Groups[1].Value : match.Success ? match.Value : "";
    }
}

public sealed record VersionLaunchOptions(
    string RuntimeDirectory,
    IReadOnlyList<string> ExtraJvmArgs,
    IReadOnlyList<string> ExtraGameArgs,
    bool ManualMemory,
    int MinMemoryMb,
    int MaxMemoryMb,
    int WindowWidth,
    int WindowHeight,
    string PreLaunchCommand,
    string GcStrategy);
