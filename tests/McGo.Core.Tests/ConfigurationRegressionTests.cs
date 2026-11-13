using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Storage;

internal static class ConfigurationRegressionTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("Config merge preserves alternating page edits and unchanged bulk setters", () =>
        {
            using var temp = TempDir.Create();
            var first = CreateSettings(temp.Path);
            first.LoadConfig();
            var second = CreateSettings(temp.Path);
            second.LoadConfig();

            first.Config.Set("UI", "theme", "light");
            first.SaveConfig();
            second.Config.Set("DOWNLOAD", "max_core_threads", "6");
            second.Config.Set("UI", "theme", "dark");
            second.SaveConfig();

            first.Config.Set("DOWNLOAD", "max_core_threads", "12");
            first.Config.Set("MUSIC", "volume", "40");
            first.SaveConfig();
            second.Config.Set("DOWNLOAD", "max_core_threads", "8");
            second.Config.Set("UI", "theme", "dark");
            second.SaveConfig();

            var saved = IniDocument.Load(first.ConfigPath);
            Assert.Equal("light", saved.Get("UI", "theme"));
            Assert.Equal("8", saved.Get("DOWNLOAD", "max_core_threads"));
            Assert.Equal("40", saved.Get("MUSIC", "volume"));
            var before = CaptureFiles(temp.Path);
            first.SaveConfig();
            second.SaveConfig();
            AssertFilesUnchanged(temp.Path, before);
        });

        runner.Add("Config merge uses the last actual same-field edit and ignores repeated saves", () =>
        {
            using var temp = TempDir.Create();
            var first = CreateSettings(temp.Path);
            var second = CreateSettings(temp.Path);
            first.LoadConfig();
            second.LoadConfig();
            first.Config.Set("UI", "theme", "light");
            second.Config.Set("UI", "theme", "system");
            first.SaveConfig();
            second.SaveConfig();
            first.SaveConfig();
            Assert.Equal("system", IniDocument.Load(first.ConfigPath).Get("UI", "theme"));

            first.Config.Set("UI", "theme", "dark");
            first.SaveConfig();
            second.SaveConfig();
            Assert.Equal("dark", IniDocument.Load(first.ConfigPath).Get("UI", "theme"));
        });

        runner.Add("Config merge handles changed-back values, casing and dictionary edits", () =>
        {
            using var temp = TempDir.Create();
            var first = CreateSettings(temp.Path);
            first.LoadConfig();
            first.Config.Set("EXTENSION", "remove_me", "old");
            first.SaveConfig();
            var second = CreateSettings(temp.Path);
            second.LoadConfig();
            first.Config.Set("ui", "THEME", "light");
            first.Config.Set("EXTENSION", "new_key", "keep");
            first.SaveConfig();

            second.Config.Set("UI", "theme", "system");
            second.Config.Set("ui", "THEME", "dark");
            second.Config.Sections["download"]["MAX_CORE_THREADS"] = "7";
            second.Config.Sections["extension"].Remove("REMOVE_ME");
            second.SaveConfig();

            var saved = IniDocument.Load(first.ConfigPath);
            Assert.Equal("light", saved.Get("UI", "theme"));
            Assert.Equal("7", saved.Get("DOWNLOAD", "max_core_threads"));
            Assert.Equal("keep", saved.Get("EXTENSION", "new_key"));
            Assert.False(saved.Sections["EXTENSION"].ContainsKey("remove_me"));
        });

        runner.Add("Config merge supports save before load and preserves unknown latest fields", () =>
        {
            using var temp = TempDir.Create();
            var settings = CreateSettings(temp.Path);
            File.WriteAllText(settings.ConfigPath, "[EXTENSION]\nunknown = keep\n[UI]\ntheme = light\n");
            settings.Config.Set("DOWNLOAD", "max_core_threads", "5");
            settings.SaveConfig();
            var saved = IniDocument.Load(settings.ConfigPath);
            Assert.Equal("keep", saved.Get("EXTENSION", "unknown"));
            Assert.Equal("light", saved.Get("UI", "theme"));
            Assert.Equal("5", saved.Get("DOWNLOAD", "max_core_threads"));
        });

        runner.Add("Config merge does not replay read-only defaults or migration over newer user values", () =>
        {
            using var temp = TempDir.Create();
            var stale = CreateSettings(temp.Path);
            File.WriteAllText(stale.ConfigPath, $"[UPDATE]\nmanifest_url = {AppConstants.LegacyUpdateManifestUrl}\n");
            stale.LoadConfig(persistChanges: false);
            Assert.Equal(AppConstants.DefaultUpdateManifestUrl, stale.Config.Get("UPDATE", "manifest_url"));
            var current = CreateSettings(temp.Path);
            current.LoadConfig();
            current.Config.Set("UPDATE", "manifest_url", "https://example.org/custom.json");
            current.Config.Set("UI", "theme", "light");
            current.SaveConfig();

            stale.Config.Set("UPDATE", "manifest_url", AppConstants.DefaultUpdateManifestUrl);
            stale.Config.Set("UI", "theme", "dark");
            stale.Config.Set("MUSIC", "volume", "50");
            stale.SaveConfig();
            var saved = IniDocument.Load(stale.ConfigPath);
            Assert.Equal("https://example.org/custom.json", saved.Get("UPDATE", "manifest_url"));
            Assert.Equal("light", saved.Get("UI", "theme"));
            Assert.Equal("50", saved.Get("MUSIC", "volume"));
        });

        runner.Add("Config merge applies an explicit user edit after migration even when it equals the raw old value", () =>
        {
            using var temp = TempDir.Create();
            var stale = CreateSettings(temp.Path);
            File.WriteAllText(stale.ConfigPath, $"[UPDATE]\nmanifest_url = {AppConstants.LegacyUpdateManifestUrl}\n");
            stale.LoadConfig(persistChanges: false);
            var current = CreateSettings(temp.Path);
            current.LoadConfig();
            current.Config.Set("UPDATE", "manifest_url", "https://example.org/custom.json");
            current.SaveConfig();

            stale.Config.Set("UPDATE", "manifest_url", AppConstants.LegacyUpdateManifestUrl);
            stale.SaveConfig();
            stale.SaveConfig();
            Assert.Equal(AppConstants.LegacyUpdateManifestUrl,
                IniDocument.Load(stale.ConfigPath).Get("UPDATE", "manifest_url"));
        });

        runner.Add("Config merge persists deferred defaults and migration when the source is unchanged", () =>
        {
            using var temp = TempDir.Create();
            var settings = CreateSettings(temp.Path);
            File.WriteAllText(settings.ConfigPath, $"[UPDATE]\nmanifest_url = {AppConstants.LegacyUpdateManifestUrl}\n");
            settings.LoadConfig(persistChanges: false);
            settings.SaveConfig();
            var saved = IniDocument.Load(settings.ConfigPath);
            Assert.Equal(AppConstants.DefaultUpdateManifestUrl, saved.Get("UPDATE", "manifest_url"));
            Assert.Equal("dark", saved.Get("UI", "theme"));
        });

        runner.Add("Config merge only fills defaults after an external change and leaves migration to explicit load", () =>
        {
            using var temp = TempDir.Create();
            var settings = CreateSettings(temp.Path);
            File.WriteAllText(settings.ConfigPath, $"[UPDATE]\nmanifest_url = {AppConstants.LegacyUpdateManifestUrl}\n");
            settings.LoadConfig(persistChanges: false);
            var external = IniDocument.Load(settings.ConfigPath);
            external.Set("EXTENSION", "new_key", "keep");
            external.Set("UI", "theme", "light");
            external.Save(settings.ConfigPath);
            settings.SaveConfig();
            settings.SaveConfig();
            var saved = IniDocument.Load(settings.ConfigPath);
            Assert.Equal(AppConstants.LegacyUpdateManifestUrl, saved.Get("UPDATE", "manifest_url"));
            Assert.Equal("light", saved.Get("UI", "theme"));
            Assert.Equal("12", saved.Get("DOWNLOAD", "max_core_threads"));
            Assert.Equal("keep", saved.Get("EXTENSION", "new_key"));

            settings.LoadConfig();
            Assert.Equal(AppConstants.DefaultUpdateManifestUrl,
                IniDocument.Load(settings.ConfigPath).Get("UPDATE", "manifest_url"));
        });

        runner.Add("Config stale read-only save preserves another instance's explicit return to the legacy URL", () =>
        {
            using var temp = TempDir.Create();
            var stale = CreateSettings(temp.Path);
            File.WriteAllText(stale.ConfigPath, $"[UPDATE]\nmanifest_url = {AppConstants.LegacyUpdateManifestUrl}\n");
            stale.LoadConfig(persistChanges: false);
            var current = CreateSettings(temp.Path);
            current.LoadConfig();
            current.Config.Set("UPDATE", "manifest_url", AppConstants.LegacyUpdateManifestUrl);
            current.SaveConfig();

            stale.Config.Set("MUSIC", "volume", "50");
            stale.SaveConfig();
            var saved = IniDocument.Load(stale.ConfigPath);
            Assert.Equal(AppConstants.LegacyUpdateManifestUrl, saved.Get("UPDATE", "manifest_url"));
            Assert.Equal("50", saved.Get("MUSIC", "volume"));

            stale.Config.Set("UPDATE", "manifest_url", AppConstants.DefaultUpdateManifestUrl);
            stale.Config.Set("MUSIC", "volume", "55");
            stale.SaveConfig();
            saved = IniDocument.Load(stale.ConfigPath);
            Assert.Equal(AppConstants.LegacyUpdateManifestUrl, saved.Get("UPDATE", "manifest_url"));
            Assert.Equal("55", saved.Get("MUSIC", "volume"));
        });

        foreach (var primaryState in new[] { "missing", "corrupt", "legacy" })
        {
            runner.Add($"Config read-only load leaves primary and backup untouched: {primaryState}", () =>
            {
                using var temp = TempDir.Create();
                var settings = CreateSettings(temp.Path);
                var content = $"; Preserve formatting\n[UPDATE]\nmanifest_url={AppConstants.LegacyUpdateManifestUrl}\n";
                if (primaryState != "missing")
                {
                    File.WriteAllText(settings.ConfigPath, primaryState == "corrupt" ? "[UPDATE" : content);
                }
                File.WriteAllText(AtomicStorage.BackupPath(settings.ConfigPath), content);
                var before = CaptureFiles(temp.Path);
                settings.LoadConfig(persistChanges: false);
                Assert.Equal("dark", settings.Config.Get("UI", "theme"));
                Assert.Equal(AppConstants.DefaultUpdateManifestUrl, settings.Config.Get("UPDATE", "manifest_url"));
                AssertFilesUnchanged(temp.Path, before);
            });
        }

        runner.Add("Config read-only load creates no directory and later merge preserves another instance", () =>
        {
            using var temp = TempDir.Create();
            var directory = Path.Combine(temp.Path, "absent");
            var first = CreateSettings(directory);
            first.LoadConfig(persistChanges: false);
            Assert.False(Directory.Exists(directory));
            var second = CreateSettings(directory);
            second.LoadConfig();
            second.Config.Set("UI", "theme", "light");
            second.SaveConfig();
            first.SaveConfig();
            Assert.Equal("light", IniDocument.Load(first.ConfigPath).Get("UI", "theme"));
        });

        runner.Add("Default download options read a backup without repairing files", () =>
        {
            using var temp = TempDir.Create();
            var originalDirectory = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(temp.Path);
                var path = Path.Combine(temp.Path, AppConstants.ConfigFile);
                File.WriteAllText(path, "[DOWNLOAD");
                File.WriteAllText(AtomicStorage.BackupPath(path),
                    "[DOWNLOAD]\nmax_core_threads=5\nmax_asset_threads=9\nspeed_limit_kbps=512\n");
                var before = CaptureFiles(temp.Path);
                var options = AppSettings.DefaultDownloadOptions();
                Assert.Equal(5, options.MaxCoreConcurrency);
                Assert.Equal(9, options.MaxAssetConcurrency);
                Assert.Equal(512, options.SpeedLimitKbps);
                AssertFilesUnchanged(temp.Path, before);
            }
            finally
            {
                Directory.SetCurrentDirectory(originalDirectory);
            }
        });

        runner.Add("Config normal load only writes when defaults or migration actually change", () =>
        {
            using var temp = TempDir.Create();
            var settings = CreateSettings(temp.Path);
            settings.LoadConfig();
            File.WriteAllText(settings.ConfigPath, "; Preserve this comment\n" + File.ReadAllText(settings.ConfigPath));
            var before = CaptureFiles(temp.Path);
            settings.LoadConfig();
            settings.SaveConfig();
            AssertFilesUnchanged(temp.Path, before);
        });

        runner.Add("Config merge retains edits when persistence fails and succeeds on retry", () =>
        {
            using var temp = TempDir.Create();
            var settings = CreateSettings(temp.Path);
            settings.LoadConfig(persistChanges: false);
            settings.Config.Set("UI", "theme", "light");
            Directory.CreateDirectory(settings.ConfigPath);
            Assert.Throws(() => settings.SaveConfig());
            Directory.Delete(settings.ConfigPath);
            settings.SaveConfig();
            Assert.Equal("light", IniDocument.Load(settings.ConfigPath).Get("UI", "theme"));
        });

        runner.Add("Config merge serializes real concurrent saves from two instances and path aliases", () =>
        {
            using var temp = TempDir.Create();
            var first = CreateSettings(temp.Path);
            var alias = Path.Combine(temp.Path, ".", "launcher_config.ini");
            if (OperatingSystem.IsWindows()) alias = alias.ToUpperInvariant();
            var second = new AppSettings(alias, first.AccountsPath);
            first.LoadConfig();
            second.LoadConfig();
            using var barrier = new Barrier(2);
            RunTogether(
                () => SaveRounds(first, "first"),
                () => SaveRounds(second, "second"));

            void SaveRounds(AppSettings settings, string key)
            {
                for (var round = 0; round < 16; round++)
                {
                    settings.Config.Set("CONCURRENT", key, round.ToString());
                    Rendezvous(barrier);
                    settings.SaveConfig();
                    Rendezvous(barrier);
                    var saved = IniDocument.Load(first.ConfigPath);
                    Assert.Equal(round.ToString(), saved.Get("CONCURRENT", "first"));
                    Assert.Equal(round.ToString(), saved.Get("CONCURRENT", "second"));
                    Assert.Equal("dark", saved.Get("UI", "theme"));
                    Rendezvous(barrier);
                }
            }

            Assert.Equal("dark", IniDocument.Load(AtomicStorage.BackupPath(first.ConfigPath)).Get("UI", "theme"));
        });

        runner.Add("Config default loads racing user saves preserve the latest edited fields", () =>
        {
            using var temp = TempDir.Create();
            var writer = CreateSettings(temp.Path);
            File.WriteAllText(writer.ConfigPath, $"[UPDATE]\nmanifest_url = {AppConstants.LegacyUpdateManifestUrl}\n");
            writer.LoadConfig(persistChanges: false);
            var loader = CreateSettings(temp.Path);
            using var barrier = new Barrier(2);
            RunTogether(() =>
            {
                for (var round = 0; round < 16; round++)
                {
                    var url = $"https://example.org/{round}.json";
                    writer.Config.Set("UPDATE", "manifest_url", url);
                    writer.Config.Set("UI", "theme", $"custom-{round}");
                    Rendezvous(barrier);
                    writer.SaveConfig();
                    Rendezvous(barrier);
                    var saved = IniDocument.Load(writer.ConfigPath);
                    Assert.Equal(url, saved.Get("UPDATE", "manifest_url"));
                    Assert.Equal($"custom-{round}", saved.Get("UI", "theme"));
                    Rendezvous(barrier);
                }
            }, () =>
            {
                for (var round = 0; round < 16; round++)
                {
                    Rendezvous(barrier);
                    loader.LoadConfig();
                    Rendezvous(barrier);
                    Rendezvous(barrier);
                }
            });
        });
    }

    private static AppSettings CreateSettings(string directory)
        => new(Path.Combine(directory, "launcher_config.ini"), Path.Combine(directory, "accounts.json"));

    private static Dictionary<string, (string Bytes, DateTime LastWrite)> CaptureFiles(string directory)
        => Directory.GetFiles(directory).ToDictionary(path => path,
            path => (Convert.ToBase64String(File.ReadAllBytes(path)), File.GetLastWriteTimeUtc(path)));

    private static void AssertFilesUnchanged(string directory, Dictionary<string, (string Bytes, DateTime LastWrite)> before)
    {
        Assert.Equal(before.Count, Directory.GetFiles(directory).Length);
        foreach (var (path, snapshot) in before)
        {
            Assert.True(File.Exists(path));
            Assert.Equal(snapshot.Bytes, Convert.ToBase64String(File.ReadAllBytes(path)));
            Assert.Equal(snapshot.LastWrite, File.GetLastWriteTimeUtc(path));
        }
    }

    private static void Rendezvous(Barrier barrier)
    {
        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Concurrent config operation did not reach the barrier.");
        }
    }

    private static void RunTogether(Action first, Action second)
    {
        var tasks = new[] { first, second }.Select(action => Task.Factory.StartNew(action,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Task.WaitAll(tasks);
    }
}
