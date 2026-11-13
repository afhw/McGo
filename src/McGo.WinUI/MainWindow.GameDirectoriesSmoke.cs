using System.Text.Json.Nodes;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Minecraft;
using McGo.WinUI.Pages;
using Microsoft.UI.Xaml.Controls;

namespace McGo.WinUI;

public sealed partial class MainWindow
{
    internal async Task RunGameDirectorySmokeChecksAsync()
    {
        var settings = new AppSettings();
        settings.LoadConfig();
        var original = settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        var first = Path.GetFullPath("directory-smoke-a");
        var second = Path.GetFullPath("directory-smoke-b");
        var service = new VersionSettingsService();
        foreach (var (directory, id, alias) in new[] { (first, "1.20.1", "First"), (second, "1.21.1", "Second") })
        {
            Directory.CreateDirectory(Path.Combine(directory, "versions", id));
            File.WriteAllText(Path.Combine(directory, "versions", id, id + ".json"),
                new JsonObject { ["id"] = id, ["type"] = "release", ["javaVersion"] = new JsonObject { ["majorVersion"] = 17 } }.ToJsonString());
            service.SaveForGameDirectory(new JsonObject { [id] = new JsonObject { ["alias"] = alias } }, directory);
            GameDirectoryCatalog.Add(settings.Config, directory, alias);
        }
        settings.SaveConfig();
        RefreshGameDirectories();
        DownloadTaskRecord? running = null;
        DownloadTaskRecord? queued = null;
        try
        {
            NavigateToTag("launch");
            Select(first);
            await Task.Delay(200);
            if (CurrentPage is not LaunchPage launch) throw new InvalidOperationException("Launch page is missing.");
            await launch.RunDataSmokeChecksAsync();
            if (((TextBox)launch.FindName("GameDirectoryBox")).Text != first
                || ((ListView)launch.FindName("VersionsList")).Items.Cast<VersionListItem>().Single().DisplayName != "First（1.20.1）")
                throw new InvalidOperationException("Launch page did not load the first directory's version and settings.");

            NavigateToTag("download");
            var download = (DownloadPage)CurrentPage!;
            running = DownloadTaskManager.Current.Enqueue("smoke", "Directory switch smoke", async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unused";
            }, outputDirectory: first);
            queued = DownloadTaskManager.Current.Enqueue("smoke", "Queued directory smoke", (_, _) => Task.FromResult("unused"), outputDirectory: first);
            Select(second);
            await WaitUntilAsync(() => ((ComboBox)download.FindName("InstallTargetVersionBox")).Items.Contains("1.21.1"));
            if (!ReferenceEquals(download, CurrentPage) || ((TextBox)download.FindName("GameDirectoryBox")).Text != second)
                throw new InvalidOperationException("The cached download page did not switch directories.");
            if (running.OutputDirectory != first || queued.OutputDirectory != first)
                throw new InvalidOperationException("Switching directories changed an existing task's destination.");

            NavigateToTag("resources");
            ((ComboBox)CurrentPage!.FindName("ResourceTypeBox")).SelectedIndex = 1;
            if (!((ComboBox)CurrentPage!.FindName("ResourceVersionBox")).Items.Cast<object>().Any(item => item.ToString()!.Contains("1.21.1")))
                throw new InvalidOperationException("Resource targets did not follow the selected directory.");

            NavigateToTag("online");
            var online = (OnlinePage)CurrentPage!;
            Select(first);
            await WaitUntilAsync(() => ((ComboBox)online.FindName("CapsuleVersionBox")).Items.Contains("1.20.1"));
            if (!ReferenceEquals(online, CurrentPage) || ((TextBox)online.FindName("CapsuleGameDirBox")).Text != first)
                throw new InvalidOperationException("The cached online page did not switch directories.");

            NavigateToTag("settings");
            await Task.Delay(200);
            ((SettingsPage)CurrentPage!).RunSmokeChecks();
            var button = (Button)CurrentPage.FindName("SaveAllButton");
            if (button.ActualWidth <= 0 || GlobalDownloadTaskButton.ActualWidth <= 0)
                throw new InvalidOperationException("The settings toolbar has not completed layout.");
            var save = button.TransformToVisual(RootGrid).TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
            var tasks = GlobalDownloadTaskButton.TransformToVisual(RootGrid).TransformBounds(
                new Windows.Foundation.Rect(0, 0, GlobalDownloadTaskButton.ActualWidth, GlobalDownloadTaskButton.ActualHeight));
            if (save.Left < tasks.Right && save.Right > tasks.Left && save.Top < tasks.Bottom && save.Bottom > tasks.Top)
                throw new InvalidOperationException("The global task button overlaps the settings save button.");
        }
        finally
        {
            if (queued is not null) DownloadTaskManager.Current.Cancel(queued.Id);
            if (running is not null) DownloadTaskManager.Current.Cancel(running.Id);
            settings.LoadConfig();
            GameDirectoryCatalog.Select(settings.Config, original);
            GameDirectoryCatalog.Remove(settings.Config, first);
            GameDirectoryCatalog.Remove(settings.Config, second);
            settings.SaveConfig();
            OnGameDirectoryChanged();
        }

        void Select(string path)
        {
            ActiveGameDirectoryBox.SelectedItem = ActiveGameDirectoryBox.Items.Cast<GameDirectoryEntry>()
                .Single(item => GameDirectoryCatalog.SamePath(item.Path, path));
        }

        static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!condition()) await Task.Delay(50, timeout.Token);
        }
    }
}
