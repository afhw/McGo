using System.Net.Http.Json;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using McGo.Core.Minecraft;
using McGo.Core.Storage;

namespace McGo.Core.Installation;

public sealed class InstallerEngine
{
    private readonly InstallServices _services;
    private readonly LauncherService _launcher;
    private readonly VersionSettingsService _versionSettings;

    public string MinecraftVersion { get; private set; }
    public string MirrorSource { get; }
    public string GameDir { get; }
    public string JavaPath { get; }
    public bool GlobalIsolation { get; }
    public Action<string>? StatusCallback { get; }
    public IProgress<DownloadSnapshot>? ProgressCallback { get; }

    public InstallerEngine(string minecraftVersion, string mirrorSource, string gameDir, string javaPath, Action<string>? statusCallback = null, IProgress<DownloadSnapshot>? progressCallback = null, bool globalIsolation = true, InstallServices? services = null, LauncherService? launcher = null)
    {
        MinecraftVersion = minecraftVersion;
        MirrorSource = mirrorSource;
        GameDir = Path.GetFullPath(gameDir);
        JavaPath = javaPath;
        StatusCallback = statusCallback;
        ProgressCallback = progressCallback;
        GlobalIsolation = globalIsolation;
        _services = services ?? new InstallServices();
        _launcher = launcher ?? new LauncherService();
        _versionSettings = new VersionSettingsService(_launcher);
    }

    public string RuntimeDirectoryFor(string versionId)
    {
        var settings = _versionSettings.LoadVersionSettings();
        return VersionSettingsService.RuntimeDirectoryForVersion(GameDir, settings, versionId, GlobalIsolation);
    }

    public string UseBaseMinecraftVersionForLoader(string label)
    {
        var baseMinecraftVersion = _versionSettings.ResolveBaseMinecraftVersion(GameDir, MinecraftVersion);
        if (!string.IsNullOrWhiteSpace(baseMinecraftVersion) && baseMinecraftVersion != MinecraftVersion)
        {
            StatusCallback?.Invoke($"{label} 将基于原版 Minecraft {baseMinecraftVersion} 安装，已从当前加载器版本 {MinecraftVersion} 自动切换。");
            MinecraftVersion = baseMinecraftVersion;
        }

        return MinecraftVersion;
    }

    public void EnsureLauncherProfiles()
    {
        var launcherProfilesPath = Path.Combine(GameDir, "launcher_profiles.json");
        if (!File.Exists(launcherProfilesPath))
        {
            AtomicStorage.SaveJsonAtomic(launcherProfilesPath, new JsonObject { ["profiles"] = new JsonObject(), ["settings"] = new JsonObject() });
        }
    }

