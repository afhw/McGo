using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using McGo.Core.App;
using McGo.Core.Downloads;
using McGo.Core.Logging;
using McGo.WinUI.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace McGo.WinUI;

public partial class App : Application
{
    private Window? _window;
    private static string? _smokeReportPath;
    private static string? _smokeScreenshotDirectory;
    private static string? _smokePageTag;
    private static string? _smokeWorkingDirectory;
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var smokePages = commandLine.Contains("--smoke-pages", StringComparer.OrdinalIgnoreCase);
        if (smokePages)
        {
            _smokeReportPath = commandLine
                .FirstOrDefault(value => value.StartsWith("--smoke-report=", StringComparison.OrdinalIgnoreCase))?
                ["--smoke-report=".Length..];
            _smokeScreenshotDirectory = commandLine
                .FirstOrDefault(value => value.StartsWith("--smoke-screenshot-dir=", StringComparison.OrdinalIgnoreCase))?
                ["--smoke-screenshot-dir=".Length..];
            _smokePageTag = commandLine
                .FirstOrDefault(value => value.StartsWith("--smoke-page=", StringComparison.OrdinalIgnoreCase))?
                ["--smoke-page=".Length..];
            if (!string.IsNullOrWhiteSpace(_smokeScreenshotDirectory))
            {
                _smokeScreenshotDirectory = Path.GetFullPath(_smokeScreenshotDirectory);
                Directory.CreateDirectory(_smokeScreenshotDirectory);
            }
            _smokeWorkingDirectory = Path.Combine(Path.GetTempPath(), $"mcgo-page-smoke-{Environment.ProcessId}");
            Directory.CreateDirectory(_smokeWorkingDirectory);
            Directory.SetCurrentDirectory(_smokeWorkingDirectory);
        }
        else
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }

        AppLog.Info("App", $"McGo {AppConstants.AppVersion} 启动，进程 {Environment.ProcessId}，架构 {RuntimeInformation.ProcessArchitecture}。");

        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();

        if (smokePages)
        {
            if (commandLine.Contains("--smoke-global-task", StringComparer.OrdinalIgnoreCase))
            {
                DownloadTaskManager.Current.Enqueue("smoke", "全局下载视觉烟测", async (record, token) =>
                {
                    DownloadTaskManager.Current.Update(record, item =>
                    {
                        item.Progress = 42;
                        item.Detail = "正在验证跨页面任务状态";
                    });
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "不会完成";
                });
            }

            _ = RunPageSmokeAsync((MainWindow)_window);
            return;
        }

        MusicPlaybackService.ApplySavedSettings();
    }

    private static async Task RunPageSmokeAsync(MainWindow window)
    {
        var results = new List<string>();
        var failed = false;
        window.RunSmokeChecks();
        foreach (var size in new[]
                 {
                     new Windows.Graphics.SizeInt32(820, 720),
                     new Windows.Graphics.SizeInt32(1440, 900),
                     new Windows.Graphics.SizeInt32(1920, 1080),
                 })
        {
            window.AppWindow.Resize(size);
            await Task.Delay(200);
            var pageTags = new[] { "home", "launch", "download", "resources", "modpacks", "online", "manage", "accounts", "settings", "logs", "about" };
            foreach (var tag in pageTags.Where(tag => string.IsNullOrWhiteSpace(_smokePageTag) || string.Equals(tag, _smokePageTag, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    window.NavigateToTag(tag);
                    await Task.Delay(string.IsNullOrWhiteSpace(_smokeScreenshotDirectory) ? 150 : 650);
                    switch (window.CurrentPage)
                    {
                        case Pages.HomePage homePage:
                            homePage.RunSmokeChecks();
                            break;
                        case Pages.AccountsPage accountsPage:
                            accountsPage.RunSmokeChecks();
                            break;
                        case Pages.DownloadPage downloadPage:
                            downloadPage.RunSmokeChecks();
                            break;
                        case Pages.LaunchPage launchPage:
                            launchPage.RunSmokeChecks();
                            break;
                        case Pages.ManagePage managePage:
                            managePage.RunSmokeChecks();
                            break;
                        case Pages.ModpacksPage modpacksPage:
                            modpacksPage.RunSmokeChecks();
                            break;
                        case Pages.OnlinePage onlinePage:
                            onlinePage.RunSmokeChecks();
                            break;
                        case Pages.ResourcesPage resourcesPage:
                            resourcesPage.RunSmokeChecks();
                            break;
                        case Pages.SettingsPage settingsPage:
                            settingsPage.RunSmokeChecks();
                            break;
                        case Pages.LogsPage logsPage:
                            logsPage.RunSmokeChecks();
                            break;
                    }

                    if (!string.IsNullOrWhiteSpace(_smokeScreenshotDirectory))
                    {
                        await CaptureSmokeScreenshotAsync(window, size, tag);
                        if (window.CurrentPage is Pages.LaunchPage launchPage)
                        {
                            launchPage.SelectVersionSettingsForSmoke(settings: true);
                            await Task.Delay(150);
                            await CaptureSmokeScreenshotAsync(window, size, "launch-settings");
                            launchPage.SelectVersionSettingsForSmoke(settings: false);
                        }
                        else if (window.CurrentPage is Pages.AccountsPage accountsPage)
                        {
                            foreach (var mode in new[] { "offline", "microsoft", "external" })
                            {
                                accountsPage.SelectLoginModeForSmoke(mode);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "accounts-" + mode);
                            }

                            accountsPage.SelectLoginModeForSmoke("overview");
                        }
                        else if (window.CurrentPage is Pages.OnlinePage onlinePage)
                        {
                            foreach (var section in new[] { "nat", "servers", "capsule" })
                            {
                                onlinePage.SelectOnlineSectionForSmoke(section);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "online-" + section);
                            }

                            onlinePage.SelectOnlineSectionForSmoke("p2p");
                        }
                        else if (window.CurrentPage is Pages.ModpacksPage modpacksPage)
                        {
                            foreach (var section in new[] { "export", "capsule", "crash" })
                            {
                                modpacksPage.SelectModpackSectionForSmoke(section);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "modpacks-" + section);
                            }

                            modpacksPage.SelectModpackSectionForSmoke("import");
                        }
                        else if (window.CurrentPage is Pages.DownloadPage downloadPage)
                        {
                            foreach (var section in new[] { "addons", "tasks" })
                            {
                                downloadPage.SelectDownloadSectionForSmoke(section);
                                await Task.Delay(75);
                                downloadPage.BringDownloadSectionIntoViewForSmoke(section);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "download-" + section);
                            }

                            downloadPage.SelectDownloadSectionForSmoke("vanilla");
                        }
                        else if (window.CurrentPage is Pages.SettingsPage settingsPage)
                        {
                            foreach (var section in new[] { "download", "appearance", "music", "update" })
                            {
                                settingsPage.SelectSettingsSectionForSmoke(section);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "settings-" + section);
                            }

                            settingsPage.SelectSettingsSectionForSmoke("environment");
                        }
                        else if (window.CurrentPage is Pages.ResourcesPage resourcesPage)
                        {
                            resourcesPage.SelectResourceSectionForSmoke("local");
                            await Task.Delay(75);
                            resourcesPage.BringResourceSectionIntoViewForSmoke("local");
                            await Task.Delay(150);
                            await CaptureSmokeScreenshotAsync(window, size, "resources-local");
                            resourcesPage.SelectResourceSectionForSmoke("market");
                        }
                    }

                    results.Add($"PASS {size.Width}x{size.Height} {tag}");
                }
                catch (Exception exc)
                {
                    failed = true;
                    results.Add($"FAIL {size.Width}x{size.Height} {tag}: {exc}");
                }
            }
        }

        WriteSmokeReport(results);
        CleanupSmokeWorkingDirectory();
        Environment.Exit(failed ? 1 : 0);
    }

    private static async Task CaptureSmokeScreenshotAsync(MainWindow window, Windows.Graphics.SizeInt32 size, string tag)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(window.SmokeCaptureRoot);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            throw new InvalidOperationException($"页面 {tag} 截图为空。 ");
        }

        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(_smokeScreenshotDirectory!);
        var file = await folder.CreateFileAsync($"{size.Width}x{size.Height}-{tag}.png", CreationCollisionOption.ReplaceExisting);
        await using var stream = await file.OpenStreamForWriteAsync();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            pixels.ToArray());
        await encoder.FlushAsync();
    }

    private static void CleanupSmokeWorkingDirectory()
    {
        if (string.IsNullOrWhiteSpace(_smokeWorkingDirectory))
        {
            return;
        }

        try
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            Directory.Delete(_smokeWorkingDirectory, recursive: true);
        }
        catch
        {
        }
    }

    private static void WriteSmokeReport(IEnumerable<string> lines)
    {
        if (string.IsNullOrWhiteSpace(_smokeReportPath))
        {
            return;
        }

        var path = Path.GetFullPath(_smokeReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(_smokeReportPath))
        {
            try
            {
                File.AppendAllText(_smokeReportPath, $"FAIL unhandled: {args.Exception}{Environment.NewLine}");
            }
            finally
            {
                CleanupSmokeWorkingDirectory();
                Environment.Exit(1);
            }
        }

        try
        {
            AppLog.Error("App", args.Message, args.Exception);
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "winui-crash.log"),
                $"{DateTimeOffset.Now:O} {args.Message}{Environment.NewLine}{args.Exception}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
