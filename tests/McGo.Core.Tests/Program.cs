using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using McGo.Core.Authentication;
using McGo.Core.Capsules;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Formatting;
using McGo.Core.Installation;
using McGo.Core.Java;
using McGo.Core.Logging;
using McGo.Core.Minecraft;
using McGo.Core.Modpacks;
using McGo.Core.Network;
using McGo.Core.P2P;
using McGo.Core.Personalization;
using McGo.Core.Resources;
using McGo.Core.Security;
using McGo.Core.Storage;
using McGo.Core.Updates;

var runner = new TestRunner();

runner.Add("FormatBytes uses binary units", () =>
{
    Assert.Equal("0 B", AppFormatter.FormatBytes(0));
    Assert.Equal("1.5 KB", AppFormatter.FormatBytes(1536));
    Assert.Equal("2.0 MB", AppFormatter.FormatBytes(2 * 1024 * 1024));
});

runner.Add("FormatResourceHitLabel includes compatibility", () =>
{
    var label = AppFormatter.FormatResourceHitLabel(new Dictionary<string, object?>
    {
        ["source"] = "modrinth",
        ["title"] = "Sodium",
        ["downloads"] = 123,
        ["follows"] = 45,
        ["target_game_version"] = "1.20.1",
        ["target_loader"] = "fabric",
        ["compatible_version"] = "mc1.20.1",
        ["description"] = "Fast renderer\nfor Minecraft",
    });
    Assert.Contains("Sodium", label);
    Assert.Contains("兼容 1.20.1 / fabric", label);
    Assert.Contains("Fast renderer for Minecraft", label);
});

runner.Add("Home content loads local text and gates network", () =>
{
    using var temp = TempDir.Create();
    var homePath = Path.Combine(temp.Path, "home.md");
    File.WriteAllText(homePath, "Hello McGo");
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["https://files.example/home.txt"] = new(HttpStatusCode.OK) { Content = new StringContent("Network Home") },
    };
    var service = new HomeContentService(new HttpClient(new RoutingHandler(routes)));
    var local = service.LoadContentAsync(homePath, allowNetwork: false).GetAwaiter().GetResult();
    Assert.Equal("Hello McGo", local.Text);
    Assert.Throws(() => service.LoadContentAsync("https://files.example/home.txt", allowNetwork: false).GetAwaiter().GetResult());
    var network = service.LoadContentAsync("https://files.example/home.txt", allowNetwork: true).GetAwaiter().GetResult();
    Assert.Equal("Network Home", network.Text);
    Assert.True(network.FromNetwork);
});

runner.Add("Download options clamp numeric values", () =>
{
    using var temp = TempDir.Create();
    var settings = new AppSettings(Path.Combine(temp.Path, "launcher_config.ini"), Path.Combine(temp.Path, "accounts.json"));
    settings.LoadConfig();
    settings.Config.Set("DOWNLOAD", "max_core_threads", "0");
    settings.Config.Set("DOWNLOAD", "max_asset_threads", "-9");
    settings.Config.Set("DOWNLOAD", "speed_limit_kbps", "-1");
    var options = settings.ReadDownloadOptions();
    Assert.Equal(1, options.MaxCoreConcurrency);
    Assert.Equal(1, options.MaxAssetConcurrency);
    Assert.Equal(0, options.SpeedLimitKbps);
});

runner.Add("Concise download error extracts 404 filename", () =>
{
    var message = AppSettings.ConciseDownloadError("404 https://libraries.minecraft.net/a/b/demo.jar；404 https://mirror/a/b/demo.jar");
    Assert.Contains("demo.jar", message);
    Assert.Contains("404", message);
});

runner.Add("SecureStore redacts and hydrates tokens", () =>
{
    var account = new JsonObject { ["access_token"] = "secret", ["display_name"] = "Steve" };
    var redacted = SecureStore.RedactAccountTokens(account);
    Assert.True(redacted["access_token"] is null);
    var hydrated = SecureStore.HydrateAccountTokens(redacted);
    Assert.Equal("secret", hydrated["access_token"]?.GetValue<string>());
});

runner.Add("Atomic storage writes valid json", () =>
{
    using var temp = TempDir.Create();
    var path = Path.Combine(temp.Path, "data.json");
    AtomicStorage.SaveJsonAtomic(path, new Dictionary<string, string> { ["hello"] = "世界" });
    Assert.Contains("世界", File.ReadAllText(path));
});

runner.Add("Atomic storage accepts filename-only paths", () =>
{
    using var temp = TempDir.Create();
    var original = Directory.GetCurrentDirectory();
    try
    {
        Directory.SetCurrentDirectory(temp.Path);
        AtomicStorage.AtomicWriteText("launcher_config.ini", "[APP]\nversion=1\n");
        Assert.True(File.Exists(Path.Combine(temp.Path, "launcher_config.ini")));
    }
    finally
    {
        Directory.SetCurrentDirectory(original);
    }
});

runner.Add("Atomic storage restores JSON backup and quarantines corrupt primary", () =>
{
    using var temp = TempDir.Create();
    var path = Path.Combine(temp.Path, "data.json");
    AtomicStorage.SaveJsonAtomic(path, new Dictionary<string, string> { ["value"] = "backup" });
    AtomicStorage.SaveJsonAtomic(path, new Dictionary<string, string> { ["value"] = "current" });
    File.WriteAllText(path, "{\"value\":");

    var restored = AtomicStorage.LoadJsonFile(path, new Dictionary<string, string>());
    Assert.Equal("backup", restored["value"]);
    Assert.Contains("backup", File.ReadAllText(path));
    Assert.True(Directory.GetFiles(temp.Path, "data.json.corrupt-*").Length == 1);
});

runner.Add("App settings recover accounts from backup", () =>
{
    using var temp = TempDir.Create();
    var configPath = Path.Combine(temp.Path, "launcher_config.ini");
    var accountsPath = Path.Combine(temp.Path, "accounts.json");
    var settings = new AppSettings(configPath, accountsPath);
    settings.LoadConfig();
    settings.SaveAccounts([new JsonObject { ["id"] = "backup", ["access_token"] = "secret" }]);
    settings.SaveAccounts([new JsonObject { ["id"] = "current" }]);
    File.WriteAllText(accountsPath, "[");

    var accounts = settings.LoadAccounts();
    Assert.Equal(1, accounts.Count);
    Assert.Equal("backup", accounts[0]["id"]?.GetValue<string>());
    Assert.Equal("secret", accounts[0]["access_token"]?.GetValue<string>());
});

runner.Add("SecureStore ignores malformed protected token", () =>
{
    Assert.Equal("", SecureStore.UnprotectSecret("plain64:not-valid-base64"));
});

runner.Add("INI restores backup after truncated primary", () =>
{
    using var temp = TempDir.Create();
    var path = Path.Combine(temp.Path, "launcher_config.ini");
    AtomicStorage.AtomicWriteText(path, "[UPDATE]\nmanifest_url = backup\n");
    AtomicStorage.AtomicWriteText(path, "[UPDATE]\nmanifest_url = current\n");
    File.WriteAllText(path, "[UPDATE");

    var document = IniDocument.Load(path);
    Assert.Equal("backup", document.Get("UPDATE", "manifest_url"));
    Assert.True(Directory.GetFiles(temp.Path, "launcher_config.ini.corrupt-*").Length == 1);
});

runner.Add("App settings migrate Python update manifest to CSharp manifest", () =>
{
    using var temp = TempDir.Create();
    var configPath = Path.Combine(temp.Path, "launcher_config.ini");
    File.WriteAllText(configPath, $"[UPDATE]\nmanifest_url = {McGo.Core.App.AppConstants.LegacyUpdateManifestUrl}\n");
    var settings = new AppSettings(configPath, Path.Combine(temp.Path, "accounts.json"));
    settings.LoadConfig();
    Assert.Equal(McGo.Core.App.AppConstants.DefaultUpdateManifestUrl, settings.Config.Get("UPDATE", "manifest_url"));
});

runner.Add("App log rotates and redacts secrets", () =>
{
    using var temp = TempDir.Create();
    var writer = new AppLogWriter(temp.Path, maxBytes: 1024, backupCount: 2);
    for (var index = 0; index < 20; index++)
    {
        writer.Write("INFO", "Test", new string('x', 100));
    }

    writer.Write("INFO", "Test", "password=top-secret Bearer abc.def");

    var allText = string.Join("\n", Directory.GetFiles(temp.Path).Select(File.ReadAllText));
    Assert.Contains("password=[REDACTED]", allText);
    Assert.True(!allText.Contains("top-secret", StringComparison.Ordinal));
    Assert.True(!allText.Contains("abc.def", StringComparison.Ordinal));
    Assert.True(File.Exists(Path.Combine(temp.Path, "mcgo.log.1")));
});

runner.Add("Active log remains readable while another stream writes", () =>
{
    using var temp = TempDir.Create();
    var path = Path.Combine(temp.Path, "latest.log");
    using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    using var textWriter = new StreamWriter(writer, Encoding.UTF8, leaveOpen: true);
    textWriter.WriteLine("active log line");
    textWriter.Flush();
    var result = LogFileReader.ReadTailAsync(path).GetAwaiter().GetResult();
    Assert.Contains("active log line", result.Text);
});

runner.Add("Rotated and compressed logs are discovered and read", () =>
{
    using var temp = TempDir.Create();
    var rotated = Path.Combine(temp.Path, "mcgo.log.1");
    File.WriteAllText(rotated, "rotated");
    var compressed = Path.Combine(temp.Path, "latest.log.gz");
    using (var file = File.Create(compressed))
    using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
    using (var writer = new StreamWriter(gzip, Encoding.UTF8))
    {
        writer.Write("compressed log line");
    }

    var files = LogFileReader.Enumerate(temp.Path);
    Assert.True(files.Contains(rotated));
    Assert.True(files.Contains(compressed));
    var result = LogFileReader.ReadTailAsync(compressed).GetAwaiter().GetResult();
    Assert.Contains("compressed log line", result.Text);
});

runner.Add("Large logs return complete lines from the tail", () =>
{
    using var temp = TempDir.Create();
    var path = Path.Combine(temp.Path, "large.log");
    File.WriteAllText(path, "first line\n" + new string('a', 2048) + "\nlast line\n");
    var result = LogFileReader.ReadTailAsync(path, maxBytes: 1024).GetAwaiter().GetResult();
    Assert.True(result.IsTruncated);
    Assert.True(!result.Text.Contains("first line", StringComparison.Ordinal));
    Assert.Contains("last line", result.Text);
});

