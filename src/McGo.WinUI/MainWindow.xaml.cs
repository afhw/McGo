using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.WinUI.Pages;
using Windows.UI;

namespace McGo.WinUI;

public sealed partial class MainWindow : Window
{
    private bool _syncingNavigationSelection;
    private bool _showDownload = true;
    internal Page? CurrentPage => NavFrame.Content as Page;
    internal FrameworkElement SmokeCaptureRoot => RootGrid;

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
        ApplySavedFeatureVisibility();
        DownloadTaskManager.Current.StateChanged += DownloadTasksStateChanged;
        UpdateGlobalDownloadTaskEntry();
        NavFrame.Navigate(typeof(HomePage));
    }

    public void NavigateToTag(string tag)
    {
        var navItem = NavView.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        if (navItem is not null && !ReferenceEquals(NavView.SelectedItem, navItem))
        {
            _syncingNavigationSelection = true;
            NavView.SelectedItem = navItem;
            _syncingNavigationSelection = false;
        }

        NavigateToPage(tag);
    }

    internal void RunSmokeChecks()
    {
        if (NavView.IsSettingsVisible)
        {
            throw new InvalidOperationException("主导航不应显示重复的内置设置项。");
        }

        if (NavView.MenuItems.Count != 5 || NavView.OpenPaneLength > 190)
        {
            throw new InvalidOperationException("主导航没有保持 Python 版的五入口紧凑布局。");
        }

        var manager = DownloadTaskManager.Current;
        var ownsSmokeTask = manager.ActiveRecord is null;
        if (ownsSmokeTask)
        {
            manager.Enqueue("smoke", "全局任务烟测", async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "不会完成";
            });
        }

        try
        {
            if (GlobalDownloadTaskButton.Visibility != Visibility.Visible ||
                !manager.NavigationLabel.StartsWith("下载任务 ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("全局下载任务入口没有随运行任务显示进度。");
            }

            NavigateToPage("download-tasks");
            if (NavFrame.Content is not DownloadPage downloadPage || !downloadPage.IsGlobalTaskCenterOpen)
            {
                throw new InvalidOperationException("全局下载任务入口没有打开任务管理视图。");
            }
        }
        finally
        {
            if (ownsSmokeTask)
            {
                manager.CancelCurrent();
            }
        }
    }

    public void ApplySavedAppearance()
    {
        var settings = new AppSettings();
        settings.LoadConfig();
        ApplyAppearance(
            settings.Config.Get("UI", "theme", "dark"),
            settings.Config.Get("UI", "theme_image", ""));
    }

    public void ApplySavedFeatureVisibility()
    {
        var settings = new AppSettings();
        settings.LoadConfig();
        ApplyFeatureVisibility(
            settings.Config.GetBool("FEATURES", "show_download", true),
            settings.Config.GetBool("FEATURES", "show_manage", true));
    }

    public void ApplyFeatureVisibility(bool showDownload, bool showManage)
    {
        _showDownload = showDownload;
        DownloadNavItem.Visibility = showDownload ? Visibility.Visible : Visibility.Collapsed;
        ManageNavItem.Visibility = showManage ? Visibility.Visible : Visibility.Collapsed;
        UpdateGlobalDownloadTaskEntry();
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
        if (_syncingNavigationSelection)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            NavigateToPage("settings");
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        NavigateToPage(item.Tag?.ToString() ?? "home");
    }

    private void DownloadTasksStateChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateGlobalDownloadTaskEntry();
            return;
        }

        if (!DispatcherQueue.TryEnqueue(UpdateGlobalDownloadTaskEntry))
        {
            UpdateGlobalDownloadTaskEntry();
        }
    }

    private void UpdateGlobalDownloadTaskEntry()
    {
        var manager = DownloadTaskManager.Current;
        GlobalDownloadTaskText.Text = manager.NavigationLabel;
        GlobalDownloadTaskButton.Visibility = _showDownload && manager.HasAttentionRequired
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenGlobalDownloadTasks(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(NavView.SelectedItem, DownloadNavItem))
        {
            _syncingNavigationSelection = true;
            NavView.SelectedItem = DownloadNavItem;
            _syncingNavigationSelection = false;
        }

        NavigateToPage("download-tasks");
    }

    private void NavigateToPage(string tag)
    {
        var openTaskCenter = string.Equals(tag, "download-tasks", StringComparison.OrdinalIgnoreCase);
        var page = tag switch
        {
            "home" => typeof(HomePage),
            "launch" => typeof(LaunchPage),
            "download" => typeof(DownloadPage),
            "download-tasks" => typeof(DownloadPage),
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
        if (NavFrame.CurrentSourcePageType == page)
        {
            if (openTaskCenter && NavFrame.Content is DownloadPage currentDownloadPage)
            {
                currentDownloadPage.OpenGlobalTaskCenter();
            }

            return;
        }

        NavFrame.Navigate(page);
        if (openTaskCenter && NavFrame.Content is DownloadPage downloadPage)
        {
            downloadPage.OpenGlobalTaskCenter();
        }
    }
}