    public async Task<InstallResult> InstallFabricAsync(string loaderVersion = "", CancellationToken cancellationToken = default)
    {
        UseBaseMinecraftVersionForLoader("Fabric");
        var versions = await _services.GetFabricLoaderVersionsAsync(MinecraftVersion, MirrorSource, cancellationToken);
        var selected = versions.OfType<JsonObject>()
                           .FirstOrDefault(item => string.IsNullOrWhiteSpace(loaderVersion) ||
                                                   string.Equals(item["loader"]?["version"]?.GetValue<string>() ?? "", loaderVersion, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"未找到适用于 {MinecraftVersion} 的 Fabric Loader。");
        var selectedLoaderVersion = selected["loader"]?["version"]?.GetValue<string>() ?? "";
        var profileUrl = $"{InstallServices.FabricMetaRoot(MirrorSource)}/v2/versions/loader/{MinecraftVersion}/{selectedLoaderVersion}/profile/json";
        StatusCallback?.Invoke($"正在获取 Fabric 安装配置：{MinecraftVersion} / Loader {selectedLoaderVersion}");
        var profileJson = await new HttpClient().GetFromJsonAsync<JsonObject>(profileUrl, cancellationToken) ?? [];
        var versionId = profileJson["id"]?.GetValue<string>() ?? $"fabric-loader-{selectedLoaderVersion}-{MinecraftVersion}";
        var versionDir = Path.Combine(GameDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);
        AtomicStorage.SaveJsonAtomic(Path.Combine(versionDir, $"{versionId}.json"), profileJson);
        if (_launcher.GetVersionJson(GameDir, MinecraftVersion) is null)
        {
            throw new InvalidOperationException($"请先下载原版 Minecraft {MinecraftVersion}。");
        }

        await _services.DownloadProfileLibrariesAsync(profileJson, GameDir, MirrorSource, ProgressCallback, cancellationToken);
        return new InstallResult(versionId, $"Fabric {selectedLoaderVersion} 已安装", ["fabric"], "fabric");
    }

    public async Task<InstallResult> InstallFabricApiAsync(CancellationToken cancellationToken = default)
    {
        var baseMinecraftVersion = _versionSettings.ResolveBaseMinecraftVersion(GameDir, MinecraftVersion);
        var fabricVersion = _launcher.GetLocalVersions(GameDir)
            .FirstOrDefault(version => version.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase)
                                       && (_launcher.GetVersionJson(GameDir, version)?["inheritsFrom"]?.GetValue<string>() == baseMinecraftVersion
                                           || version.EndsWith("-" + baseMinecraftVersion, StringComparison.OrdinalIgnoreCase)));
        if (fabricVersion is null)
        {
            throw new InvalidOperationException($"请先为 Minecraft {baseMinecraftVersion} 安装 Fabric，再安装 Fabric API。");
        }

        var versions = await _services.GetFabricApiVersionsAsync(baseMinecraftVersion, cancellationToken);
        var selected = versions.OfType<JsonObject>().OrderByDescending(item => item["version_type"]?.GetValue<string>() == "release").FirstOrDefault()
                       ?? throw new InvalidOperationException($"未找到适用于 {baseMinecraftVersion} 的 Fabric API。");
        var files = selected["files"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        var primary = files.FirstOrDefault(item => item["primary"]?.GetValue<bool>() == true) ?? files.FirstOrDefault();
        var url = primary?["url"]?.GetValue<string>() ?? throw new InvalidOperationException("Fabric API 元数据缺少可下载文件。");
        var filename = primary["filename"]?.GetValue<string>() ?? $"fabric-api-{baseMinecraftVersion}.jar";
        var modsDir = Path.Combine(RuntimeDirectoryFor(fabricVersion), "mods");
        Directory.CreateDirectory(modsDir);
        await _services.StreamDownloadAsync(url, Path.Combine(modsDir, filename), ProgressCallback, "下载 Fabric API", MirrorSource, cancellationToken);
        return new InstallResult(fabricVersion, $"Fabric API 已安装：{filename}", ["fabric_api"], "fabric_api");
    }

    public async Task<InstallResult> InstallForgeLikeAsync(string kind, string requestedVersion = "", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(JavaPath))
        {
            throw new InvalidOperationException($"安装 {kind} 需要可用的 Java。");
        }

        UseBaseMinecraftVersionForLoader(kind == "forge" ? "Forge" : "NeoForge");
        Directory.CreateDirectory(GameDir);
        EnsureLauncherProfiles();

        string installerUrl;
        string label;
        if (kind == "forge")
        {
            var forgeVersion = string.IsNullOrWhiteSpace(requestedVersion)
                ? await _services.GetForgeVersionForMcAsync(MinecraftVersion, MirrorSource, cancellationToken)
                : requestedVersion;
            if (string.IsNullOrWhiteSpace(forgeVersion))
            {
                throw new InvalidOperationException($"未找到适用于 {MinecraftVersion} 的 Forge 版本。");
            }

            forgeVersion = forgeVersion.Trim();
            // Forge 安装器只在 Forge 自己的 Maven 上，官方源要指向 maven.minecraftforge.net，
            // 拼到 launchermeta.mojang.com/maven 上是 404。
            installerUrl = $"{InstallServices.ForgeMavenRoot(MirrorSource)}/net/minecraftforge/forge/{MinecraftVersion}-{forgeVersion}/forge-{MinecraftVersion}-{forgeVersion}-installer.jar";
            label = $"Forge {forgeVersion}";
        }
        else
        {
            var versions = await _services.GetNeoForgeListAsync(MinecraftVersion, MirrorSource, cancellationToken);
            var selected = versions.OfType<JsonObject>()
                               .FirstOrDefault(item => string.IsNullOrWhiteSpace(requestedVersion) ||
                                                       string.Equals(item["version"]?.GetValue<string>() ?? "", requestedVersion, StringComparison.OrdinalIgnoreCase) ||
                                                       string.Equals(item["rawVersion"]?.GetValue<string>() ?? "", requestedVersion, StringComparison.OrdinalIgnoreCase))
                           ?? versions.OfType<JsonObject>()
                               .OrderByDescending(item => item["rawVersion"]?.GetValue<string>() ?? "", Comparer<string>.Create(InstallServices.CompareVersionKey))
                               .FirstOrDefault()
                              ?? throw new InvalidOperationException($"未找到适用于 {MinecraftVersion} 的 NeoForge 版本。");
            var installerPath = selected["installerPath"]?.GetValue<string>() ?? "";
            var selectedVersion = selected["version"]?.GetValue<string>() ?? selected["rawVersion"]?.GetValue<string>() ?? "";
            var rawVersion = selected["rawVersion"]?.GetValue<string>() ?? selectedVersion;
            installerUrl = installerPath.Length > 0
                ? InstallServices.NeoForgeInstallerUrl(installerPath, MirrorSource)
                : InstallServices.NeoForgeInstallerUrl(MinecraftVersion, rawVersion, selectedVersion, MirrorSource);
            label = $"NeoForge {selectedVersion}";
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"mcgo-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var installerPath = Path.Combine(tempDir, $"{kind}-installer.jar");
            StatusCallback?.Invoke($"正在下载 {label} 安装器");
            await _services.StreamDownloadAsync(installerUrl, installerPath, ProgressCallback, $"下载 {label} 安装器", MirrorSource, cancellationToken);
            var before = _launcher.GetLocalVersions(GameDir).ToHashSet(StringComparer.Ordinal);
            var args = kind == "neoforge"
                ? $"-jar \"{installerPath}\" --install-client \"{GameDir}\""
                : $"-jar \"{installerPath}\" --installClient \"{GameDir}\"";
            var output = await RunProcessAsync(JavaPath, args, tempDir, StatusCallback, cancellationToken);
            var after = _launcher.GetLocalVersions(GameDir).ToHashSet(StringComparer.Ordinal);
            var installedVersion = after.Except(before).OrderBy(value => value, StringComparer.Ordinal).LastOrDefault() ?? MinecraftVersion;
            return new InstallResult(installedVersion, string.IsNullOrWhiteSpace(output) ? $"{label} 安装完成" : output, [kind], kind);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task<InstallResult> InstallOptifineAsync(string requestedVersion = "", CancellationToken cancellationToken = default)
    {
        UseBaseMinecraftVersionForLoader("OptiFine");
        var baseJson = _launcher.GetVersionJson(GameDir, MinecraftVersion)
                       ?? throw new InvalidOperationException($"请先在当前游戏目录下载原版 Minecraft {MinecraftVersion}，再安装 OptiFine。");
        var versions = await _services.GetOptifineListAsync(MirrorSource, cancellationToken);
        var candidates = versions.OfType<JsonObject>().Where(item => item["mcversion"]?.GetValue<string>() == MinecraftVersion).ToList();
        var selected = candidates.FirstOrDefault(item => OptifineVersionKey(item).Equals(requestedVersion, StringComparison.OrdinalIgnoreCase))
                       ?? InstallServices.SelectOptifineCandidate(candidates)
                       ?? throw new InvalidOperationException($"未找到适用于 {MinecraftVersion} 的 OptiFine。");
        var patch = selected["patch"]?.GetValue<string>() ?? selected["type"]?.GetValue<string>() ?? "";
        var type = selected["type"]?.GetValue<string>() ?? "HD_U";
        var versionId = InstallServices.BuildOptifineVersionId(MinecraftVersion, selected);
        var libraryVersion = InstallServices.BuildOptifineLibraryVersion(selected);
        var installerUrl = InstallServices.OptifineInstallerUrl(MinecraftVersion, type, patch);
        var tempDir = Path.Combine(Path.GetTempPath(), $"mcgo-optifine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var installerPath = Path.Combine(tempDir, "optifine-installer.jar");
            // OptiFine 安装包只从 BMCLAPI 取（optifine.net 有防盗链），
            // 因此这里显式指定镜像，避免被按当前下载源改写到 Mojang 上（404）。
            await _services.StreamDownloadAsync(installerUrl, installerPath, ProgressCallback, "下载 OptiFine 安装包", "bmclapi", cancellationToken);
            var libraryPath = Path.Combine(GameDir, "libraries", "optifine", "OptiFine", libraryVersion, $"OptiFine-{libraryVersion}.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
            File.Copy(installerPath, libraryPath, overwrite: true);
            var launchWrapperName = await EnsureLaunchWrapperLibraryAsync(installerPath, cancellationToken);
            WriteOptifineVersionJson(versionId, baseJson, $"optifine:OptiFine:{libraryVersion}", launchWrapperName);
            return new InstallResult(versionId, $"OptiFine 已安装：{versionId}", ["optifine"], "optifine");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task<InstallResult> InstallAsync(string installType, string requestedVersion = "", CancellationToken cancellationToken = default)
        => installType switch
        {
            "fabric" => await InstallFabricAsync(requestedVersion, cancellationToken),
            "fabric_api" => await InstallFabricApiAsync(cancellationToken),
            "forge" => await InstallForgeLikeAsync("forge", requestedVersion, cancellationToken),
            "neoforge" => await InstallForgeLikeAsync("neoforge", requestedVersion, cancellationToken),
            "optifine" => await InstallOptifineAsync(requestedVersion, cancellationToken),
            _ => throw new InvalidOperationException($"不支持的安装类型：{installType}"),
        };

    public async Task<InstallResult> InstallSequenceAsync(IEnumerable<string> installTypes, IReadOnlyDictionary<string, string>? requestedVersions = null, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var steps = installTypes.ToList();
        var installedVersion = MinecraftVersion;
        foreach (var installType in steps)
        {
            StatusCallback?.Invoke($"正在安装 {installType}");
            var requestedVersion = requestedVersions is not null && requestedVersions.TryGetValue(installType, out var configuredVersion)
                ? configuredVersion
                : "";
            var result = await InstallAsync(installType, requestedVersion ?? "", cancellationToken);
            messages.Add(result.Message);
            if (!string.IsNullOrWhiteSpace(result.InstalledVersion) && installType != "fabric_api")
            {
                installedVersion = result.InstalledVersion;
                MinecraftVersion = installedVersion;
            }
        }

        return new InstallResult(installedVersion, string.Join("；", messages), steps, steps.LastOrDefault() ?? "");
    }

    public InstallResult InstallSequence(IEnumerable<string> installTypes)
    {
        var steps = installTypes.ToList();
        var installedVersion = MinecraftVersion;
        foreach (var installType in steps)
        {
            if (installType is "fabric" or "forge" or "neoforge" or "optifine")
            {
                installedVersion = UseBaseMinecraftVersionForLoader(installType);
            }
        }

        return new InstallResult(installedVersion, string.Join("；", steps.Select(step => $"准备安装 {step}")), steps, steps.LastOrDefault() ?? "");
    }

    private async Task<string> EnsureLaunchWrapperLibraryAsync(string optifineInstallerPath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(optifineInstallerPath);
        var launchWrapper = archive.Entries.FirstOrDefault(entry => entry.FullName.StartsWith("launchwrapper-of-", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        if (launchWrapper is not null)
        {
            var version = launchWrapper.FullName.Split('-').Last().Replace(".jar", "", StringComparison.OrdinalIgnoreCase);
            var target = Path.Combine(GameDir, "libraries", "optifine", "launchwrapper-of", version, $"launchwrapper-of-{version}.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            launchWrapper.ExtractToFile(target, overwrite: true);
            return $"optifine:launchwrapper-of:{version}";
        }

        const string fallbackName = "net.minecraft:launchwrapper:1.12";
        var relative = InstallServices.LibraryRelativePath(fallbackName) ?? throw new InvalidOperationException("无法解析 LaunchWrapper 依赖路径。");
        var fallbackPath = Path.Combine(GameDir, "libraries", relative);
        if (!File.Exists(fallbackPath))
        {
            var url = $"{InstallServices.MirrorRoot("bmclapi")}/maven/{relative.Replace('\\', '/')}";
            await _services.StreamDownloadAsync(url, fallbackPath, ProgressCallback, "下载 LaunchWrapper", "bmclapi", cancellationToken);
        }

        return fallbackName;
    }

    private void WriteOptifineVersionJson(string versionId, JsonObject baseJson, string optifineLibraryName, string launchWrapperLibraryName)
    {
        var versionDir = Path.Combine(GameDir, "versions", versionId);
        Directory.CreateDirectory(versionDir);
        var versionJson = new JsonObject
        {
            ["id"] = versionId,
            ["inheritsFrom"] = MinecraftVersion,
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["type"] = baseJson["type"]?.GetValue<string>() ?? "release",
            ["libraries"] = new JsonArray(
                new JsonObject { ["name"] = launchWrapperLibraryName },
                new JsonObject { ["name"] = optifineLibraryName }),
        };
        if (baseJson["releaseTime"] is not null) versionJson["releaseTime"] = baseJson["releaseTime"]!.DeepClone();
        if (baseJson["time"] is not null) versionJson["time"] = baseJson["time"]!.DeepClone();
        if (baseJson["arguments"]?["game"] is not null)
        {
            versionJson["arguments"] = new JsonObject { ["game"] = new JsonArray("--tweakClass", "optifine.OptiFineTweaker"), ["jvm"] = new JsonArray() };
        }
        else
        {
            var legacyArgs = baseJson["minecraftArguments"]?.GetValue<string>()?.Trim() ?? "";
            const string tweak = "--tweakClass optifine.OptiFineTweaker";
            versionJson["minecraftArguments"] = legacyArgs.Contains(tweak, StringComparison.Ordinal) ? legacyArgs : (legacyArgs + " " + tweak).Trim();
        }

        AtomicStorage.SaveJsonAtomic(Path.Combine(versionDir, $"{versionId}.json"), versionJson, indented: true);
    }

    private static string OptifineVersionKey(JsonObject item)
    {
        var filename = item["filename"]?.GetValue<string>()?.Trim() ?? "";
        if (filename.Length > 0)
        {
            return filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? filename[..^4] : filename;
        }

        var type = item["type"]?.GetValue<string>()?.Trim() ?? "";
        var patch = item["patch"]?.GetValue<string>()?.Trim() ?? "";
        return string.Join('_', new[] { type, patch }.Where(value => value.Length > 0));
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, string workingDirectory, Action<string>? status, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动进程：{fileName}");
        var output = new StringWriter();
        while (!process.HasExited)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is not null)
            {
                status?.Invoke(line);
                output.WriteLine(line);
            }
        }

        var rest = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = await process.StandardError.ReadToEndAsync(cancellationToken);
        output.Write(rest);
        output.Write(errors);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(output.ToString().Trim().Length == 0 ? $"安装器退出码 {process.ExitCode}" : output.ToString().Trim());
        }

        return output.ToString().Trim();
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

public sealed record InstallResult(string InstalledVersion, string Message, IReadOnlyList<string> Steps, string InstallType);