runner.Add("Global download manager runs queued tasks serially", () =>
{
    var manager = new DownloadTaskManager();
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var order = new List<string>();
    manager.Enqueue("test", "first", async (_, token) =>
    {
        lock (order) order.Add("first-start");
        await releaseFirst.Task.WaitAsync(token);
        lock (order) order.Add("first-end");
        return "first done";
    });
    manager.Enqueue("test", "second", (_, _) =>
    {
        lock (order) order.Add("second-start");
        return Task.FromResult("second done");
    });

    Assert.True(SpinWait.SpinUntil(() => manager.ActiveTitle == "first" && manager.QueuedCount == 1, TimeSpan.FromSeconds(2)));
    lock (order) Assert.False(order.Contains("second-start"));
    Assert.Equal("下载任务 0%", manager.NavigationLabel);
    releaseFirst.SetResult();
    Assert.True(SpinWait.SpinUntil(() => manager.Records.Count == 2 && manager.Records.All(item => item.Status == "completed"), TimeSpan.FromSeconds(2)));
    lock (order) Assert.SequenceEqual(new[] { "first-start", "first-end", "second-start" }, order);
});

runner.Add("Global download manager retries the last failed task", () =>
{
    var manager = new DownloadTaskManager();
    var attempts = 0;
    manager.Enqueue("test", "retry", (_, _) =>
    {
        attempts++;
        return attempts == 1
            ? Task.FromException<string>(new InvalidOperationException("first failure"))
            : Task.FromResult("recovered");
    });

    Assert.True(SpinWait.SpinUntil(() => manager.Records.Single().Status == "failed", TimeSpan.FromSeconds(2)));
    Assert.Equal("下载失败 1", manager.NavigationLabel);
    Assert.True(manager.RetryLastFailed());
    Assert.True(SpinWait.SpinUntil(() => manager.Records.Single().Status == "completed", TimeSpan.FromSeconds(2)));
    Assert.Equal(2, attempts);
});

runner.Add("Global download manager cancels the active task", () =>
{
    var manager = new DownloadTaskManager();
    manager.Enqueue("test", "cancel", async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return "unreachable";
    });

    Assert.True(SpinWait.SpinUntil(() => manager.ActiveTitle == "cancel", TimeSpan.FromSeconds(2)));
    Assert.True(manager.CancelCurrent());
    Assert.True(SpinWait.SpinUntil(
        () => manager.Records.Single().Status == "canceled" && manager.ActiveRecord is null,
        TimeSpan.FromSeconds(2)));
    Assert.False(manager.HasAttentionRequired);
});

runner.Add("Global download manager clears queued tasks and keeps 24 finished records", () =>
{
    var manager = new DownloadTaskManager();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    manager.Enqueue("test", "active", async (_, token) =>
    {
        await release.Task.WaitAsync(token);
        return "done";
    });
    manager.Enqueue("test", "queued-1", (_, _) => Task.FromResult("unexpected"));
    manager.Enqueue("test", "queued-2", (_, _) => Task.FromResult("unexpected"));
    Assert.True(SpinWait.SpinUntil(() => manager.ActiveTitle == "active" && manager.QueuedCount == 2, TimeSpan.FromSeconds(2)));
    Assert.Equal(2, manager.ClearQueue());
    Assert.Equal(0, manager.QueuedCount);
    Assert.Equal(2, manager.Records.Count(item => item.Status == "canceled"));
    release.SetResult();
    Assert.True(SpinWait.SpinUntil(() => manager.ActiveRecord is null, TimeSpan.FromSeconds(2)));

    for (var index = 0; index < 30; index++)
    {
        manager.Enqueue("test", "finished-" + index, (_, _) => Task.FromResult("done"));
    }

    Assert.True(SpinWait.SpinUntil(
        () => manager.ActiveRecord is null && manager.QueuedCount == 0 && manager.Records.All(item => item.Status == "completed"),
        TimeSpan.FromSeconds(3)));
    Assert.Equal(24, manager.Records.Count);
});

runner.Add("Version launch options append quick play", () =>
{
    var settings = new JsonObject
    {
        ["1.20.1"] = new JsonObject
        {
            ["game_args"] = "--fullscreen",
            ["quick_play_singleplayer"] = "My World",
            ["jvm_args"] = "-Ddemo=true",
            ["pre_launch_command"] = "echo before",
            ["min_memory_mb"] = 512,
            ["max_memory_mb"] = 4096,
            ["window_width"] = 1280,
            ["window_height"] = 720,
            ["runtime_directory"] = Path.Combine("custom", "runtime"),
            ["mods_directory"] = Path.Combine("custom", "mods"),
        },
    };
    var options = VersionSettingsService.LaunchOptionsForVersion("game", settings, "1.20.1");
    Assert.SequenceEqual(["--fullscreen", "--quickPlaySingleplayer", "My World"], options.ExtraGameArgs);
    Assert.SequenceEqual(["-Ddemo=true"], options.ExtraJvmArgs);
    Assert.Equal("echo before", options.PreLaunchCommand);
    Assert.Equal(512, options.MinMemoryMb);
    Assert.Equal(4096, options.MaxMemoryMb);
    Assert.Equal(1280, options.WindowWidth);
    Assert.Equal(720, options.WindowHeight);
    Assert.Contains(Path.Combine("custom", "runtime"), options.RuntimeDirectory);
    Assert.Contains(Path.Combine("custom", "mods"), VersionSettingsService.ModsDirectoryForVersion("game", settings, "1.20.1"));
});

runner.Add("Version settings detect categories and favorite labels", () =>
{
    using var temp = TempDir.Create();
    WriteVersion(temp.Path, "1.20.1", new JsonObject
    {
        ["mainClass"] = "net.minecraft.client.main.Main",
        ["libraries"] = new JsonArray(),
    });
    WriteVersion(temp.Path, "fabric-demo", new JsonObject
    {
        ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient",
        ["inheritsFrom"] = "1.20.1",
        ["libraries"] = new JsonArray { new JsonObject { ["name"] = "net.fabricmc:fabric-loader:0.15.0" } },
    });
    var service = new VersionSettingsService();
    var settings = new JsonObject
    {
        ["fabric-demo"] = new JsonObject { ["alias"] = "Fabric Pack", ["favorite"] = true },
    };

    Assert.True(service.VersionMatchesCategory(temp.Path, "1.20.1", "原版"));
    Assert.True(service.VersionMatchesCategory(temp.Path, "fabric-demo", "可安装 Mod"));
    Assert.False(service.VersionMatchesCategory(temp.Path, "fabric-demo", "原版"));
    Assert.Contains("* Fabric Pack [fabric-demo]", VersionSettingsService.VersionDisplayName(settings, "fabric-demo"));
});

runner.Add("Version management deletes only direct version directories", () =>
{
    using var temp = TempDir.Create();
    WriteVersion(temp.Path, "demo", new JsonObject
    {
        ["mainClass"] = "net.minecraft.client.main.Main",
        ["libraries"] = new JsonArray(),
    });
    var result = VersionManagementService.DeleteVersion(temp.Path, "demo");
    Assert.False(Directory.Exists(result.DeletedPath));
    Assert.Throws(() => VersionManagementService.DeleteVersion(temp.Path, ".."));
});

runner.Add("Launcher infers manifest java version", () =>
{
    using var temp = TempDir.Create();
    WriteVersion(temp.Path, "1.20.1", new JsonObject
    {
        ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 },
        ["libraries"] = new JsonArray(),
        ["mainClass"] = "net.minecraft.client.main.Main",
    });
    Assert.Equal(17, new LauncherService().InferRequiredJavaVersion(temp.Path, "1.20.1"));
});

runner.Add("Launcher infers Java baseline by Minecraft generation", () =>
{
    using var temp = TempDir.Create();
    // 1.16.5 官方使用 Java 8：即便清单声明 8，也不能被启发式基线抬到 17。
    WriteVersion(temp.Path, "1.16.5", new JsonObject
    {
        ["javaVersion"] = new JsonObject { ["majorVersion"] = 8 },
        ["libraries"] = new JsonArray(),
        ["mainClass"] = "net.minecraft.client.main.Main",
    });
    WriteVersion(temp.Path, "1.17.1", new JsonObject
    {
        ["libraries"] = new JsonArray(),
        ["mainClass"] = "net.minecraft.client.main.Main",
    });
    WriteVersion(temp.Path, "1.21", new JsonObject
    {
        ["libraries"] = new JsonArray(),
        ["mainClass"] = "net.minecraft.client.main.Main",
    });

    var launcher = new LauncherService();
    Assert.Equal(8, launcher.InferRequiredJavaVersion(temp.Path, "1.16.5"));
    Assert.Equal(17, launcher.InferRequiredJavaVersion(temp.Path, "1.17.1"));
    Assert.Equal(21, launcher.InferRequiredJavaVersion(temp.Path, "1.21"));
});

runner.Add("Launcher ignores loader prefix when inferring Java baseline", () =>
{
    using var temp = TempDir.Create();
    // fabric-loader-0.16.0-1.21.1 的前缀 0.16 不能被当成 MC 版本，否则会误判为 Java 8。
    WriteVersion(temp.Path, "fabric-loader-0.16.0-1.21.1", new JsonObject
    {
        ["libraries"] = new JsonArray(),
        ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient",
    });
    WriteVersion(temp.Path, "1.20.1-forge-47.2.0", new JsonObject
    {
        ["libraries"] = new JsonArray(),
        ["mainClass"] = "cpw.mods.bootstraplauncher.BootstrapLauncher",
    });

    var launcher = new LauncherService();
    Assert.Equal(21, launcher.InferRequiredJavaVersion(temp.Path, "fabric-loader-0.16.0-1.21.1"));
    Assert.Equal(17, launcher.InferRequiredJavaVersion(temp.Path, "1.20.1-forge-47.2.0"));
});

runner.Add("Launcher builds command with runtime and memory", () =>
{
    using var temp = TempDir.Create();
    var libraryPath = Path.Combine(temp.Path, "libraries", "com", "example", "demo", "1.0", "demo-1.0.jar");
    Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
    File.WriteAllBytes(libraryPath, [1, 2, 3]);
    WriteVersion(temp.Path, "1.20.1", new JsonObject
    {
        ["mainClass"] = "net.minecraft.client.main.Main",
        ["assetIndex"] = new JsonObject { ["id"] = "1" },
        ["libraries"] = new JsonArray { new JsonObject { ["name"] = "com.example:demo:1.0" } },
        ["arguments"] = new JsonObject
        {
            ["game"] = new JsonArray("--username", "${auth_player_name}"),
            ["jvm"] = new JsonArray(),
        },
    });
    var command = new LauncherService().BuildLaunchCommand(new LaunchOptions(
        "java",
        "1.20.1",
        temp.Path,
        Username: "Steve",
        RuntimeDirectory: Path.Combine(temp.Path, "runtime"),
        MaxMemoryMb: 3072));
    Assert.Contains("-Xmx3072M", command);
    Assert.Contains("net.minecraft.client.main.Main", command);
    Assert.Contains("Steve", command);
    Assert.Contains(string.Join(Path.PathSeparator, [libraryPath, Path.Combine(temp.Path, "versions", "1.20.1", "1.20.1.jar")]), command);
});

