using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McGo.Core.Minecraft;

public sealed class LauncherService
{
    public string? FindVersionJsonPath(string gameDirectory, string versionId)
    {
        var versionDir = Path.Combine(gameDirectory, "versions", versionId);
        var preferredPath = Path.Combine(versionDir, $"{versionId}.json");
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        if (!Directory.Exists(versionDir))
        {
            return null;
        }

        var candidates = Directory.GetFiles(versionDir, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Manifest: LoadJsonObject(path)))
            .Where(item => LooksLikeVersionManifest(item.Manifest))
            .Select(item =>
            {
                var score = 0;
                var filename = Path.GetFileNameWithoutExtension(item.Path);
                if (filename == versionId) score += 100;
                if (item.Manifest?["id"]?.GetValue<string>() == versionId) score += 80;
                if (!string.IsNullOrWhiteSpace(item.Manifest?["mainClass"]?.GetValue<string>())) score += 20;
                if (!string.IsNullOrWhiteSpace(item.Manifest?["inheritsFrom"]?.GetValue<string>())) score += 10;
                if (item.Manifest?["arguments"] is not null || item.Manifest?["minecraftArguments"] is not null) score += 10;
                return (score, item.Path);
            })
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToList();

        return candidates.FirstOrDefault().Path;
    }

    public string? FindVersionJarPath(string gameDirectory, string versionId, JsonObject? versionJson = null)
    {
        var versionDir = Path.Combine(gameDirectory, "versions", versionId);
        if (!Directory.Exists(versionDir))
        {
            return null;
        }

        var jarCandidates = Directory.GetFiles(versionDir, "*.jar", SearchOption.TopDirectoryOnly);
        if (jarCandidates.Length == 0)
        {
            return null;
        }

        var preferredNames = new HashSet<string>(StringComparer.Ordinal) { versionId };
        var manifestId = versionJson?["id"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrWhiteSpace(manifestId))
        {
            preferredNames.Add(manifestId);
        }

        var manifestPath = FindVersionJsonPath(gameDirectory, versionId);
        if (!string.IsNullOrEmpty(manifestPath))
        {
            preferredNames.Add(Path.GetFileNameWithoutExtension(manifestPath));
        }

        foreach (var preferredName in preferredNames)
        {
            var jarPath = Path.Combine(versionDir, $"{preferredName}.jar");
            if (File.Exists(jarPath))
            {
                return jarPath;
            }
        }

        if (jarCandidates.Length == 1)
        {
            return jarCandidates[0];
        }

        return jarCandidates
            .Select(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var score = 0;
                if (preferredNames.Contains(name)) score += 100;
                if (preferredNames.Any(preferred => preferred.Length > 0 && name.Contains(preferred, StringComparison.Ordinal))) score += 30;
                if (name.EndsWith("-natives", StringComparison.OrdinalIgnoreCase)) score -= 50;
                return (score, path);
            })
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.path, StringComparer.Ordinal)
            .First()
            .path;
    }

    public JsonObject? GetVersionJson(string gameDirectory, string versionId)
    {
        var path = FindVersionJsonPath(gameDirectory, versionId);
        return path is null ? null : LoadJsonObject(path);
    }

    public List<JsonObject> GetVersionInheritanceChain(string gameDirectory, string versionId)
    {
        var chain = new List<JsonObject>();
        var currentId = versionId;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (!string.IsNullOrWhiteSpace(currentId) && seen.Add(currentId))
        {
            var versionJson = GetVersionJson(gameDirectory, currentId);
            if (versionJson is null)
            {
                break;
            }

            chain.Add(versionJson);
            currentId = versionJson["inheritsFrom"]?.GetValue<string>() ?? "";
        }

        return chain;
    }

    public IReadOnlyList<string> GetLocalVersions(string gameDirectory = ".minecraft")
    {
        var versionsDir = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versionsDir))
        {
            return [];
        }

        return Directory.GetDirectories(versionsDir)
            .Select(Path.GetFileName)
            .Where(versionId => !string.IsNullOrWhiteSpace(versionId) && FindVersionJsonPath(gameDirectory, versionId!) is not null)
            .Cast<string>()
            .OrderBy(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 推断启动某个版本所需的最低 Java 大版本。
    /// 优先读版本 JSON 自带的 javaVersion.majorVersion（权威值），
    /// 拿不到时按 Minecraft 官方各版本要求回退。
    /// </summary>
    /// <summary>
    /// 解析快照版本号（形如 24w14a / 21w43a）并按发布日期推断 Java 需求。
    ///
    /// Java 需求升级在快照线上对应的真实周次：
    ///   1.17 开发期（21w19a 起改用 Java 16）→ 2021 年第 19 周
    ///   1.18 开发期（21w37a 起改用 Java 17）→ 2021 年第 37 周
    ///   1.20.5 开发期（24w03a 起改用 Java 21）→ 2024 年第 3 周
    ///
    /// 返回 null 表示不是可识别的快照，交给调用方按 1.x 规则处理。
    /// </summary>
    private static int? TryParseSnapshotJavaBaseline(string versionId)
    {
        var match = Regex.Match(versionId.ToLowerInvariant(), @"\b(\d{2})w(\d{2})([a-z])\b");
        if (!match.Success)
        {
            return null;
        }

        var year = 2000 + int.Parse(match.Groups[1].Value);
        var week = int.Parse(match.Groups[2].Value);
        if (year < 2011 || year > 2100 || week < 1 || week > 53)
        {
            return null;
        }

        // 用「年*100+周」做线性比较，避免跨年计算。
        // 阈值必须是完整 stamp（年份也要算进去），写成 2403 会让 20w45a(202045) 误判成 Java 21。
        var stamp = (year * 100) + week;
        return stamp switch
        {
            >= 202403 => 21,   // 24w03a 起：1.20.5 开发线
            >= 202137 => 17,   // 21w37a 起：1.18 开发线
            >= 202119 => 16,   // 21w19a 起：1.17 开发线
            _ => 8,
        };
    }

    public int InferRequiredJavaVersion(string gameDirectory, string versionId)
    {
        // 默认 8 是最保守的：老版本用高版本 Java 会崩，用低版本至少能启动并给出明确升级提示。
        var baseline = 8;
        // 快照（形如 24w14a）没有 1.x 版本号，按发布年份+周次推断，
        // 依据是 Java 需求切换的真实时间点（下方 SnapshotJavaBaseline）。
        var snapshot = TryParseSnapshotJavaBaseline(versionId);
        if (snapshot.HasValue)
        {
            baseline = Math.Max(baseline, snapshot.Value);
        }
        // 只匹配形如 1.20 / 1.20.1 的 Minecraft 版本号，避免被 loader 版本号误导，
        // 例如 fabric-loader-0.16.0-1.20.1 不能被前缀的 0.16 当成 MC 版本。
        var matches = Regex.Matches(versionId.ToLowerInvariant(), @"1\.(\d+)(?:\.(\d+))?");
        if (matches.Count > 0)
        {
            // 取最高的那个匹配作为主版本（去掉 loader 版本号带来的噪声）
            var best = matches.Cast<Match>()
                .OrderByDescending(item => int.Parse(item.Groups[1].Value))
                .ThenByDescending(item => item.Groups[2].Success ? int.Parse(item.Groups[2].Value) : 0)
                .First();
            var minor = int.Parse(best.Groups[1].Value);
            var patch = best.Groups[2].Success ? int.Parse(best.Groups[2].Value) : 0;

            // Minecraft → Java 的官方要求（来自各版本 version.json 的 javaVersion.majorVersion）：
            //   1.16.5 及更早 → Java 8
            //   1.17.x        → Java 16
            //   1.18 - 1.20.4 → Java 17
            //   1.20.5 及以后 → Java 21
            baseline = minor switch
            {
                >= 21 => 21,
                20 => patch >= 5 ? 21 : 17,
                18 or 19 => 17,
                17 => 16,
                _ => 8,
            };
        }

        int? highestDeclared = null;
        foreach (var versionJson in GetVersionInheritanceChain(gameDirectory, versionId))
        {
            var majorVersion = versionJson["javaVersion"]?["majorVersion"]?.GetValue<int?>();
            if (majorVersion.HasValue)
            {
                highestDeclared = highestDeclared.HasValue ? Math.Max(highestDeclared.Value, majorVersion.Value) : majorVersion.Value;
            }
        }

        return Math.Max(baseline, highestDeclared ?? baseline);
    }

    public List<string> BuildLaunchCommand(LaunchOptions options)
    {
        var chain = GetVersionInheritanceChain(options.GameDirectory, options.VersionId);
        if (chain.Count == 0)
        {
            throw new FileNotFoundException($"未找到版本清单：{options.VersionId}");
        }

        var versionJson = MergeVersionChain(chain);
        var versionJars = new List<string>();
        foreach (var item in chain)
        {
            var itemId = item["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            var jarPath = FindVersionJarPath(options.GameDirectory, itemId, item);
            if (!string.IsNullOrEmpty(jarPath) && File.Exists(jarPath) && !versionJars.Contains(jarPath))
            {
                versionJars.Add(jarPath);
            }
        }

        if (versionJars.Count == 0)
        {
            throw new FileNotFoundException($"未找到可启动的版本 JAR：{options.VersionId}");
        }

        var nativesVersionId = versionJson["id"]?.GetValue<string>() ?? options.VersionId;
        foreach (var item in chain)
        {
            if (item["downloads"]?["client"] is not null || item["assetIndex"] is not null)
            {
                nativesVersionId = item["id"]?.GetValue<string>() ?? nativesVersionId;
                break;
            }
        }

        var nativesDir = Path.Combine(options.GameDirectory, "versions", nativesVersionId, $"{nativesVersionId}-natives");
        var runtimeDirectory = options.RuntimeDirectory ?? options.GameDirectory;
        Directory.CreateDirectory(runtimeDirectory);
        var assetsDir = Path.Combine(options.GameDirectory, "assets");

        var features = new Dictionary<string, bool>
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = options.WindowWidth > 0 && options.WindowHeight > 0,
        };

        var classpath = new List<string>();
        foreach (var library in versionJson["libraries"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (!LibraryAllowed(library, features))
            {
                continue;
            }

            var path = LibraryPath(options.GameDirectory, library);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                classpath.Add(path);
            }
        }

        classpath.AddRange(versionJars);
        var classpathString = string.Join(Path.PathSeparator, classpath);
        var context = new Dictionary<string, string>
        {
            ["auth_player_name"] = options.Username ?? "Player",
            ["version_name"] = versionJson["id"]?.GetValue<string>() ?? options.VersionId,
            ["game_directory"] = runtimeDirectory,
            ["assets_root"] = assetsDir,
            ["assets_index_name"] = versionJson["assetIndex"]?["id"]?.GetValue<string>() ?? "",
            ["auth_uuid"] = options.Uuid ?? "00000000-0000-0000-0000-000000000000",
            ["auth_access_token"] = options.MinecraftAccessToken ?? "0",
            ["auth_xuid"] = "0",
            ["clientid"] = "",
            ["client_id"] = "",
            ["user_type"] = options.MinecraftAccessToken is null ? "mojang" : "msa",
            ["version_type"] = versionJson["type"]?.GetValue<string>() ?? "release",
            ["launcher_name"] = "McGo",
            ["launcher_version"] = "1.0",
            ["natives_directory"] = nativesDir,
            ["library_directory"] = Path.Combine(options.GameDirectory, "libraries"),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["classpath"] = classpathString,
            ["user_properties"] = "{}",
            ["resolution_width"] = (options.WindowWidth > 0 ? options.WindowWidth : 854).ToString(),
            ["resolution_height"] = (options.WindowHeight > 0 ? options.WindowHeight : 480).ToString(),
        };

        var modernArguments = versionJson["arguments"] as JsonObject;
        var gameArguments = modernArguments?["game"] is JsonArray modernGame
            ? ArgumentList(modernGame, context, features)
            : LegacyGameArguments(versionJson, context);

        var resolvedModernJvm = modernArguments?["jvm"] is JsonArray modernJvm
            ? ArgumentList(modernJvm, context, features)
            : [];
        var jvmArguments = resolvedModernJvm.Count > 0
            ? resolvedModernJvm
            : [.. MemoryArguments(options.MinMemoryMb, options.MaxMemoryMb == 0 ? 2048 : options.MaxMemoryMb), .. GcArguments(options.GcStrategy), $"-Djava.library.path={nativesDir}"];

        if (resolvedModernJvm.Count > 0 && !string.Join(' ', jvmArguments).Contains("-Djava.library.path=", StringComparison.Ordinal))
        {
            jvmArguments.Add($"-Djava.library.path={nativesDir}");
        }

        var command = new List<string> { options.JavaPath };
        command.AddRange(options.ExtraJvmArgs);
        command.AddRange(jvmArguments);
        if (!jvmArguments.Contains("-cp") && !jvmArguments.Contains("--class-path"))
        {
            command.Add("-cp");
            command.Add(classpathString);
        }

        gameArguments.AddRange(options.ExtraGameArgs);
        command.Add(versionJson["mainClass"]?.GetValue<string>() ?? "net.minecraft.client.main.Main");
        command.AddRange(gameArguments);
        return command;
    }

    public Process LaunchMinecraft(LaunchOptions options)
    {
        var command = BuildLaunchCommand(options);
        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Minecraft process failed to start.");
    }

    private static JsonObject MergeVersionChain(IReadOnlyList<JsonObject> chain)
    {
        var resolved = new JsonObject();
        var libraries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var versionJson in chain.Reverse())
        {
            foreach (var library in versionJson["libraries"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                // 以模块标识(group:artifact[:classifier],不含版本)去重,使加载器提升的
                // 依赖版本覆盖父级(原版)的旧版本;否则同一库的两个版本会同时进入 classpath,
                // 且原版旧版排在前面被 JVM 优先加载,导致 NoSuchMethodError 之类的启动崩溃。
                // natives 等带 classifier 的构件因保留 classifier 而不会被误合并。
                var key = LibraryModuleKey(library);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    libraries[key] = JsonNode.Parse(library.ToJsonString())!.AsObject();
                }
            }

            foreach (var pair in versionJson)
            {
                if (pair.Key == "libraries")
                {
                    continue;
                }

                if (pair.Key == "arguments" && pair.Value is JsonObject arguments)
                {
                    var merged = resolved["arguments"] as JsonObject ?? new JsonObject
                    {
                        ["jvm"] = new JsonArray(),
                        ["game"] = new JsonArray(),
                    };
                    AppendArray(merged["jvm"]!.AsArray(), arguments["jvm"] as JsonArray);
                    AppendArray(merged["game"]!.AsArray(), arguments["game"] as JsonArray);
                    resolved["arguments"] = merged;
                    continue;
                }

                resolved[pair.Key] = pair.Value is null ? null : JsonNode.Parse(pair.Value.ToJsonString());
            }
        }

        resolved["libraries"] = new JsonArray(libraries.Values.Select(item => JsonNode.Parse(item.ToJsonString())).ToArray());
        return resolved;
    }

    // 计算库的去重键:优先按 Maven 模块标识(group:artifact[:classifier],剔除版本与扩展名),
    // 无 name 时退回 downloads.artifact.path,坐标非常规时退回原始 name。
    private static string? LibraryModuleKey(JsonObject library)
    {
        var name = library["name"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var at = name.IndexOf('@');
            var core = at >= 0 ? name[..at] : name;
            var parts = core.Split(':');
            if (parts.Length >= 3)
            {
                var key = $"{parts[0]}:{parts[1]}";
                return parts.Length >= 4 ? $"{key}:{parts[3]}" : key;
            }

            return name;
        }

        return library["downloads"]?["artifact"]?["path"]?.GetValue<string>();
    }

    private static void AppendArray(JsonArray target, JsonArray? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var item in source)
        {
            target.Add(item is null ? null : JsonNode.Parse(item.ToJsonString()));
        }
    }

    private static List<string> LegacyGameArguments(JsonObject versionJson, Dictionary<string, string> context)
    {
        var legacy = versionJson["minecraftArguments"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            return SplitCommandLine(legacy).Select(item => Substitute(item, context)).ToList();
        }

        return
        [
            "--username", context["auth_player_name"],
            "--version", context["version_name"],
            "--gameDir", context["game_directory"],
            "--assetsDir", context["assets_root"],
            "--assetIndex", context["assets_index_name"],
            "--uuid", context["auth_uuid"],
            "--accessToken", context["auth_access_token"],
            "--userType", context["user_type"],
            "--versionType", context["version_type"],
        ];
    }

    private static List<string> ArgumentList(JsonArray items, Dictionary<string, string> context, Dictionary<string, bool> features)
    {
        var result = new List<string>();
        foreach (var item in items)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text))
            {
                result.Add(Substitute(text, context));
                continue;
            }

            if (item is not JsonObject obj)
            {
                continue;
            }

            if (obj["rules"] is JsonArray rules && !RulesAllow(rules, features, CurrentMojangOsName(), CurrentMojangOsArch()))
            {
                continue;
            }

            switch (obj["value"])
            {
                case JsonArray values:
                    result.AddRange(values.Select(valueNode => valueNode?.GetValue<string>()).OfType<string>().Select(valueText => Substitute(valueText, context)));
                    break;
                case JsonValue single when single.TryGetValue<string>(out var valueText):
                    result.Add(Substitute(valueText, context));
                    break;
            }
        }

        return result;
    }

    private static string Substitute(string value, IReadOnlyDictionary<string, string> context)
        => Regex.Replace(value, @"\$\{([^}]+)\}", match => context.TryGetValue(match.Groups[1].Value, out var replacement) ? replacement : match.Value);

    // Mojang 规则语义：默认 disallow，按顺序遍历，命中（os+features 均匹配）的规则用其 action 覆盖结果。
    // 之前仅用 Any(action!=disallow) 且忽略 os，会在 Windows 上错误放行 osx 专属库/参数。
    public static bool RulesAllow(JsonArray? rules, IReadOnlyDictionary<string, bool> features, string osName, string osArch)
    {
        if (rules is null)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules.OfType<JsonObject>())
        {
            if (!RuleMatches(rule, features, osName, osArch))
            {
                continue;
            }

            allowed = !string.Equals(rule["action"]?.GetValue<string>(), "disallow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool RuleMatches(JsonObject rule, IReadOnlyDictionary<string, bool> features, string osName, string osArch)
    {
        if (rule["features"] is JsonObject requiredFeatures)
        {
            foreach (var pair in requiredFeatures)
            {
                var expected = pair.Value?.GetValue<bool>() ?? false;
                if (!features.TryGetValue(pair.Key, out var actual) || actual != expected)
                {
                    return false;
                }
            }
        }

        if (rule["os"] is JsonObject os)
        {
            var name = os["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, osName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var arch = os["arch"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(arch) && !string.Equals(arch, osArch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var version = os["version"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(version) && !OsVersionMatches(version))
            {
                return false;
            }
        }

        return true;
    }

    private static bool OsVersionMatches(string pattern)
    {
        try
        {
            return Regex.IsMatch(Environment.OSVersion.Version.ToString(), pattern);
        }
        catch (ArgumentException)
        {
            // 无效正则视为匹配，避免因清单错误而误排除。
            return true;
        }
    }

    internal static string CurrentMojangOsName()
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    internal static string CurrentMojangOsArch()
        => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => "arm32",
            var other => other.ToString().ToLowerInvariant(),
        };

    private static bool LibraryAllowed(JsonObject library, Dictionary<string, bool> features)
        => RulesAllow(library["rules"] as JsonArray, features, CurrentMojangOsName(), CurrentMojangOsArch());

    public static string? MavenLibraryPath(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3)
        {
            return null;
        }

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? parts[3] : "";
        var extension = parts.Length >= 5 ? parts[4] : "jar";
        var filename = $"{artifact}-{version}";
        if (!string.IsNullOrWhiteSpace(classifier))
        {
            filename += "-" + classifier;
        }

        return Path.Combine([.. group.Split('.'), artifact, version, $"{filename}.{extension}"]);
    }

    private static string? LibraryPath(string gameDirectory, JsonObject library)
    {
        var artifactPath = library["downloads"]?["artifact"]?["path"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(artifactPath))
        {
            return Path.Combine(gameDirectory, "libraries", artifactPath.Replace('/', Path.DirectorySeparatorChar));
        }

        var name = library["name"]?.GetValue<string>();
        var mavenPath = name is null ? null : MavenLibraryPath(name);
        return mavenPath is null ? null : Path.Combine(gameDirectory, "libraries", mavenPath);
    }

    private static IEnumerable<string> MemoryArguments(int minMemoryMb, int maxMemoryMb)
    {
        if (minMemoryMb > 0) yield return $"-Xms{minMemoryMb}M";
        if (maxMemoryMb > 0) yield return $"-Xmx{maxMemoryMb}M";
    }

    private static IEnumerable<string> GcArguments(string? strategy)
    {
        var normalized = (strategy ?? "G1GC").Trim().ToLowerInvariant();
        if (normalized is "none" or "默认" or "default" or "vanilla")
        {
            yield break;
        }

        if (normalized is "zgc" or "z gc")
        {
            yield return "-XX:+UseZGC";
            yield break;
        }

        if (normalized is "shenandoah" or "shenandoahgc")
        {
            yield return "-XX:+UseShenandoahGC";
            yield break;
        }

        yield return "-XX:+UnlockExperimentalVMOptions";
        yield return "-XX:+UseG1GC";
        yield return "-XX:G1NewSizePercent=20";
        yield return "-XX:G1ReservePercent=20";
        yield return "-XX:MaxGCPauseMillis=50";
        yield return "-XX:G1HeapRegionSize=32M";
    }

    private static JsonObject? LoadJsonObject(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeVersionManifest(JsonObject? data)
        => data is not null
           && !string.IsNullOrWhiteSpace(data["id"]?.GetValue<string>())
           && new[] { "mainClass", "inheritsFrom", "arguments", "minecraftArguments", "libraries", "downloads", "assetIndex" }
               .Any(key => data.ContainsKey(key));

    public static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        foreach (Match match in Regex.Matches(commandLine, @"[\""].+?[\""]|[^ ]+"))
        {
            result.Add(match.Value.Trim('"'));
        }

        return result;
    }
}

public sealed record LaunchOptions(
    string JavaPath,
    string VersionId,
    string GameDirectory,
    string? MinecraftAccessToken = null,
    string? Username = null,
    string? Uuid = null,
    string? RuntimeDirectory = null,
    IReadOnlyList<string>? ExtraJvmArgs = null,
    IReadOnlyList<string>? ExtraGameArgs = null,
    int MinMemoryMb = 0,
    int MaxMemoryMb = 0,
    int WindowWidth = 0,
    int WindowHeight = 0,
    string GcStrategy = "G1GC")
{
    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = ExtraJvmArgs ?? [];
    public IReadOnlyList<string> ExtraGameArgs { get; init; } = ExtraGameArgs ?? [];
}
