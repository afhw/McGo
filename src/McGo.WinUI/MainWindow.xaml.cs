using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using McGo.Core.Configuration;
using McGo.WinUI.Pages;

namespace McGo.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        ApplySavedAppearance();
        NavFrame.Navigate(typeof(HomePage));
    }

    public void NavigateToTag(string tag)
    {
        NavigateToPage(tag);
    }

    public void ApplySavedAppearance()
    {
        var settings = new AppSettings();
        settings.LoadConfig();
        ApplyAppearance(
            settings.Config.Get("UI", "theme", "dark"),
            settings.Config.Get("UI", "theme_image", ""));
    }

    public void ApplyAppearance(string theme, string imagePath)
    {
        RootGrid.RequestedTheme = (theme ?? "").Trim().ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "auto" => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };

        var path = Environment.ExpandEnvironmentVariables((imagePath ?? "").Trim());
        if (path.Length == 0 || !File.Exists(path))
        {
            ThemeBackgroundImage.Source = null;
            ThemeBackgroundImage.Visibility = Visibility.Collapsed;
            return;
        }

        ThemeBackgroundImage.Source = new BitmapImage(new Uri(Path.GetFullPath(path)));
        ThemeBackgroundImage.Visibility = Visibility.Visible;
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        NavigateToPage(item.Tag?.ToString() ?? "home");
    }

    private void NavigateToPage(string tag)
    {
        var page = tag switch
        {
            "home" => typeof(HomePage),
            "launch" => typeof(LaunchPage),
            "download" => typeof(DownloadPage),
            "accounts" => typeof(AccountsPage),
            "resources" => typeof(ResourcesPage),
            "modpacks" => typeof(ModpacksPage),
            "online" => typeof(OnlinePage),
            "manage" => typeof(ManagePage),
            "settings" => typeof(SettingsPage),
            "logs" => typeof(LogsPage),
            "about" => typeof(AboutPage),
            _ => typeof(HomePage),
        };
        NavFrame.Navigate(page);
    }
}