runner.Add("Launch script quotes command and sanitizes filename", () =>
{
    var script = LaunchScriptService.BuildWindowsBatchScript(
        [@"C:\Program Files\Java\bin\java.exe", "-Xmx2G", "-jar", @"C:\Games\Mc Go\client.jar"],
        "echo before");
    Assert.Contains("echo before", script);
    Assert.Contains("\"C:\\Program Files\\Java\\bin\\java.exe\"", script);
    Assert.Contains("\"C:\\Games\\Mc Go\\client.jar\"", script);
    Assert.Equal("bad_name_", LaunchScriptService.SafeScriptName("bad:name?"));
});

runner.Add("Launch preflight reports Java, missing files, and natives", () =>
{
    using var temp = TempDir.Create();
    var clientBytes = Encoding.UTF8.GetBytes("client");
    WriteVersion(temp.Path, "1.20.1", new JsonObject
    {
        ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 },
        ["downloads"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["url"] = "https://files.example/client.jar",
                ["sha1"] = Sha1(clientBytes),
                ["size"] = clientBytes.Length,
            },
        },
        ["libraries"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "org.lwjgl:lwjgl:3.3.1",
                ["natives"] = new JsonObject { ["windows"] = "natives-windows" },
                ["downloads"] = new JsonObject
                {
                    ["classifiers"] = new JsonObject
                    {
                        ["natives-windows"] = new JsonObject
                        {
                            ["path"] = "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar",
                            ["url"] = "https://files.example/lwjgl-native.jar",
                        },
                    },
                },
            },
        },
        ["mainClass"] = "net.minecraft.client.main.Main",
    }, jar: false);

    var result = new LaunchPreflightService(javaMajorVersionProvider: _ => 8)
        .Validate(temp.Path, "1.20.1", "java");
    var text = string.Join("\n", result.Issues.Select(issue => issue.Message));
    Assert.False(result.IsOk);
    Assert.Contains("至少需要 Java 17", text);
    Assert.Contains("缺失 versions", text);
    Assert.Contains("natives 未解压", text);
});

runner.Add("Java runtime download extracts archive and finds executable", () =>
{
    using var temp = TempDir.Create();
    var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
    var archiveBytes = ZipBytes(($"jdk-17/bin/{executable}", Encoding.UTF8.GetBytes("java")));
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["https://files.example/jdk.zip"] = BytesResponse(archiveBytes),
    };
    var result = JavaRuntimeService.DownloadJavaRuntimeAsync(
        17,
        Path.Combine(temp.Path, "java_runtimes"),
        new HttpClient(new RoutingHandler(routes)),
        downloadUrl: "https://files.example/jdk.zip").GetAwaiter().GetResult();

    Assert.False(result.Reused);
    Assert.True(File.Exists(result.JavaPath));
    Assert.Contains(Path.Combine("bin", executable), result.JavaPath);
});

runner.Add("Java version parser handles legacy and modern output", () =>
{
    // 旧式 HotSpot 输出 1.8.0_392，主版本应解析为 8。
    Assert.Equal(8, JavaRuntimeService.ParseJavaMajorVersion("java version \"1.8.0_392\"\nJava(TM) SE Runtime Environment"));
    // 新式输出 17.0.9 / 21，主版本分别为 17 与 21。
    Assert.Equal(17, JavaRuntimeService.ParseJavaMajorVersion("openjdk version \"17.0.9\" 2023-10-17"));
    Assert.Equal(21, JavaRuntimeService.ParseJavaMajorVersion("openjdk version \"21\" 2023-09-19 LTS"));
    // 无法识别的输出返回 0。
    Assert.Equal(0, JavaRuntimeService.ParseJavaMajorVersion(""));
    Assert.Equal(0, JavaRuntimeService.ParseJavaMajorVersion("no version here"));
});

runner.Add("Updater compares semver", () =>
{
    Assert.True(UpdaterService.IsNewerVersion("1.10.0", "1.9.9"));
    Assert.False(UpdaterService.IsNewerVersion("1.0.0", "1.0.0"));
    Assert.True(UpdaterService.IsNewerVersion("1.0.0", "1.0.0-rc1"));
    Assert.False(UpdaterService.IsNewerVersion("1.0.0-rc1", "1.0.0"));
});

runner.Add("Updater command uses encoded PowerShell", () =>
{
    var command = UpdaterService.BuildUpdateApplyCommand(@"C:\Temp\McGo 1.2.0.exe", @"C:\Apps\McGo.exe", 1234, @"C:\Temp\mcgo-update.log");
    var encoded = UpdaterService.PowershellEncodedCommand(command);
    var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
    Assert.Contains("Copy-Item -LiteralPath $Source -Destination $Destination -Force", decoded);
    Assert.Contains(@"McGo 1.2.0.exe", decoded);
});

runner.Add("Installer update ignores same-version executable hash difference", () =>
{
    var currentHashRequested = false;
    var manifest = new JsonObject
    {
        ["version"] = "1.0.11",
        ["url"] = "https://files.example/McGo-Setup.exe",
        ["sha256"] = new string('a', 64),
        ["size"] = 123,
        ["installer"] = true,
    };
    var result = new UpdaterService(new HttpClient(new JsonHandler(manifest)))
        .CheckForUpdateAsync("https://files.example/update-csharp.json", "1.0.11", () =>
        {
            currentHashRequested = true;
            return new string('b', 64);
        })
        .GetAwaiter()
        .GetResult();
    Assert.False(currentHashRequested);
    Assert.False(result["build_changed"]!.GetValue<bool>());
    Assert.False(result["update_available"]!.GetValue<bool>());
});

runner.Add("Newer installer version reports update", () =>
{
    var manifest = new JsonObject
    {
        ["version"] = "1.0.12",
        ["url"] = "https://files.example/McGo-Setup.exe",
        ["sha256"] = new string('a', 64),
        ["size"] = 123,
        ["installer"] = true,
    };
    var result = new UpdaterService(new HttpClient(new JsonHandler(manifest)))
        .CheckForUpdateAsync("https://files.example/update-csharp.json", "1.0.11", () => new string('b', 64))
        .GetAwaiter()
        .GetResult();
    Assert.True(result["update_available"]!.GetValue<bool>());
    Assert.True(UpdaterService.IsInstallerManifest(result));
});

runner.Add("Installer launch command waits for client then starts Setup", () =>
{
    var command = UpdaterService.BuildInstallerLaunchCommand(@"C:\Temp\McGo Setup.exe", 4321, @"C:\Temp\mcgo-update.log");
    Assert.Contains("Wait-Process -Id $ParentPid", command);
    Assert.Contains("Start-Process -FilePath $Installer", command);
    Assert.Contains(@"McGo Setup.exe", command);
});

runner.Add("Legacy manifest without explicit mode cannot replace CSharp client", () =>
{
    var manifest = new JsonObject
    {
        ["version"] = "1.0.12",
        ["url"] = "https://files.example/McGo.exe",
        ["sha256"] = new string('a', 64),
        ["size"] = 123,
    };
    var result = new UpdaterService(new HttpClient(new JsonHandler(manifest)))
        .FetchUpdateManifestAsync("https://files.example/update.json")
        .GetAwaiter()
        .GetResult();
    Assert.False(UpdaterService.IsInstallerManifest(result));
    Assert.False(UpdaterService.IsExplicitLegacyExecutableManifest(result));
});

runner.Add("Update manifest generation round-trips through fetch", () =>
{
    using var temp = TempDir.Create();
    var artifact = Path.Combine(temp.Path, "McGo.exe");
    File.WriteAllBytes(artifact, Encoding.ASCII.GetBytes("MZMcGo release artifact bytes"));
    var expectedSha = UpdaterService.FileSha256(artifact);
    var expectedSize = new FileInfo(artifact).Length;

    var manifest = UpdaterService.BuildUpdateManifest(artifact, "2.1.0", "https://files.example/McGo.exe", installer: false, notes: "  首个发布  ");
    Assert.Equal("2.1.0", manifest["version"]!.GetValue<string>());
    Assert.Equal("https://files.example/McGo.exe", manifest["url"]!.GetValue<string>());
    Assert.Equal(expectedSha, manifest["sha256"]!.GetValue<string>());
    Assert.Equal(expectedSize, manifest["size"]!.GetValue<long>());
    Assert.False(manifest["installer"]!.GetValue<bool>());
    Assert.Equal("首个发布", manifest["notes"]!.GetValue<string>());

    var fetched = new UpdaterService(new HttpClient(new JsonHandler(manifest)))
        .FetchUpdateManifestAsync("https://files.example/update.json")
        .GetAwaiter()
        .GetResult();
    Assert.Equal("2.1.0", fetched["version"]!.GetValue<string>());
    Assert.Equal("https://files.example/McGo.exe", fetched["url"]!.GetValue<string>());
    Assert.Equal(expectedSha, fetched["sha256"]!.GetValue<string>());
    Assert.False(UpdaterService.IsInstallerManifest(fetched));

    var outputPath = Path.Combine(temp.Path, "update.json");
    var written = UpdaterService.WriteUpdateManifest(outputPath, artifact, "2.1.0", "https://files.example/McGo.exe", installer: true, notes: "");
    Assert.True(File.Exists(written));
    var reread = JsonNode.Parse(File.ReadAllText(written))!.AsObject();
    Assert.True(reread["installer"]!.GetValue<bool>());
    Assert.Equal(expectedSha, reread["sha256"]!.GetValue<string>());
});

runner.Add("Update manifest generation rejects invalid input", () =>
{
    using var temp = TempDir.Create();
    var artifact = Path.Combine(temp.Path, "McGo.exe");
    File.WriteAllText(artifact, "payload");
    Assert.Throws(() => UpdaterService.BuildUpdateManifest(artifact, "", "https://files.example/McGo.exe"));
    Assert.Throws(() => UpdaterService.BuildUpdateManifest(artifact, "1.0.0", "ftp://files.example/McGo.exe"));
    Assert.Throws(() => UpdaterService.BuildUpdateManifest(Path.Combine(temp.Path, "missing.exe"), "1.0.0", "https://files.example/McGo.exe"));
});

