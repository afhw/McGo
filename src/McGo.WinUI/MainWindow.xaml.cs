using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.WinUI.Pages;
using Windows.UI;

namespace McGo.WinUI;

public sealed partial class MainWindow : Window
{
    /// <summary>主导航可见入口数量（首页、启动、下载、资源、整合包、联机）。</summary>
    private const int PrimaryNavigationCount = 6;

    /// <summary>底部导航可见入口数量（账号、设置、日志、关于）。</summary>
    private const int FooterNavigationCount = 4;

    private static readonly string[] KnownTags =
    [
        "home", "launch", "download", "resources", "modpacks", "online",
        "accounts", "settings", "logs", "about",
    ];

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

        PaneVersionText.Text = $"v{AppConstants.AppVersion}";

        ApplyTitleBarButtonColors(ElementTheme.Dark);
        ApplySavedAppearance();
        ApplySavedFeatureVisibility();
        DownloadTaskManager.Current.StateChanged += DownloadTasksStateChanged;
        UpdateGlobalDownloadTaskEntry();
        NavFrame.Navigate(typeof(HomePage));
    }

    public void NavigateToTag(string tag)
    {
        if (!KnownTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            tag = "home";
        }

        SyncNavigationSelection(tag);
        NavigateToPage(tag);
    }

    /// <summary>
    /// 冒烟专用：把游戏目录指到真实数据目录，便于验证已有版本列表等真实场景。
    /// 直接写配置项（配置本身在临时工作目录，可写），不改变页面的其他行为。
    /// </summary>
    internal void ApplySmokeGameDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return;
        }

        var settings = new AppSettings();
        settings.LoadConfig();
        settings.Config.Set("GAME", "directory", gameDirectory);
        settings.SaveConfig();
    }

    internal void RunSmokeChecks()
    {
        if (NavView.IsSettingsVisible)
        {
            throw new InvalidOperationException("主导航不应显示重复的内置设置项。");
        }

        if (NavView.MenuItems.Count != PrimaryNavigationCount || NavView.FooterMenuItems.Count != FooterNavigationCount)
        {
            throw new InvalidOperationException("主导航没有保持主区六个入口加底部四个辅助入口的信息布局。");
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
        ApplyFeatureVisibility(settings.Config.GetBool("FEATURES", "show_download", true));
    }

    public void ApplyFeatureVisibility(bool showDownload)
    {
        _showDownload = showDownload;
        DownloadNavItem.Visibility = showDownload ? Visibility.Visible : Visibility.Collapsed;
        UpdateGlobalDownloadTaskEntry();
    }

    /// <summary>
    /// 当前生效的窗口主题。
    /// ContentDialog 的内容会被重新挂到 Popup 下，脱离了 RootGrid 的继承链，
    /// 不显式跟随就会出现"深色界面弹白色对话框"，因此弹出前要以它为准。
    /// </summary>
    internal ElementTheme CurrentTheme => RootGrid.RequestedTheme;

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
        SyncNavigationSelection("download");
        NavigateToPage("download-tasks");
    }

    /// <summary>让导航选中状态跟随当前页面标签。</summary>
    private void SyncNavigationSelection(string tag)
    {
        var target = FindNavigationItem(tag);
        if (target is null || ReferenceEquals(NavView.SelectedItem, target))
        {
            return;
        }

        _syncingNavigationSelection = true;
        try
        {
            NavView.SelectedItem = target;
        }
        finally
        {
            _syncingNavigationSelection = false;
        }
    }

    private NavigationViewItem? FindNavigationItem(string tag)
    {
        return NavView.MenuItems.Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
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
