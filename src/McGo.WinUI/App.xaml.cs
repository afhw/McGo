using Microsoft.UI.Xaml;
using System.IO;
using McGo.WinUI.Services;

namespace McGo.WinUI;

public partial class App : Application
{
    private Window? _window;
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        _window = new MainWindow();
        MainWindow = _window;
        MusicPlaybackService.ApplySavedSettings();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        try
        {
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