runner.Add("Updater downloads verified executable", () =>
{
    using var temp = TempDir.Create();
    var payload = Encoding.ASCII.GetBytes("MZMcGo update payload");
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["https://files.example/McGo.exe"] = BytesResponse(payload),
    };
    var manifest = new JsonObject
    {
        ["version"] = "9.9.9",
        ["url"] = "https://files.example/McGo.exe",
        ["sha256"] = Sha256(payload),
        ["size"] = payload.Length,
    };
    var result = new UpdaterService(new HttpClient(new RoutingHandler(routes)))
        .DownloadUpdateAsync(manifest, temp.Path)
        .GetAwaiter()
        .GetResult();

    var path = result["path"]!.GetValue<string>();
    Assert.True(File.Exists(path));
    Assert.Contains("9.9.9", Path.GetFileName(path));
    Assert.Equal(payload.Length, result["bytes"]!.GetValue<long>());
    Assert.Equal(Sha256(payload), result["sha256"]!.GetValue<string>());
});

runner.Add("Downloader rewrites BMCLAPI maven urls", () =>
{
    var rewritten = DownloaderService.RewriteUrl("https://libraries.minecraft.net/com/example/demo.jar", "bmclapi");
    Assert.Contains("https://bmclapi2.bangbang93.com/maven", rewritten);
});

runner.Add("Downloader rewrites all BMCLAPI mirror hosts", () =>
{
    // client.jar / assets index 走 piston-data，是镜像最需要覆盖的大文件。
    Assert.Equal("https://bmclapi2.bangbang93.com/v1/objects/abc/client.jar",
        DownloaderService.RewriteUrl("https://piston-data.mojang.com/v1/objects/abc/client.jar", "bmclapi"));
    Assert.Equal("https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
        DownloaderService.RewriteUrl("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", "bmclapi"));
    Assert.Equal("https://bmclapi2.bangbang93.com/mc/launcher.json",
        DownloaderService.RewriteUrl("https://launchermeta.mojang.com/mc/launcher.json", "bmclapi"));
    Assert.Equal("https://bmclapi2.bangbang93.com/v1/objects/def/server.jar",
        DownloaderService.RewriteUrl("https://launcher.mojang.com/v1/objects/def/server.jar", "bmclapi"));
    Assert.Equal("https://bmclapi2.bangbang93.com/assets/ab/abcdef",
        DownloaderService.RewriteUrl("https://resources.download.minecraft.net/ab/abcdef", "bmclapi"));
    // launchermeta 不应被 launcher.mojang.com 规则误伤。
    Assert.Equal("https://bmclapi2.bangbang93.com/x", DownloaderService.RewriteUrl("https://launchermeta.mojang.com/x", "bmclapi"));
    // 非 bmclapi 源保持原样。
    Assert.Equal("https://piston-data.mojang.com/a", DownloaderService.RewriteUrl("https://piston-data.mojang.com/a", "official"));
});

runner.Add("Downloader repair fills missing files and extracts natives", () =>
{
    using var temp = TempDir.Create();
    var gameDir = Path.Combine(temp.Path, "game");
    var versionId = "1.20.1";
    var clientBytes = Encoding.UTF8.GetBytes("client");
    var libraryBytes = Encoding.UTF8.GetBytes("library");
    var assetBytes = Encoding.UTF8.GetBytes("asset");
    var nativeBytes = ZipBytes(("native.dll", Encoding.UTF8.GetBytes("native")), ("META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("skip")));
    var assetHash = Sha1(assetBytes);
    var assetIndex = new JsonObject
    {
        ["objects"] = new JsonObject
        {
            ["minecraft/sounds/demo.ogg"] = new JsonObject
            {
                ["hash"] = assetHash,
                ["size"] = assetBytes.Length,
            },
        },
    };
    var assetIndexText = assetIndex.ToJsonString();
    var assetIndexBytes = Encoding.UTF8.GetBytes(assetIndexText);
    var versionJson = new JsonObject
    {
        ["id"] = versionId,
        ["downloads"] = new JsonObject
        {
            ["client"] = new JsonObject
            {
                ["url"] = "https://files.example/client.jar",
                ["sha1"] = Sha1(clientBytes),
                ["size"] = clientBytes.Length,
            },
        },
        ["assetIndex"] = new JsonObject
        {
            ["id"] = "demo",
            ["url"] = "https://files.example/index.json",
            ["sha1"] = Sha1(assetIndexBytes),
            ["size"] = assetIndexBytes.Length,
        },
        ["libraries"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "com.example:demo:1.0",
                ["downloads"] = new JsonObject
                {
                    ["artifact"] = new JsonObject
                    {
                        ["path"] = "com/example/demo/1.0/demo-1.0.jar",
                        ["url"] = "https://files.example/demo.jar",
                        ["sha1"] = Sha1(libraryBytes),
                        ["size"] = libraryBytes.Length,
                    },
                },
            },
            new JsonObject
            {
                ["name"] = "org.lwjgl:lwjgl:3.3.1",
                ["natives"] = new JsonObject { ["windows"] = "natives-windows" },
                ["downloads"] = new JsonObject
                {
                    ["classifiers"] = new JsonObject
                    {
                        ["natives-windows"] = new JsonObject
                        {
                            ["path"] = "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar",
                            ["url"] = "https://files.example/lwjgl-native.jar",
                            ["sha1"] = Sha1(nativeBytes),
                            ["size"] = nativeBytes.Length,
                        },
                    },
                },
            },
        },
    };
    WriteVersion(gameDir, versionId, versionJson, jar: false);
    var clientPath = Path.Combine(gameDir, "versions", versionId, $"{versionId}.jar");
    File.WriteAllBytes(clientPath, Encoding.UTF8.GetBytes("corrupt"));
    var libraryPath = Path.Combine(gameDir, "libraries", "com", "example", "demo", "1.0", "demo-1.0.jar");
    Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
    File.WriteAllBytes(libraryPath, libraryBytes);

    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["https://files.example/client.jar"] = BytesResponse(clientBytes),
        ["https://files.example/demo.jar"] = BytesResponse(libraryBytes),
        ["https://files.example/index.json"] = BytesResponse(assetIndexBytes),
        ["https://files.example/lwjgl-native.jar"] = BytesResponse(nativeBytes),
        [$"/{assetHash[..2]}/{assetHash}"] = BytesResponse(assetBytes),
    };
    var service = new DownloaderService(new HttpClient(new RoutingHandler(routes)));
    var summary = service.RepairGameFilesAsync(versionJson, gameDir, versionId, "official").GetAwaiter().GetResult();

    Assert.True(summary.TotalJobs >= 5);
    Assert.True(summary.ReusedJobs >= 1);
    Assert.Equal(Sha1(clientBytes), McGo.Core.Utilities.FileUtilities.Sha1File(clientPath));
    Assert.True(File.Exists(Path.Combine(gameDir, "assets", "objects", assetHash[..2], assetHash)));
    Assert.Equal("native", File.ReadAllText(Path.Combine(summary.NativesDirectory, "native.dll")));
    Assert.Equal(0, DownloaderService.CollectMissingGameFiles(versionJson, gameDir, versionId).Count);
});

runner.Add("Downloader retries transient failures before succeeding", () =>
{
    using var temp = TempDir.Create();
    var payload = Encoding.UTF8.GetBytes("payload-bytes");
    var targetPath = Path.Combine(temp.Path, "sub", "demo.bin");
    // 前两次返回 503，第三次成功；下载应通过重试完成。
    var handler = new FlakyHandler(failuresBeforeSuccess: 2, BytesResponse(payload));
    var service = new DownloaderService(new HttpClient(handler));
    var job = new DownloadJob("https://files.example/demo.bin", targetPath, Sha1(payload), payload.Length, "asset");
    var summary = service.DownloadJobsAsync([job]).GetAwaiter().GetResult();

    Assert.Equal(1, summary.CompletedJobs);
    Assert.Equal(3, handler.Attempts);
    Assert.True(File.Exists(targetPath));
    Assert.Equal(Sha1(payload), McGo.Core.Utilities.FileUtilities.Sha1File(targetPath));
    Assert.False(File.Exists(targetPath + ".download"));
});

runner.Add("Downloader gives up after exhausting retries", () =>
{
    using var temp = TempDir.Create();
    var payload = Encoding.UTF8.GetBytes("never-served");
    var targetPath = Path.Combine(temp.Path, "demo.bin");
    // 一直返回 503，重试耗尽后应抛出且不留下临时文件。
    var handler = new FlakyHandler(failuresBeforeSuccess: 99, BytesResponse(payload));
    var service = new DownloaderService(new HttpClient(handler));
    var job = new DownloadJob("https://files.example/demo.bin", targetPath, Sha1(payload), payload.Length, "asset");
    Assert.Throws(() => service.DownloadJobsAsync([job]).GetAwaiter().GetResult());
    Assert.Equal(3, handler.Attempts);
    Assert.False(File.Exists(targetPath));
    Assert.False(File.Exists(targetPath + ".download"));
});

runner.Add("Capsule file filter excludes sensitive files", () =>
{
    Assert.True(CapsuleService.ShouldIncludeCapsuleFile("mods/demo.jar"));
    Assert.False(CapsuleService.ShouldIncludeCapsuleFile("logs/latest.log"));
    Assert.False(CapsuleService.ShouldIncludeCapsuleFile("config/accounts.json"));
});

runner.Add("Capsule export/import roundtrip manifest", () =>
{
    using var temp = TempDir.Create();
    var gameDir = Path.Combine(temp.Path, "game");
    var versionId = "1.20.1";
    WriteVersion(gameDir, versionId, new JsonObject { ["mainClass"] = "net.minecraft.client.main.Main", ["libraries"] = new JsonArray() });
    var modPath = Path.Combine(gameDir, "mods", "demo.jar");
    Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
    File.WriteAllBytes(modPath, [1, 2, 3]);
    var settings = new JsonObject();
    var capsulePath = Path.Combine(temp.Path, "demo.mcgo");
    var service = new CapsuleService();
    var p2p = new JsonObject
    {
        ["relay_host"] = "relay.example",
        ["relay_port"] = 10721,
        ["room"] = "ROOM1234",
    };
    var exported = service.ExportCapsule(gameDir, settings, versionId, capsulePath, p2p);
    Assert.Equal(1, exported.FileCount);
    var manifest = CapsuleService.ReadCapsuleManifest(capsulePath);
    Assert.Equal("mcgo.capsule", manifest["schema"]?.GetValue<string>());
    Assert.Equal("ROOM1234", manifest["p2p"]?["room"]?.GetValue<string>());

    var importGameDir = Path.Combine(temp.Path, "imported-game");
    var importedSettings = new JsonObject();
    var imported = service.ImportCapsule(capsulePath, importGameDir, importedSettings);
    Assert.Equal(versionId, imported.Version);
    Assert.Equal("ROOM1234", imported.P2P["room"]?.GetValue<string>());
    Assert.Equal(1, imported.Files.Count);
    Assert.Equal(1, imported.MissingFiles.Count);
    Assert.True(File.Exists(Path.Combine(importGameDir, "versions", imported.Version, $"{imported.Version}.json")));
    Assert.True(importedSettings[imported.Version]?["use_isolated_directory"]?.GetValue<bool>() == true);
});

