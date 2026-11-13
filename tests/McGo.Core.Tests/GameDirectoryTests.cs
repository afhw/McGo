using System.Text.Json;
using System.Text.Json.Nodes;
using McGo.Core.Configuration;
using McGo.Core.Installation;
using McGo.Core.Minecraft;
using McGo.Core.Storage;

internal static class GameDirectoryTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("Game directories read legacy relative paths without changing config", () =>
        {
            var config = new IniDocument();
            var entries = GameDirectoryCatalog.Read(config);
            Assert.Equal(1, entries.Count);
            Assert.Equal(Path.GetFullPath(".minecraft"), entries[0].Path);
            Assert.Equal(".minecraft", entries[0].Name);
            Assert.Equal(0, config.Sections.Count);

            config.Set("GAME", "directory", Path.Combine(".", ".minecraft"));
            var before = Snapshot(config);
            Assert.True(GameDirectoryCatalog.SamePath(".minecraft", GameDirectoryCatalog.Read(config)[0].Path));
            Assert.Equal(before, Snapshot(config));
        });

        runner.Add("Game directories deduplicate normalized paths and preserve root paths", () =>
        {
            using var temp = TempDir.Create();
            var current = Path.Combine(temp.Path, "Game");
            var config = ConfigFor(current);
            var equivalent = OperatingSystem.IsWindows() ? current.ToUpperInvariant() : current;
            config.Set("GAME", "directories", JsonSerializer.Serialize(new[]
            {
                new GameDirectoryEntry("Original", current + Path.DirectorySeparatorChar),
                new GameDirectoryEntry("Duplicate", equivalent),
                new GameDirectoryEntry("Dot segments", Path.Combine(current, "child", "..")),
                new GameDirectoryEntry("Other", Path.Combine(temp.Path, "Other")),
            }));

            var entries = GameDirectoryCatalog.Read(config);
            Assert.Equal(2, entries.Count);
            Assert.Equal("Original", entries[0].Name);
            var added = GameDirectoryCatalog.Add(config, equivalent + Path.AltDirectorySeparatorChar, "Ignored");
            Assert.Equal(entries[0], added);
            Assert.Equal(2, GameDirectoryCatalog.Read(config).Count);
            Assert.True(GameDirectoryCatalog.SamePath(current, current + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar));
            Assert.False(GameDirectoryCatalog.SamePath(current, current + "Other"));
            var root = Path.GetPathRoot(temp.Path)!;
            Assert.Equal(root, GameDirectoryCatalog.Add(config, root).Path);
        });

        runner.Add("Game directories tolerate malformed catalogs and skip invalid stored paths", () =>
        {
            var config = ConfigFor(".minecraft");
            foreach (var json in new[] { "[", "{}", "null", "[]" })
            {
                config.Set("GAME", "directories", json);
                Assert.Equal(1, GameDirectoryCatalog.Read(config).Count);
                Assert.Equal(json, config.Get("GAME", "directories"));
            }

            config.Set("GAME", "directories", "[null,{\"path\":\"\"},{\"path\":\"bad\\u0000path\"},{\"name\":\"Default\",\"path\":\".minecraft\"}]");
            var entries = GameDirectoryCatalog.Read(config);
            Assert.Equal(1, entries.Count);
            Assert.Equal("Default", entries[0].Name);
        });

        runner.Add("Game directories stage changes until SaveConfig and retain the first legacy owner", () =>
        {
            using var temp = TempDir.Create();
            var app = new AppSettings(Path.Combine(temp.Path, "launcher_config.ini"), Path.Combine(temp.Path, "accounts.json"));
            app.LoadConfig();
            var originalFile = File.ReadAllText(app.ConfigPath);
            var added = GameDirectoryCatalog.Add(app.Config, Path.Combine(temp.Path, "New"), "  New game  ");
            Assert.Equal("New game", added.Name);
            Assert.Equal(".minecraft", app.Config.Get("GAME", "directory"));
            Assert.Equal("", app.Config.Get("GAME", "legacy_settings_directory"));
            GameDirectoryCatalog.Select(app.Config, added.Path);
            Assert.Equal(".minecraft", app.Config.Get("GAME", "legacy_settings_directory"));
            GameDirectoryCatalog.Rename(app.Config, added.Path, "  Renamed  ");
            GameDirectoryCatalog.Select(app.Config, ".minecraft");
            GameDirectoryCatalog.Select(app.Config, added.Path);
            Assert.Equal(".minecraft", app.Config.Get("GAME", "legacy_settings_directory"));
            Assert.Equal(originalFile, File.ReadAllText(app.ConfigPath));
            Assert.False(Directory.Exists(added.Path));

            app.SaveConfig();
            var saved = IniDocument.Load(app.ConfigPath);
            Assert.Equal(".minecraft", saved.Get("GAME", "legacy_settings_directory"));
            Assert.Equal(added.Path, saved.Get("GAME", "directory"));
            Assert.Equal("Renamed", GameDirectoryCatalog.Read(saved)[1].Name);
            Assert.Equal(2, JsonNode.Parse(saved.Get("GAME", "directories"))!.AsArray().Count);
            Assert.False(Directory.Exists(added.Path));
        });

        runner.Add("Game directories remove only config and select the first remaining entry", () =>
        {
            using var temp = TempDir.Create();
            var original = Path.Combine(temp.Path, "Original");
            Directory.CreateDirectory(original);
            var marker = Path.Combine(original, "keep.txt");
            File.WriteAllText(marker, "keep");
            var config = ConfigFor(original);
            var first = GameDirectoryCatalog.Add(config, Path.Combine(temp.Path, "First"));
            var second = GameDirectoryCatalog.Add(config, Path.Combine(temp.Path, "Second"));
            GameDirectoryCatalog.Remove(config, original);
            Assert.Equal(first.Path, config.Get("GAME", "directory"));
            Assert.Equal(original, config.Get("GAME", "legacy_settings_directory"));
            Assert.Equal("keep", File.ReadAllText(marker));
            GameDirectoryCatalog.Remove(config, second.Path);
            Assert.Equal(first.Path, config.Get("GAME", "directory"));
            var before = Snapshot(config);
            Assert.Throws<InvalidOperationException>(() => GameDirectoryCatalog.Remove(config, first.Path));
            Assert.Equal(before, Snapshot(config));
        });

        runner.Add("Game directories reject invalid paths and names without partial edits", () =>
        {
            using var temp = TempDir.Create();
            var config = ConfigFor(temp.Path);
            var before = Snapshot(config);
            var invalidPaths = new List<string> { "", " ", "\0" };
            if (OperatingSystem.IsWindows()) invalidPaths.AddRange([
                "bad|path", "bad*path", "bad?path", "bad:path", "bad\"path", "bad.", "bad ",
                "CON", "aux.txt", Path.Combine(temp.Path, "LPT1"), Path.Combine(temp.Path, "COM1.txt", "child"),
            ]);
            foreach (var invalid in invalidPaths)
            {
                Assert.Throws<ArgumentException>(() => GameDirectoryCatalog.Add(config, invalid));
                Assert.Throws<ArgumentException>(() => GameDirectoryCatalog.Select(config, invalid));
                Assert.Throws<ArgumentException>(() => GameDirectoryCatalog.Remove(config, invalid));
                Assert.Throws<ArgumentException>(() => GameDirectoryCatalog.Rename(config, invalid, "Name"));
                Assert.Throws<ArgumentException>(() => GameDirectoryCatalog.SamePath(temp.Path, invalid));
            }

            var unknown = Path.Combine(temp.Path, "unknown");
            Assert.Throws<ArgumentException>(() => GameDirectoryCatalog.Select(config, unknown));
            Assert.Throws<ArgumentException>(() => GameDirectoryCatalog.Remove(config, unknown));
            Assert.Throws<ArgumentException>(() => GameDirectoryCatalog.Rename(config, unknown, "Name"));
            Assert.Throws<ArgumentException>(() => GameDirectoryCatalog.Rename(config, temp.Path, " "));
            Assert.Equal(before, Snapshot(config));
        });

        runner.Add("Game directories recover the catalog and legacy owner together from config backup", () =>
        {
            using var temp = TempDir.Create();
            var app = new AppSettings(Path.Combine(temp.Path, "launcher_config.ini"), Path.Combine(temp.Path, "accounts.json"));
            app.LoadConfig();
            var added = GameDirectoryCatalog.Add(app.Config, Path.Combine(temp.Path, "New"), "Backup");
            GameDirectoryCatalog.Select(app.Config, added.Path);
            app.SaveConfig();
            GameDirectoryCatalog.Rename(app.Config, added.Path, "Current");
            app.SaveConfig();
            File.WriteAllText(app.ConfigPath, "[GAME");
            app.LoadConfig();
            Assert.Equal(added.Path, app.Config.Get("GAME", "directory"));
            Assert.Equal(".minecraft", app.Config.Get("GAME", "legacy_settings_directory"));
            Assert.Equal("Backup", GameDirectoryCatalog.Read(app.Config)[1].Name);
        });

        runner.Add("Directory settings use relative legacy ownership across saved directory switches", () =>
        {
            using var temp = TempDir.Create();
            InDirectory(temp.Path, () =>
            {
                var app = new AppSettings();
                app.LoadConfig();
                var service = new VersionSettingsService();
                service.SaveVersionSettings(Settings("Legacy"));
                Assert.Equal("Legacy", Alias(service.LoadForGameDirectory(Path.GetFullPath(".minecraft"))));
                var added = GameDirectoryCatalog.Add(app.Config, "other");
                GameDirectoryCatalog.Select(app.Config, added.Path);
                app.SaveConfig();
                Assert.True(service.LoadForGameDirectory(added.Path)["1.20.1"] is null);
                Assert.Equal("Legacy", Alias(service.LoadForGameDirectory(".minecraft" + Path.DirectorySeparatorChar)));
                Assert.False(Directory.Exists(".minecraft"));
                Assert.False(Directory.Exists(added.Path));
                var migrated = service.LoadForGameDirectory(".minecraft");
                service.SaveForGameDirectory(migrated, ".minecraft");
                service.SaveVersionSettings(Settings("Changed global"));
                Assert.Equal("Legacy", Alias(service.LoadForGameDirectory(".minecraft")));
                Assert.Equal("Changed global", Alias(service.LoadVersionSettings()));
            });
        });

        runner.Add("Directory settings allow only the configured legacy owner to inherit globals", () =>
        {
            using var temp = TempDir.Create();
            var old = Path.Combine(temp.Path, "Old");
            var other = Path.Combine(temp.Path, "Other");
            var config = ConfigFor(old);
            var legacyPath = Path.Combine(temp.Path, "legacy.json");
            var service = new VersionSettingsService(config: config, legacySettingsPath: legacyPath);
            service.SaveVersionSettings(Settings("Legacy"), legacyPath);
            Assert.Equal("Legacy", Alias(service.LoadForGameDirectory(old)));
            Assert.True(service.LoadForGameDirectory(other)["1.20.1"] is null);
            GameDirectoryCatalog.Add(config, other);
            GameDirectoryCatalog.Select(config, other);
            Assert.Equal("Legacy", Alias(service.LoadForGameDirectory(old)));
            Assert.True(service.LoadForGameDirectory(other)["1.20.1"] is null);
            GameDirectoryCatalog.Remove(config, old);
            Assert.Equal(old, config.Get("GAME", "legacy_settings_directory"));
            Assert.True(service.LoadForGameDirectory(other)["1.20.1"] is null);
        });

        runner.Add("Directory settings return independent defaults when legacy and config files are missing", () =>
        {
            using var temp = TempDir.Create();
            InDirectory(temp.Path, () =>
            {
                var service = new VersionSettingsService();
                var first = service.LoadForGameDirectory(".minecraft");
                Assert.Equal("", first["_meta"]?["last_launched_version"]?.GetValue<string>());
                first["1.20.1"] = new JsonObject { ["alias"] = "In memory" };
                Assert.True(service.LoadForGameDirectory(".minecraft")["1.20.1"] is null);
                Assert.True(service.LoadForGameDirectory("other")["1.20.1"] is null);
                Assert.Equal(0, Directory.GetFileSystemEntries(temp.Path).Length);
            });
        });

        runner.Add("Directory settings isolate identical version IDs and metadata", () =>
        {
            using var temp = TempDir.Create();
            var first = Path.Combine(temp.Path, "First");
            var second = Path.Combine(temp.Path, "Second");
            var legacy = Path.Combine(temp.Path, "legacy.json");
            var service = new VersionSettingsService(config: ConfigFor(first), legacySettingsPath: legacy);
            service.SaveVersionSettings(Settings("Global"), legacy);
            var globalBefore = File.ReadAllText(legacy);
            service.SaveForGameDirectory(Settings("First"), first);
            service.SaveForGameDirectory(Settings("Second"), second);
            Assert.Equal("First", Alias(service.LoadForGameDirectory(first)));
            Assert.Equal("Second", Alias(service.LoadForGameDirectory(second)));
            Assert.Equal("First", service.LoadForGameDirectory(first)["_meta"]?["last_launched_version"]?.GetValue<string>());
            Assert.Equal("Second", service.LoadForGameDirectory(second)["_meta"]?["last_launched_version"]?.GetValue<string>());
            var changed = service.LoadForGameDirectory(first);
            changed["1.20.1"]!["alias"] = "Changed";
            service.SaveForGameDirectory(changed, first);
            Assert.Equal("Second", Alias(service.LoadForGameDirectory(second)));
            Assert.Equal(globalBefore, File.ReadAllText(legacy));
        });

        foreach (var state in new[] { "missing", "truncated", "array", "null" })
        {
            runner.Add($"Directory settings atomically save and restore local backup: {state}", () =>
            {
                using var temp = TempDir.Create();
                var game = Path.Combine(temp.Path, "Game");
                var legacy = Path.Combine(temp.Path, "legacy.json");
                var service = new VersionSettingsService(config: ConfigFor(game), legacySettingsPath: legacy);
                service.SaveVersionSettings(Settings("Global"), legacy);
                service.SaveForGameDirectory(Settings("Backup"), game);
                service.SaveForGameDirectory(Settings("Current"), game);
                var path = LocalPath(game);
                Assert.Equal("Current", Alias(service.LoadForGameDirectory(game)));
                Assert.Equal("Backup", Alias(service.LoadVersionSettings(AtomicStorage.BackupPath(path))));
                if (state == "missing") File.Delete(path);
                else File.WriteAllText(path, state == "truncated" ? "{" : state == "array" ? "[]" : "null");
                Assert.Equal("Backup", Alias(service.LoadForGameDirectory(game)));
                Assert.Equal("Backup", Alias(JsonNode.Parse(File.ReadAllText(path))!.AsObject()));
                Assert.Equal(state == "missing" ? 0 : 1, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*").Length);
                Assert.Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length);
            });
        }

        runner.Add("Directory settings never resurrect globals after unrecoverable local corruption", () =>
        {
            using var temp = TempDir.Create();
            var game = Path.Combine(temp.Path, "Game");
            var legacy = Path.Combine(temp.Path, "legacy.json");
            var service = new VersionSettingsService(config: ConfigFor(game), legacySettingsPath: legacy);
            service.SaveVersionSettings(Settings("Global"), legacy);
            service.SaveForGameDirectory(Settings("Local"), game);
            File.WriteAllText(LocalPath(game), "{");
            for (var index = 0; index < 2; index++)
            {
                Assert.True(service.LoadForGameDirectory(game)["1.20.1"] is null);
            }

            Assert.Equal("Global", Alias(service.LoadVersionSettings(legacy)));
        });

        runner.Add("Directory settings recover legacy backups and retain the old file API", () =>
        {
            using var temp = TempDir.Create();
            var game = Path.Combine(temp.Path, "Game");
            var legacy = Path.Combine(temp.Path, "legacy.json");
            var service = new VersionSettingsService(config: ConfigFor(game), legacySettingsPath: legacy);
            service.SaveVersionSettings(Settings("Backup"), legacy);
            service.SaveVersionSettings(Settings("Current"), legacy);
            File.WriteAllText(legacy, "{");
            Assert.Equal("Backup", Alias(service.LoadForGameDirectory(game)));
            Assert.Equal("Backup", Alias(service.LoadVersionSettings(legacy)));
            service.SaveVersionSettings(new JsonObject(), legacy);
            Assert.True(service.LoadVersionSettings(legacy)["_meta"] is JsonObject);
            Assert.False(Directory.Exists(game));
        });

        runner.Add("Directory settings reject invalid target paths before writing", () =>
        {
            using var temp = TempDir.Create();
            var service = new VersionSettingsService(config: ConfigFor(temp.Path), legacySettingsPath: Path.Combine(temp.Path, "legacy.json"));
            foreach (var invalid in new[] { "", " ", "\0" })
            {
                Assert.Throws<ArgumentException>(() => service.LoadForGameDirectory(invalid));
                Assert.Throws<ArgumentException>(() => service.SaveForGameDirectory(Settings("Invalid"), invalid));
            }

            Assert.Equal(0, Directory.GetFileSystemEntries(temp.Path).Length);
        });

        runner.Add("Installer settings follow the requested game directory instead of the selected directory", () =>
        {
            using var temp = TempDir.Create();
            var selected = Path.Combine(temp.Path, "Selected");
            var requested = Path.Combine(temp.Path, "Requested");
            var config = ConfigFor(selected);
            var legacy = Path.Combine(temp.Path, "legacy.json");
            var service = new VersionSettingsService(config: config, legacySettingsPath: legacy);
            var globalSettings = Settings("Legacy");
            globalSettings["1.20.1"]!["runtime_directory"] = Path.Combine(temp.Path, "Wrong");
            service.SaveVersionSettings(globalSettings, legacy);
            var installer = new InstallerEngine("1.20.1", "official", requested, "java", globalIsolation: false, versionSettings: service);
            Assert.Equal(requested, installer.RuntimeDirectoryFor("1.20.1"));
            var localSettings = Settings("Local");
            var runtime = Path.Combine(requested, "runtime");
            localSettings["1.20.1"]!["runtime_directory"] = runtime;
            service.SaveForGameDirectory(localSettings, requested);
            Assert.Equal(runtime, installer.RuntimeDirectoryFor("1.20.1"));
            Assert.Equal(Path.Combine(temp.Path, "Wrong"), new InstallerEngine("1.20.1", "official", selected, "java", globalIsolation: false, versionSettings: service).RuntimeDirectoryFor("1.20.1"));
        });
    }

    private static IniDocument ConfigFor(string directory)
    {
        var config = new IniDocument();
        config.Set("GAME", "directory", directory);
        return config;
    }

    private static JsonObject Settings(string alias)
        => new()
        {
            ["1.20.1"] = new JsonObject { ["alias"] = alias },
            ["_meta"] = new JsonObject { ["last_launched_version"] = alias },
        };

    private static string? Alias(JsonObject settings) => settings["1.20.1"]?["alias"]?.GetValue<string>();
    private static string LocalPath(string game) => Path.Combine(game, ".mcgo", VersionSettingsService.VersionSettingsFile);
    private static string Snapshot(IniDocument config) => JsonSerializer.Serialize(config.Sections);

    private static void InDirectory(string directory, Action action)
    {
        var original = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(directory);
            action();
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }
}
