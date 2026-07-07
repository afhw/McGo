using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using McGo.Core.Configuration;
using McGo.WinUI.Pages;
using Windows.UI;

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

        ApplyTitleBarButtonColors(ElementTheme.Dark);
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
        var requestedTheme = (theme ?? "").Trim().ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "auto" => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
        RootGrid.RequestedTheme = requestedTheme;
        ApplyTitleBarButtonColors(requestedTheme);

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

    private void ApplyTitleBarButtonColors(ElementTheme theme)
    {
        var titleBar = AppWindow.TitleBar;
        var isLight = theme == ElementTheme.Light;
        titleBar.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.InactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonForegroundColor = isLight ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 255, 255, 255);
        titleBar.ButtonInactiveForegroundColor = isLight ? Color.FromArgb(150, 32, 32, 32) : Color.FromArgb(170, 255, 255, 255);
        titleBar.ButtonHoverForegroundColor = isLight ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
        titleBar.ButtonPressedForegroundColor = isLight ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
        titleBar.ButtonHoverBackgroundColor = isLight ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = isLight ? Color.FromArgb(45, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255);
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