runner.Add("STUN XOR-MAPPED-ADDRESS parses endpoint", () =>
{
    var tx = Enumerable.Range(1, 12).Select(i => (byte)i).ToArray();
    var data = new byte[32];
    data[0] = 0x01;
    data[1] = 0x01;
    data[3] = 0x0c;
    data[4] = 0x21;
    data[5] = 0x12;
    data[6] = 0xA4;
    data[7] = 0x42;
    tx.CopyTo(data.AsSpan(8));
    data[21] = 0x20;
    data[23] = 0x08;
    data[25] = 0x01;
    var port = 25565 ^ 0x2112;
    data[26] = (byte)(port >> 8);
    data[27] = (byte)port;
    var cookie = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
    var ip = IPAddress.Parse("1.2.3.4").GetAddressBytes();
    for (var i = 0; i < 4; i++) data[28 + i] = (byte)(ip[i] ^ cookie[i]);
    var endpoint = NatUtils.ParseStunBindingResponse(data, tx);
    Assert.Equal("1.2.3.4", endpoint?.Address.ToString());
    Assert.Equal(25565, endpoint?.Port);
});

runner.Add("NAT detection queries STUN server and reports mapped endpoint", () =>
{
    RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        using var server = new FakeStunServer(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 41234));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await NatUtils.DetectNatTypeAsync([new StunServer("127.0.0.1", server.Port)], TimeSpan.FromSeconds(2), cts.Token);
        Assert.Equal("unknown", result.Quality);
        Assert.Equal("203.0.113.9", result.Samples[0].Mapped.Address.ToString());
        Assert.Equal(41234, result.Samples[0].Mapped.Port);
        Assert.Contains("公网映射", result.Details);
    }
});

runner.Add("NAT detection continues after a STUN timeout", () =>
{
    RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        using var silentServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var silentPort = ((IPEndPoint)silentServer.Client.LocalEndPoint!).Port;
        using var respondingServer = new FakeStunServer(new IPEndPoint(IPAddress.Parse("203.0.113.10"), 42345));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await NatUtils.DetectNatTypeAsync(
            [new StunServer("127.0.0.1", silentPort), new StunServer("127.0.0.1", respondingServer.Port)],
            TimeSpan.FromMilliseconds(150),
            cts.Token);
        Assert.Equal(1, result.Samples.Count);
        Assert.Equal("203.0.113.10", result.Samples[0].Mapped.Address.ToString());
        Assert.Contains($"127.0.0.1:{silentPort}", result.Details);
    }
});

runner.Add("External auth normalizes and validates server urls", () =>
{
    Assert.Equal("https://example.com/api/yggdrasil", ExternalAuthService.NormalizeAuthServer("example.com/api/yggdrasil/authserver"));
    Assert.Equal("http://localhost:8080", ExternalAuthService.NormalizeAuthServer("http://localhost:8080"));
    Assert.Throws(() => ExternalAuthService.NormalizeAuthServer("http://example.com"));
});

runner.Add("External auth downloads authlib injector and verifies sha256", () =>
{
    using var temp = TempDir.Create();
    var jarBytes = Encoding.UTF8.GetBytes("authlib");
    var sha256 = Sha256(jarBytes);
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["/artifact/latest.json"] = JsonResponse(new JsonObject
        {
            ["version"] = "1.2.3",
            ["download_url"] = "https://files.example/authlib.jar",
            ["fileName"] = "authlib-injector.jar",
            ["checksums"] = new JsonObject { ["sha256"] = sha256 },
        }),
        ["https://files.example/authlib.jar"] = BytesResponse(jarBytes),
    };
    var service = new ExternalAuthService(new HttpClient(new RoutingHandler(routes)));
    var result = service.DownloadAuthlibInjectorAsync(temp.Path).GetAwaiter().GetResult();
    Assert.True(File.Exists(result.Path));
    Assert.Equal("authlib", File.ReadAllText(result.Path));
});

runner.Add("External auth refresh updates token and profile fields", () =>
{
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["/authserver/refresh"] = JsonResponse(new JsonObject
        {
            ["accessToken"] = "new-token",
            ["clientToken"] = "client",
            ["selectedProfile"] = new JsonObject
            {
                ["id"] = "uuid2",
                ["name"] = "Alex",
            },
        }),
    };
    var service = new ExternalAuthService(new HttpClient(new RoutingHandler(routes)));
    var refreshed = service.RefreshExternalAccountAsync(new JsonObject
    {
        ["type"] = "external",
        ["auth_server"] = "http://localhost",
        ["access_token"] = "old-token",
        ["client_token"] = "client",
        ["uuid"] = "uuid1",
        ["username"] = "Steve",
    }).GetAwaiter().GetResult();
    Assert.Equal("new-token", refreshed["access_token"]?.GetValue<string>());
    Assert.Equal("uuid2", refreshed["uuid"]?.GetValue<string>());
    Assert.Equal("Alex", refreshed["username"]?.GetValue<string>());
});

runner.Add("Authlib injector args require external account and existing jar", () =>
{
    using var temp = TempDir.Create();
    var jar = Path.Combine(temp.Path, "authlib-injector.jar");
    File.WriteAllBytes(jar, [0]);
    var args = ExternalAuthService.AuthlibInjectorArgs(new JsonObject
    {
        ["type"] = "external",
        ["authlib_injector_path"] = jar,
        ["auth_server"] = "https://auth.example/yggdrasil",
    });
    Assert.Contains($"-javaagent:{jar}=https://auth.example/yggdrasil", args);
});

runner.Add("External auth surfaces server errorMessage on failure", () =>
{
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["/authserver/authenticate"] = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(new JsonObject { ["errorMessage"] = "无效的凭证" }.ToJsonString(), Encoding.UTF8, "application/json"),
        },
    };
    var service = new ExternalAuthService(new HttpClient(new RoutingHandler(routes)));
    var error = Assert.Throws<InvalidOperationException>(() =>
        service.AuthenticateExternalAccountAsync("https://auth.example", "steve", "pw").GetAwaiter().GetResult());
    Assert.Contains("无效的凭证", error.Message);
});

runner.Add("Install services rewrite mirrors and build OptiFine ids", () =>
{
    Assert.Equal("bmclapi", InstallServices.FallbackMirrorSource("official"));
    Assert.Equal("https://bmclapi2.bangbang93.com/maven/com/example/demo.jar", InstallServices.RewriteDownloadUrlForMirror("https://libraries.minecraft.net/com/example/demo.jar", "bmclapi"));
    var selected = new JsonObject { ["filename"] = "preview_OptiFine_1.20.1_HD_U_I6_pre6.jar", ["type"] = "HD_U", ["patch"] = "I6_pre6" };
    Assert.Equal("1.20.1-OptiFine_HD_U_I6_pre6", InstallServices.BuildOptifineVersionId("1.20.1", selected));
    Assert.Equal("1.20.1_HD_U_I6_pre6", InstallServices.BuildOptifineLibraryVersion(selected));
});

runner.Add("Installer engine switches loader versions to base Minecraft", () =>
{
    using var temp = TempDir.Create();
    WriteVersion(temp.Path, "1.20.1", new JsonObject { ["type"] = "release" }, jar: false);
    WriteVersion(temp.Path, "fabric-loader-0.16.0-1.20.1", new JsonObject { ["inheritsFrom"] = "1.20.1", ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient" }, jar: false);
    var statuses = new List<string>();
    var engine = new InstallerEngine("fabric-loader-0.16.0-1.20.1", "official", temp.Path, "", statuses.Add);
    var target = engine.UseBaseMinecraftVersionForLoader("Fabric");
    Assert.Equal("1.20.1", target);
    Assert.True(statuses.Any(status => status.Contains("自动切换", StringComparison.Ordinal)));
});

runner.Add("Modpack export writes Modrinth index and skips noisy files", () =>
{
    Assert.True(ModpackService.ShouldSkipPackFile("logs/latest.log"));
    Assert.False(ModpackService.ShouldSkipPackFile("mods/demo.jar"));
    using var temp = TempDir.Create();
    var gameDir = Path.Combine(temp.Path, "game");
    WriteVersion(gameDir, "1.20.1", new JsonObject { ["type"] = "release", ["libraries"] = new JsonArray(), ["mainClass"] = "net.minecraft.client.main.Main" });
    Directory.CreateDirectory(Path.Combine(gameDir, "mods"));
    File.WriteAllBytes(Path.Combine(gameDir, "mods", "demo.jar"), [1, 2, 3]);
    Directory.CreateDirectory(Path.Combine(gameDir, "logs"));
    File.WriteAllText(Path.Combine(gameDir, "logs", "latest.log"), "noise");
    var target = Path.Combine(temp.Path, "pack.mrpack");
    var added = new ModpackService().ExportModpack(gameDir, new JsonObject(), "1.20.1", target);
    Assert.True(added >= 1);
    using var archive = System.IO.Compression.ZipFile.OpenRead(target);
    Assert.True(archive.GetEntry("modrinth.index.json") is not null);
    Assert.True(archive.GetEntry("overrides/mods/demo.jar") is not null);
    Assert.True(archive.GetEntry("overrides/logs/latest.log") is null);
});

runner.Add("Modpack import reads mrpack overrides and downloads declared files", () =>
{
    using var temp = TempDir.Create();
    var gameDir = Path.Combine(temp.Path, "game");
    Directory.CreateDirectory(gameDir);
    var packPath = Path.Combine(temp.Path, "demo.mrpack");
    using (var archive = ZipFile.Open(packPath, ZipArchiveMode.Create))
    {
        var indexEntry = archive.CreateEntry("modrinth.index.json");
        using (var writer = new StreamWriter(indexEntry.Open()))
        {
            writer.Write(new JsonObject
            {
                ["formatVersion"] = 1,
                ["game"] = "minecraft",
                ["name"] = "Demo Pack",
                ["dependencies"] = new JsonObject
                {
                    ["minecraft"] = "1.20.1",
                    ["fabric-loader"] = "0.15.0",
                },
                ["files"] = new JsonArray(new JsonObject
                {
                    ["path"] = "mods/demo.jar",
                    ["downloads"] = new JsonArray("https://files.example/demo.jar"),
                }),
            }.ToJsonString());
        }

        var overrideEntry = archive.CreateEntry("overrides/config/demo.txt");
        using var overrideWriter = new StreamWriter(overrideEntry.Open());
        overrideWriter.Write("config");
    }

    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["https://files.example/demo.jar"] = BytesResponse([1, 2, 3]),
    };
    var settings = new JsonObject();
    var service = new ModpackService(httpClient: new HttpClient(new RoutingHandler(routes)));
    var result = service.ImportModpackAsync(packPath, gameDir, settings).GetAwaiter().GetResult();
    Assert.Equal("Demo Pack", result.Name);
    Assert.Equal("1.20.1", result.Minecraft);
    Assert.Equal("fabric-loader", result.Loader);
    Assert.Equal(1, result.OverridesCopied);
    Assert.Equal(1, result.FilesDownloaded);
    Assert.Equal(0, result.MissingFiles);
    Assert.True(File.Exists(Path.Combine(gameDir, "versions", result.Version, "config", "demo.txt")));
    Assert.True(File.Exists(Path.Combine(gameDir, "versions", result.Version, "mods", "demo.jar")));
    Assert.True(File.Exists(Path.Combine(gameDir, "versions", result.Version, $"{result.Version}.json")));
    Assert.True(settings[result.Version]?["use_isolated_directory"]?.GetValue<bool>() == true);
});

runner.Add("Modpack import preview reports overwrite conflicts", () =>
{
    using var temp = TempDir.Create();
    var gameDir = temp.Path;
    var runtimeDir = Path.Combine(gameDir, "versions", "CoolPack");
    Directory.CreateDirectory(Path.Combine(runtimeDir, "config"));
    File.WriteAllText(Path.Combine(runtimeDir, "config", "demo.toml"), "old");
    var packPath = Path.Combine(temp.Path, "pack.mrpack");
    using (var archive = ZipFile.Open(packPath, ZipArchiveMode.Create))
    {
        var index = archive.CreateEntry("modrinth.index.json");
        using (var writer = new StreamWriter(index.Open()))
        {
            writer.Write(new JsonObject
            {
                ["name"] = "CoolPack",
                ["dependencies"] = new JsonObject { ["minecraft"] = "1.20.1" },
                ["files"] = new JsonArray(),
            }.ToJsonString());
        }

        var config = archive.CreateEntry("overrides/config/demo.toml");
        using var configWriter = new StreamWriter(config.Open());
        configWriter.Write("new");
    }

    var service = new ModpackService();
    var preview = service.PreviewImportModpack(packPath, gameDir, [], "", true);
    Assert.Equal("CoolPack", preview.Version);
    Assert.True(preview.VersionExists);
    Assert.Contains("config/demo.toml", preview.Conflicts);
});

runner.Add("Crash log analysis identifies causes, advice, and key lines", () =>
{
    using var temp = TempDir.Create();
    var logsDir = Path.Combine(temp.Path, "logs");
    Directory.CreateDirectory(logsDir);
    File.WriteAllText(Path.Combine(logsDir, "latest.log"),
        "[12:00:00] [main/INFO]: Starting game\n" +
        "Exception in thread \"main\" java.lang.OutOfMemoryError: Java heap space\n" +
        "\tat net.minecraft.client.main.Main.main(Main.java:1)\n" +
        "Caused by: java.lang.OutOfMemoryError: Java heap space\n");

    var report = ModpackService.AnalyzeCrashLogs(temp.Path, "1.20.1", "");
    Assert.Contains("分析文件：1 个", report);
    Assert.Contains("内存不足", report);
    Assert.Contains("建议处理：", report);
    Assert.Contains("最大内存", report);
    Assert.Contains("关键行：", report);
    Assert.Contains("java.lang.OutOfMemoryError", report);
});

runner.Add("Crash log analysis reports unknown crash gracefully", () =>
{
    using var temp = TempDir.Create();
    var crashDir = Path.Combine(temp.Path, "crash-reports");
    Directory.CreateDirectory(crashDir);
    File.WriteAllText(Path.Combine(crashDir, "crash-2026.txt"),
        "Description: Rendering screen overlay\nOnly ordinary log content here\n");

    var report = ModpackService.AnalyzeCrashLogs(temp.Path, "1.20.1", "");
    Assert.Contains("未匹配到常见崩溃类型", report);
    Assert.Contains("Description: Rendering screen overlay", report);
});

runner.Add("Crash log analysis handles no logs", () =>
{
    using var temp = TempDir.Create();
    Assert.Equal("未找到 latest.log 或 crash-reports。", ModpackService.AnalyzeCrashLogs(temp.Path, "1.20.1", ""));
});

runner.Add("Resource market dependency plan finds missing required dependencies", () =>
{
    using var temp = TempDir.Create();
    var modsDir = Path.Combine(temp.Path, "mods");
    Directory.CreateDirectory(modsDir);
    var sodiumHash = WriteMod(Path.Combine(modsDir, "sodium.jar"), "sodium");
    var payload = new JsonObject
    {
        [sodiumHash] = new JsonObject
        {
            ["project_id"] = "sodium",
            ["name"] = "Sodium",
            ["dependencies"] = new JsonArray
            {
                new JsonObject { ["dependency_type"] = "required", ["project_id"] = "fabric-api" },
                new JsonObject { ["dependency_type"] = "optional", ["project_id"] = "modmenu" },
            },
        },
    };
    var service = new ResourceMarketService(new HttpClient(new JsonHandler(payload)));
    var plan = service.ResolveModrinthDependencyPlanAsync(modsDir).GetAwaiter().GetResult();
    Assert.SequenceEqual(["sodium"], plan.InstalledProjectIds);
    Assert.Equal("fabric-api", plan.MissingDependencies[0].ProjectId);
    Assert.SequenceEqual(["Sodium"], plan.MissingDependencies[0].RequiredBy);
});

runner.Add("Resource market applies Modrinth API and CDN bases", () =>
{
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["/proxy/search?query=sodium&limit=25&index=relevance&facets=%5B%5B%22project_type%3Amod%22%5D%5D"] = JsonResponse(new JsonObject
        {
            ["hits"] = new JsonArray(new JsonObject { ["project_id"] = "sodium" }),
        }),
    };
    var service = new ResourceMarketService(new HttpClient(new RoutingHandler(routes)))
    {
        ModrinthApiBase = "https://api.example/proxy",
        ModrinthCdnBase = "https://cdn.example/modrinth",
    };

    var hits = service.SearchModrinthResourcesAsync("sodium", "mod").GetAwaiter().GetResult();
    Assert.Equal("sodium", hits[0]?["project_id"]?.GetValue<string>());
    Assert.Equal("https://cdn.example/modrinth/data/file.jar", service.RewriteModrinthDownloadUrl("https://cdn.modrinth.com/data/file.jar"));
    Assert.Equal("https://files.example/file.jar", service.RewriteModrinthDownloadUrl("https://files.example/file.jar"));
});

runner.Add("Resource market normalizes plural type and keeps loader facet", () =>
{
    Assert.Equal("mod", ResourceMarketService.NormalizeResourceType("mods"));
    Assert.Equal("resourcepack", ResourceMarketService.NormalizeResourceType("resourcepacks"));
    Assert.Equal("shader", ResourceMarketService.NormalizeResourceType("shaderpacks"));
    Assert.Equal("datapack", ResourceMarketService.NormalizeResourceType("datapacks"));

    // 复数 "mods" 必须归一为 Modrinth 规范 project_type:mod，同时仍应用 categories:fabric 加载器 facet。
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["/v2/search?query=sodium&limit=25&index=relevance&facets=%5B%5B%22project_type%3Amod%22%5D%2C%5B%22categories%3Afabric%22%5D%5D"] = JsonResponse(new JsonObject
        {
            ["hits"] = new JsonArray(new JsonObject { ["project_id"] = "sodium" }),
        }),
    };
    var service = new ResourceMarketService(new HttpClient(new RoutingHandler(routes)));
    var hits = service.SearchModrinthResourcesStrictAsync("sodium", "mods", "", "fabric").GetAwaiter().GetResult();
    Assert.Equal("sodium", hits[0]?["project_id"]?.GetValue<string>());
});

runner.Add("Resource market loads Modrinth detail and compatibility", () =>
{
    var screenshotBytes = Encoding.UTF8.GetBytes("fake image");
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["/v2/project/sodium"] = JsonResponse(new JsonObject
        {
            ["title"] = "Sodium",
            ["slug"] = "sodium",
            ["project_type"] = "mod",
            ["description"] = "Fast renderer",
            ["downloads"] = 100,
            ["followers"] = 10,
            ["gallery"] = new JsonArray(new JsonObject { ["url"] = "https://cdn.example/sodium.jpg" }),
        }),
        ["/v2/project/sodium/version?game_versions=%5B%221.20.1%22%5D&loaders=%5B%22fabric%22%5D"] = JsonResponse(new JsonArray(new JsonObject
        {
            ["version_number"] = "mc1.20.1",
            ["version_type"] = "release",
            ["game_versions"] = new JsonArray("1.20.1"),
            ["loaders"] = new JsonArray("fabric"),
            ["dependencies"] = new JsonArray(new JsonObject { ["dependency_type"] = "required", ["project_id"] = "fabric-api" }),
        })),
        ["/sodium.jpg"] = BytesResponse(screenshotBytes),
    };
    var service = new ResourceMarketService(new HttpClient(new RoutingHandler(routes))) { ModrinthApiBase = "https://api.example/v2" };
    var compatibility = service.CheckModrinthCompatibilityAsync("sodium", "mod", "1.20.1", "fabric").GetAwaiter().GetResult();
    if (!compatibility.Compatible)
    {
        throw new InvalidOperationException("Expected compatible: " + compatibility.Error);
    }
    Assert.Equal("mc1.20.1", compatibility.VersionNumber);

    var detail = service.GetModrinthResourceDetailAsync("sodium", "mod", "1.20.1", "fabric").GetAwaiter().GetResult();
    Assert.Equal("Sodium", detail.Title);
    Assert.Equal("fabric-api", detail.Dependencies[0].ProjectId);
    Assert.Equal("mc1.20.1", detail.Versions[0].Name);
    Assert.True(File.Exists(detail.Screenshots[0].LocalPath));
});

runner.Add("Local resource management lists, analyzes, and toggles files", () =>
{
    using var temp = TempDir.Create();
    var modsDir = Path.Combine(temp.Path, "mods");
    Directory.CreateDirectory(modsDir);
    var modPath = Path.Combine(modsDir, "demo-fabric-mod.jar");
    using (var archive = ZipFile.Open(modPath, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("fabric.mod.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(new JsonObject
        {
            ["id"] = "demo",
            ["depends"] = new JsonObject
            {
                ["fabricloader"] = ">=0.15.0",
                ["fabric-api"] = "*",
            },
        }.ToJsonString());
    }

    File.WriteAllText(Path.Combine(modsDir, "old.jar.disabled"), "disabled");
    File.WriteAllText(Path.Combine(modsDir, "notes.txt"), "ignored");

    var resources = ResourceMarketService.ListLocalResources("", "mod", modsDir);
    Assert.Equal(2, resources.Count);
    Assert.True(resources.Any(item => item.Title == "demo-fabric-mod.jar"));
    Assert.True(resources.Any(item => item.Status == "禁用"));

    var detail = ResourceMarketService.GetLocalResourceDetail(modPath);
    Assert.Contains("可能缺少前置：fabric-api", string.Join("\n", detail.Hints));
    Assert.Equal("启用", detail.Status);

    var disabledPath = ResourceMarketService.SetLocalResourceEnabled(modPath, enabled: false);
    Assert.True(disabledPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
    Assert.True(File.Exists(disabledPath));
    var enabledPath = ResourceMarketService.SetLocalResourceEnabled(disabledPath, enabled: true);
    Assert.Equal(modPath, enabledPath);
    Assert.True(File.Exists(enabledPath));

    ResourceMarketService.DeleteLocalResource(enabledPath);
    Assert.True(!File.Exists(enabledPath));
});

runner.Add("OAuth callback server captures authorization code", () =>
{
    var port = FreePort();
    var callbackUri = $"http://localhost:{port}/login/callback";
    var server = new OAuthCallbackServer();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var waitTask = server.WaitForCodeAsync(callbackUri, TimeSpan.FromSeconds(10), timeout.Token);
    using var client = new HttpClient();
    var response = client.GetAsync(callbackUri + "?code=abc123", timeout.Token).GetAwaiter().GetResult();
    Assert.True(response.IsSuccessStatusCode);
    var result = waitTask.GetAwaiter().GetResult();
    Assert.Equal("abc123", result.Code);
});

runner.Add("P2P bidirectional copy carries bytes", () =>
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var leftClient = new TcpClient();
    using var rightClient = new TcpClient();
    var leftConnect = leftClient.ConnectAsync(IPAddress.Loopback, port);
    using var leftServer = listener.AcceptTcpClient();
    leftConnect.GetAwaiter().GetResult();
    var rightConnect = rightClient.ConnectAsync(IPAddress.Loopback, port);
    using var rightServer = listener.AcceptTcpClient();
    rightConnect.GetAwaiter().GetResult();
    listener.Stop();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var copy = TcpRelayTunnel.CopyBidirectionalAsync(leftServer.GetStream(), rightServer.GetStream(), cts.Token);
    var payload = Encoding.UTF8.GetBytes("hello");
    leftClient.GetStream().Write(payload);
    var buffer = new byte[5];
    ReadExact(rightClient.GetStream(), buffer);
    Assert.Equal("hello", Encoding.UTF8.GetString(buffer));
    rightClient.Close();
    leftClient.Close();
    copy.GetAwaiter().GetResult();
});

runner.Add("UDP frame pack and unpack preserves header fields", () =>
{
    var token = Encoding.ASCII.GetBytes("12345678");
    var frame = new UdpFrame(UdpFrameKind.Data, 42, token, Seq: 7, Ack: 3, Payload: Encoding.UTF8.GetBytes("payload"));
    var unpacked = UdpFrame.Unpack(frame.Pack());
    Assert.Equal(UdpFrameKind.Data, unpacked!.Kind);
    Assert.Equal((ushort)42, unpacked.StreamId);
    Assert.Equal((uint)7, unpacked.Seq);
    Assert.Equal((uint)3, unpacked.Ack);
    Assert.Equal("payload", Encoding.UTF8.GetString(unpacked.Payload!));
    Assert.True(UdpFrame.Unpack([1, 2, 3]) is null);
});

runner.Add("UDP reliable stream handshakes, sends chunks, and closes", () =>
{
    var pair = UdpTestPair.Create();
    Assert.True(pair.Guest.ConnectAsync().GetAwaiter().GetResult());
    var payload = Encoding.UTF8.GetBytes(new string('x', UdpFrame.PayloadSize + 25));
    pair.Guest.WriteAsync(payload).GetAwaiter().GetResult();
    var first = pair.Host.ReadAsync().GetAwaiter().GetResult();
    var second = pair.Host.ReadAsync().GetAwaiter().GetResult();
    Assert.Equal(UdpFrame.PayloadSize, first.Length);
    Assert.Equal(25, second.Length);
    pair.Guest.CloseAsync().GetAwaiter().GetResult();
    var fin = pair.Host.ReadAsync().GetAwaiter().GetResult();
    Assert.Equal(0, fin.Length);
});

runner.Add("UDP reliable stream retransmits dropped data frame", () =>
{
    var pair = UdpTestPair.Create();
    Assert.True(pair.Guest.ConnectAsync().GetAwaiter().GetResult());
    pair.GuestTransport.DropNext(frame => frame.Kind == UdpFrameKind.Data && frame.Seq == 1);
    pair.Guest.WriteAsync(Encoding.UTF8.GetBytes("retry")).GetAwaiter().GetResult();
    var received = pair.Host.ReadAsync().GetAwaiter().GetResult();
    Assert.Equal("retry", Encoding.UTF8.GetString(received));
    Assert.True(pair.GuestTransport.SentFrames.Count(frame => frame.Kind == UdpFrameKind.Data && frame.Seq == 1) >= 2);
});

runner.Add("P2P tunnel bridges local TCP over UDP peer path", () =>
{
    RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var relay = MiniUdpRegistrationRelay.Start();
        var minecraftListener = new TcpListener(IPAddress.Loopback, 0);
        minecraftListener.Start();
        var minecraftPort = ((IPEndPoint)minecraftListener.LocalEndpoint).Port;
        var minecraftTask = Task.Run(async () =>
        {
            using var minecraft = await minecraftListener.AcceptTcpClientAsync(timeout.Token);
            var buffer = new byte[4];
            await ReadExactAsync(minecraft.GetStream(), buffer, timeout.Token);
            Assert.Equal("ping", Encoding.UTF8.GetString(buffer));
            await minecraft.GetStream().WriteAsync(Encoding.UTF8.GetBytes("pong"), timeout.Token);
            minecraftListener.Stop();
        }, timeout.Token);

        var tunnel = new P2PTunnel();
        var room = P2PUtilities.GenerateRoomCode();
        var joinPort = FreePort();
        var hostConfig = new P2PTunnelConfig("127.0.0.1", relay.Port, room, "secret", joinPort, minecraftPort);
        var guestConfig = hostConfig;
        var statuses = new List<string>();
        void Status(string message)
        {
            lock (statuses)
            {
                statuses.Add(message);
            }
        }

        var hostTask = tunnel.RunHostAsync(hostConfig, udpEnabled: true, "127.0.0.1", Status, timeout.Token);
        var guestTask = tunnel.RunGuestAsync(guestConfig, udpEnabled: true, Status, timeout.Token);

        using var client = await ConnectWithRetryAsync(IPAddress.Loopback, joinPort, timeout.Token);
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("ping"), timeout.Token);
        var response = new byte[4];
        await ReadExactAsync(client.GetStream(), response, timeout.Token);
        Assert.Equal("pong", Encoding.UTF8.GetString(response));

        await minecraftTask;
        await timeout.CancelAsync();
        await Task.WhenAll(IgnoreCancellationAsync(hostTask), IgnoreCancellationAsync(guestTask));
        string[] statusSnapshot;
        lock (statuses)
        {
            statusSnapshot = [.. statuses];
        }

        Assert.True(statusSnapshot.Any(message => message.Contains("UDP 打洞")));
    }
});

runner.Add("Install services parse Forge promos and NeoForge/OptiFine helpers", () =>
{
    var promos = new JsonObject
    {
        ["1.20.1-recommended"] = "47.2.0",
        ["1.20.1-latest"] = "47.2.20",
    };
    var service = new InstallServices(new HttpClient(new JsonHandler(new JsonObject { ["promos"] = promos })));
    var version = service.GetForgeVersionForMcAsync("1.20.1", "official").GetAwaiter().GetResult();
    Assert.Equal("47.2.0", version);
    var versions = service.GetForgeVersionsForMcAsync("1.20.1", "official").GetAwaiter().GetResult();
    Assert.SequenceEqual(["47.2.0", "47.2.20"], versions);
    var selected = InstallServices.SelectOptifineCandidate([
        new JsonObject { ["filename"] = "preview_OptiFine_1.20.1_HD_U_I6_pre6.jar" },
        new JsonObject { ["filename"] = "OptiFine_1.20.1_HD_U_I5.jar" },
    ]);
    Assert.Equal("OptiFine_1.20.1_HD_U_I5.jar", selected?["filename"]?.GetValue<string>());
});

runner.Add("Resource market installs Modrinth resource with required dependency", () =>
{
    using var temp = TempDir.Create();
    var depBytes = Encoding.UTF8.GetBytes("dep");
    var mainBytes = Encoding.UTF8.GetBytes("main");
    var depSha1 = Sha1(depBytes);
    var mainSha1 = Sha1(mainBytes);
    var routes = new Dictionary<string, HttpResponseMessage>
    {
        ["/v2/project/main/version?game_versions=%5B%221.20.1%22%5D&loaders=%5B%22fabric%22%5D"] = JsonResponse(new JsonArray(new JsonObject
        {
            ["version_type"] = "release",
            ["version_number"] = "1.0.0",
            ["game_versions"] = new JsonArray("1.20.1"),
            ["loaders"] = new JsonArray("fabric"),
            ["dependencies"] = new JsonArray(new JsonObject { ["dependency_type"] = "required", ["project_id"] = "dep" }),
            ["files"] = new JsonArray(new JsonObject
            {
                ["primary"] = true,
                ["url"] = "https://files.example/main.jar",
                ["filename"] = "main.jar",
                ["hashes"] = new JsonObject { ["sha1"] = mainSha1 },
            }),
        })),
        ["/v2/project/dep/version?game_versions=%5B%221.20.1%22%5D&loaders=%5B%22fabric%22%5D"] = JsonResponse(new JsonArray(new JsonObject
        {
            ["version_type"] = "release",
            ["version_number"] = "1.0.0",
            ["game_versions"] = new JsonArray("1.20.1"),
            ["loaders"] = new JsonArray("fabric"),
            ["dependencies"] = new JsonArray(),
            ["files"] = new JsonArray(new JsonObject
            {
                ["primary"] = true,
                ["url"] = "https://files.example/dep.jar",
                ["filename"] = "dep.jar",
                ["hashes"] = new JsonObject { ["sha1"] = depSha1 },
            }),
        })),
        ["https://files.example/main.jar"] = BytesResponse(mainBytes),
        ["https://files.example/dep.jar"] = BytesResponse(depBytes),
    };
    var service = new ResourceMarketService(new HttpClient(new RoutingHandler(routes))) { ModrinthApiBase = "https://api.example/v2" };
    var result = service.InstallModrinthResourceAsync("main", "mod", "1.20.1", "fabric", temp.Path, installDependencies: true).GetAwaiter().GetResult();
    Assert.Equal("main.jar", result.Filename);
    Assert.True(File.Exists(Path.Combine(temp.Path, "main.jar")));
    Assert.True(File.Exists(Path.Combine(temp.Path, "dep.jar")));
    Assert.SequenceEqual(["dep.jar"], result.DependenciesInstalled);
});

return runner.Run();

static void WriteVersion(string root, string versionId, JsonObject data, bool jar = true)
{
    var versionDir = Path.Combine(root, "versions", versionId);
    Directory.CreateDirectory(versionDir);
    var payload = JsonNode.Parse(data.ToJsonString())!.AsObject();
    payload["id"] = versionId;
    File.WriteAllText(Path.Combine(versionDir, $"{versionId}.json"), payload.ToJsonString());
    if (jar)
    {
        File.WriteAllBytes(Path.Combine(versionDir, $"{versionId}.jar"), [0]);
    }
}

static string WriteMod(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    return McGo.Core.Utilities.FileUtilities.Sha1File(path);
}

static string Sha1(byte[] bytes)
{
    using var sha1 = System.Security.Cryptography.SHA1.Create();
    return Convert.ToHexString(sha1.ComputeHash(bytes)).ToLowerInvariant();
}

static string Sha256(byte[] bytes)
{
    using var sha256 = System.Security.Cryptography.SHA256.Create();
    return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
}

static HttpResponseMessage JsonResponse(JsonNode payload)
    => new(HttpStatusCode.OK) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };

static HttpResponseMessage BytesResponse(byte[] payload)
    => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

static byte[] ZipBytes(params (string Name, byte[] Bytes)[] entries)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(bytes);
        }
    }

    return output.ToArray();
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static void ReadExact(Stream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = stream.Read(buffer, offset, buffer.Length - offset);
        if (read <= 0)
        {
            throw new EndOfStreamException();
        }
        offset += read;
    }
}

static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
        if (read <= 0)
        {
            throw new EndOfStreamException();
        }

        offset += read;
    }
}

static async Task<TcpClient> ConnectWithRetryAsync(IPAddress address, int port, CancellationToken cancellationToken)
{
    Exception? last = null;
    for (var attempt = 0; attempt < 40; attempt++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address, port, cancellationToken);
            return client;
        }
        catch (Exception exc) when (exc is SocketException or IOException)
        {
            client.Dispose();
            last = exc;
            await Task.Delay(100, cancellationToken);
        }
    }

    throw new InvalidOperationException($"Could not connect to {address}:{port}", last);
}

static async Task IgnoreCancellationAsync(Task task)
{
    try
    {
        await task;
    }
    catch (OperationCanceledException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
}

internal sealed class TestRunner
{
    private readonly List<(string Name, Action Test)> _tests = [];

    public void Add(string name, Action test) => _tests.Add((name, test));

    public int Run()
    {
        var failed = 0;
        foreach (var (name, test) in _tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exc)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {exc.Message}");
            }
        }

        Console.WriteLine($"{_tests.Count - failed}/{_tests.Count} tests passed");
        return failed == 0 ? 0 : 1;
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }
    }

    public static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    public static void False(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    public static void Contains<T>(T expected, IEnumerable<T> values)
    {
        if (!values.Contains(expected))
        {
            throw new InvalidOperationException($"Expected sequence to contain {expected}.");
        }
    }

    public static void Contains(string expected, string value)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text to contain {expected}.");
        }
    }

    public static void Throws(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }

        throw new InvalidOperationException("Expected exception.");
    }

    public static TException Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new InvalidOperationException($"Expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message}");
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcgo-tests-" + Guid.NewGuid().ToString("N"));

    private TempDir() => Directory.CreateDirectory(Path);

    public static TempDir Create() => new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class JsonHandler : HttpMessageHandler
{
    private readonly JsonNode _payload;

    public JsonHandler(JsonNode payload)
    {
        _payload = payload;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_payload.ToJsonString(), Encoding.UTF8, "application/json"),
            RequestMessage = request,
        });
}

internal sealed class RoutingHandler : HttpMessageHandler
{
    private readonly IReadOnlyDictionary<string, HttpResponseMessage> _routes;

    public RoutingHandler(IReadOnlyDictionary<string, HttpResponseMessage> routes)
    {
        _routes = routes;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.RequestUri!.IsAbsoluteUri && request.RequestUri!.Host == "files.example"
            ? request.RequestUri!.ToString()
            : request.RequestUri!.PathAndQuery;
        if (!_routes.TryGetValue(key, out var response))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("missing route: " + key),
                RequestMessage = request,
            });
        }

        return Task.FromResult(CloneResponse(response, request));
    }

    private static HttpResponseMessage CloneResponse(HttpResponseMessage source, HttpRequestMessage request)
    {
        var clone = new HttpResponseMessage(source.StatusCode) { RequestMessage = request };
        var bytes = source.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        clone.Content = new ByteArrayContent(bytes);
        foreach (var header in source.Content.Headers)
        {
            clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}

internal sealed class FlakyHandler : HttpMessageHandler
{
    private readonly int _failuresBeforeSuccess;
    private readonly byte[] _successBytes;
    private int _attempts;

    public FlakyHandler(int failuresBeforeSuccess, HttpResponseMessage success)
    {
        _failuresBeforeSuccess = failuresBeforeSuccess;
        _successBytes = success.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    public int Attempts => _attempts;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        if (attempt <= _failuresBeforeSuccess)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("temporarily unavailable"),
                RequestMessage = request,
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(_successBytes),
            RequestMessage = request,
        });
    }
}

internal sealed class UdpTestPair
{
    private UdpTestPair(UdpReliableStream guest, UdpReliableStream host, LoopbackUdpTransport guestTransport, LoopbackUdpTransport hostTransport)
    {
        Guest = guest;
        Host = host;
        GuestTransport = guestTransport;
        HostTransport = hostTransport;
    }

    public UdpReliableStream Guest { get; }
    public UdpReliableStream Host { get; }
    public LoopbackUdpTransport GuestTransport { get; }
    public LoopbackUdpTransport HostTransport { get; }

    public static UdpTestPair Create()
    {
        var token = Encoding.ASCII.GetBytes("tokentest");
        var guestTransport = new LoopbackUdpTransport();
        var hostTransport = new LoopbackUdpTransport();
        var guest = new UdpReliableStream(guestTransport, 123, token, initiator: true);
        var host = new UdpReliableStream(hostTransport, 123, token, initiator: false);
        guestTransport.Target = host;
        hostTransport.Target = guest;
        return new UdpTestPair(guest, host, guestTransport, hostTransport);
    }
}

internal sealed class LoopbackUdpTransport : IUdpFrameTransport
{
    private readonly List<Func<UdpFrame, bool>> _dropPredicates = [];
    private readonly object _lock = new();

    public UdpReliableStream? Target { get; set; }
    public List<UdpFrame> SentFrames { get; } = [];

    public void DropNext(Func<UdpFrame, bool> predicate)
    {
        lock (_lock)
        {
            _dropPredicates.Add(predicate);
        }
    }

    public void Send(UdpFrame frame)
    {
        var shouldDrop = false;
        lock (_lock)
        {
            SentFrames.Add(frame);
            var drop = _dropPredicates.FirstOrDefault(predicate => predicate(frame));
            if (drop is not null)
            {
                _dropPredicates.Remove(drop);
                shouldDrop = true;
            }
        }

        if (shouldDrop)
        {
            return;
        }

        Target?.HandleFrame(frame);
    }
}

internal sealed class MiniUdpRegistrationRelay : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, IPEndPoint> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPEndPoint> _guests = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public static MiniUdpRegistrationRelay Start()
    {
        var relay = new MiniUdpRegistrationRelay();
        relay._loop = Task.Run(() => relay.RunAsync());
        return relay;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp.ReceiveAsync(_stop.Token);
            }
            catch
            {
                break;
            }

            JsonObject? request;
            try
            {
                request = JsonNode.Parse(Encoding.UTF8.GetString(packet.Buffer)) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (request?["type"]?.GetValue<string>() != "udp_register")
            {
                continue;
            }

            var response = HandleRegister(request, packet.RemoteEndPoint);
            await _udp.SendAsync(Encoding.UTF8.GetBytes(response.ToJsonString()), packet.RemoteEndPoint, _stop.Token);
        }
    }

    private JsonObject HandleRegister(JsonObject request, IPEndPoint endpoint)
    {
        var room = request["room"]?.GetValue<string>() ?? "";
        var side = request["side"]?.GetValue<string>() ?? "";
        if (side == "join")
        {
            side = "guest";
        }

        IPEndPoint? peer;
        lock (_hosts)
        {
            if (side == "host")
            {
                _hosts[room] = endpoint;
                _guests.TryGetValue(room, out peer);
            }
            else
            {
                _guests[room] = endpoint;
                _hosts.TryGetValue(room, out peer);
            }
        }

        return new JsonObject
        {
            ["type"] = "udp_registered",
            ["room"] = room,
            ["side"] = side,
            ["endpoint"] = EndpointJson(endpoint),
            ["peer"] = peer is null ? null : EndpointJson(peer),
        };
    }

    private static JsonObject EndpointJson(IPEndPoint endpoint)
        => new()
        {
            ["host"] = endpoint.Address.ToString(),
            ["port"] = endpoint.Port,
        };
}

internal sealed class FakeStunServer : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly IPEndPoint _mapped;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeStunServer(IPEndPoint mapped)
    {
        _mapped = mapped;
        _loop = Task.Run(RunAsync);
    }

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp.ReceiveAsync(_stop.Token);
            }
            catch
            {
                break;
            }

            if (packet.Buffer.Length < 20)
            {
                continue;
            }

            var transactionId = packet.Buffer.AsSpan(8, 12).ToArray();
            var response = BuildXorMappedResponse(transactionId, _mapped);
            await _udp.SendAsync(response, packet.RemoteEndPoint, _stop.Token);
        }
    }

    private static byte[] BuildXorMappedResponse(byte[] transactionId, IPEndPoint mapped)
    {
        var response = new byte[32];
        response[0] = 0x01;
        response[1] = 0x01;
        response[3] = 0x0c;
        response[4] = 0x21;
        response[5] = 0x12;
        response[6] = 0xA4;
        response[7] = 0x42;
        transactionId.CopyTo(response.AsSpan(8));
        response[21] = 0x20;
        response[23] = 0x08;
        response[25] = 0x01;
        var encodedPort = mapped.Port ^ 0x2112;
        response[26] = (byte)(encodedPort >> 8);
        response[27] = (byte)encodedPort;
        var cookie = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
        var ip = mapped.Address.GetAddressBytes();
        for (var index = 0; index < 4; index++)
        {
            response[28 + index] = (byte)(ip[index] ^ cookie[index]);
        }

        return response;
    }
}
