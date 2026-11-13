using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Java;
using McGo.Core.Minecraft;
using McGo.Core.Personalization;

namespace McGo.WinUI.Pages;

/// <summary>
/// 首页：第一屏回答“现在能不能开游戏”，其次才是状态和常用入口。
/// 布局参考 PCL 启动页——左侧主操作，右侧状态与版本信息；窄窗口下堆叠为单列。
/// </summary>
public sealed partial class HomePage : Page
{
    private readonly HomeContentService _homeContent = new();
    private int _refreshRequest;
    private bool _wideLayout;
    private bool _hasVersion;
    private bool _hasAccount;
    private Task _refreshTask = Task.CompletedTask;

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HomePage_Loaded;
        ApplyResponsiveLayout(ResponsiveLayoutRoot.ActualWidth);
        _refreshTask = RefreshAsync();
    }

    private async void RefreshStatus(object sender, RoutedEventArgs e)
    {
        _refreshTask = RefreshAsync();
        await _refreshTask;
    }

    private static void Navigate(string tag)
    {
        if (App.MainWindow is MainWindow window)
        {
            window.NavigateToTag(tag);
        }
    }

    private void OpenDownload(object sender, RoutedEventArgs e) => Navigate("download");
    private void OpenOnline(object sender, RoutedEventArgs e) => Navigate("online");
    private void OpenResources(object sender, RoutedEventArgs e) => Navigate("resources");
    private void OpenSettings(object sender, RoutedEventArgs e) => Navigate("settings");

    /// <summary>
    /// 主按钮只做“现在最该做的那件事”：缺账号先建账号，账号齐了才进启动页。
    /// 这样从首页第一步就不会走错。
    /// </summary>
    private void PrimaryAction(object sender, RoutedEventArgs e) => Navigate(_hasAccount ? "launch" : "accounts");

    /// <summary>次按钮补上当前还缺的那一步，不与主按钮重复。</summary>
    private void SecondaryAction(object sender, RoutedEventArgs e) => Navigate(_hasVersion ? "launch" : "download");

    private async Task RefreshAsync()
    {
        var request = ++_refreshRequest;
        JavaStatusText.Text = "正在检测 Java…";
        HomeStatusSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(CaptureStatusSnapshot);
        }
        catch (Exception exc)
        {
            if (request == _refreshRequest)
            {
                JavaStatusText.Text = "状态检测失败";
                SummaryText.Text = "刷新主页状态失败：" + exc.Message;
            }

            return;
        }

        if (request != _refreshRequest)
        {
            return;
        }

        _hasVersion = snapshot.VersionCount > 0;
        _hasAccount = snapshot.AccountName != "未选择";
        VersionCountText.Text = snapshot.VersionCount.ToString();
        AccountCountText.Text = snapshot.AccountCount.ToString();
        MemoryText.Text = snapshot.MemoryText;
        GameDirectoryText.Text = snapshot.GameDirectory;
        AccountChipText.Text = snapshot.AccountName == "未选择" ? "未登录账号" : snapshot.AccountName;
        ChipsSummaryText.Text = $"{AccountChipText.Text} · {snapshot.FirstVersion ?? "尚未安装版本"} · {snapshot.JavaStatusText}";
        JavaChipText.Text = snapshot.JavaStatusText;
        JavaStatusText.Text = snapshot.JavaStatusText;
        FirstVersionText.Text = snapshot.FirstVersion ?? "尚未安装版本";
        VersionHintText.Text = snapshot.VersionCount == 0
            ? "先下载一个游戏版本"
            : $"共 {snapshot.VersionCount} 个本地版本";
        LaunchHintText.Text = snapshot.LaunchHintText;
        PrimaryButtonText.Text = _hasAccount ? "进入启动页" : "先添加账号";
        PrimaryButtonIcon.Symbol = _hasAccount ? Symbol.Play : Symbol.Contact;
        SecondaryButtonText.Text = _hasVersion ? "管理版本" : "去下载版本";
        SecondaryButtonIcon.Symbol = _hasVersion ? Symbol.Library : Symbol.Download;
        ReadinessText.Text = snapshot.ReadinessText;

        CustomHomeCard.Visibility = snapshot.ContentSource.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (snapshot.ContentSource.Length == 0)
        {
            SummaryText.Text = "";
            return;
        }

        try
        {
            var result = await _homeContent.LoadContentAsync(snapshot.ContentSource, snapshot.AllowNetwork);
            if (request == _refreshRequest)
            {
                SummaryText.Text = result.Text;
            }
        }
        catch (Exception exc)
        {
            if (request == _refreshRequest)
            {
                SummaryText.Text = "加载自定义主页失败：" + exc.Message;
            }
        }
    }

    private static HomeStatusSnapshot CaptureStatusSnapshot()
    {
        var settings = new AppSettings();
        settings.LoadConfig(persistChanges: false);
        var gameDirectory = settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        var versions = new LauncherService().GetLocalVersions(gameDirectory);
        var accounts = settings.LoadAccounts(persistChanges: false);
        var selectedId = settings.Config.Get("ACCOUNTS", "selected_account_id", "");
        var selectedAccount = accounts.FirstOrDefault(account => account["id"]?.GetValue<string>() == selectedId) ?? accounts.FirstOrDefault();
        var accountName = selectedAccount is null
            ? "未选择"
            : selectedAccount["display_name"]?.GetValue<string>()
              ?? selectedAccount["username"]?.GetValue<string>()
              ?? "未命名";
        var javaPath = settings.Config.Get("JAVA", "path", "java");
        var autoJava = settings.Config.GetBool("JAVA", "auto_select", true);
        var javaMajor = autoJava ? 0 : JavaRuntimeService.GetJavaMajorVersion(javaPath);
        var firstVersion = versions.Count == 0 ? null : versions[0];

        var readiness = (versions.Count, accountName, javaMajor) switch
        {
            (0, _, _) => "缺少游戏版本",
            (_, "未选择", _) => "缺少账号",
            (_, _, _) when autoJava => "可选择版本启动",
            (_, _, <= 0) => "缺少 Java",
            _ => "可选择版本启动",
        };
        var launchHint = (versions.Count, accountName, javaMajor) switch
        {
            (0, _, _) => "还没有本地版本。先到“下载”安装一个，再回来启动。",
            (_, "未选择", _) => "还没有选择账号。先添加离线或 Microsoft 账号。",
            (_, _, _) when autoJava => "选择游戏版本后自动匹配 Java。",
            (_, _, <= 0) => "未识别到 Java。启动页可以检测或下载推荐运行时。",
            _ => $"{accountName} · {firstVersion} · Java {javaMajor}，随时可以开始游戏。",
        };

        return new HomeStatusSnapshot(
            gameDirectory,
            versions.Count,
            accounts.Count,
            AppSettings.RecommendedMemoryMb() + " MB",
            accountName,
            firstVersion,
            autoJava ? "Java 自动匹配" : javaMajor > 0 ? $"Java {javaMajor}" : "Java 未识别",
            readiness,
            launchHint,
            settings.Config.Get("HOME", "content_source", "").Trim(),
            settings.Config.GetBool("HOME", "allow_network", false));
    }

    private sealed record HomeStatusSnapshot(
        string GameDirectory,
        int VersionCount,
        int AccountCount,
        string MemoryText,
        string AccountName,
        string? FirstVersion,
        string JavaStatusText,
        string ReadinessText,
        string LaunchHintText,
        string ContentSource,
        bool AllowNetwork);

    private void ResponsiveLayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width)
        {
            ApplyResponsiveLayout(e.NewSize.Width);
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var contentWidth = width - ResponsiveLayoutRoot.Padding.Left - ResponsiveLayoutRoot.Padding.Right;
        var wide = contentWidth >= 900;
        _wideLayout = wide;
        RefreshStatusLabel.Visibility = contentWidth >= 480 ? Visibility.Visible : Visibility.Collapsed;
        HomeContentGrid.ColumnSpacing = wide ? 16 : 0;
        var rows = wide ? 3 : 4;
        while (HomeContentGrid.RowDefinitions.Count < rows)
            HomeContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (HomeContentGrid.RowDefinitions.Count > rows)
            HomeContentGrid.RowDefinitions.RemoveAt(HomeContentGrid.RowDefinitions.Count - 1);
        HomePrimaryColumn.Width = wide ? new GridLength(1.3, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        HomeSecondaryColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(StatusCard, wide ? 0 : 1);
        Grid.SetColumn(StatusCard, wide ? 1 : 0);
        Grid.SetRow(QuickLinksCard, wide ? 1 : 2);
        Grid.SetColumn(QuickLinksCard, 0);
        Grid.SetColumnSpan(QuickLinksCard, wide ? 2 : 1);
        Grid.SetRow(CustomHomeCard, wide ? 2 : 3);
        Grid.SetColumnSpan(CustomHomeCard, wide ? 2 : 1);
    }

    private void HomeActionsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Width != e.PreviousSize.Width)
            ArrangeGrid(HomeActionsGrid, e.NewSize.Width >= 360 ? 2 : 1);
    }

    private void HomeStatsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Width == e.PreviousSize.Width) return;
        var columns = e.NewSize.Width >= 360 ? 3 : 2;
        ArrangeGrid(HomeStatsGrid, columns);
        Grid.SetColumnSpan((FrameworkElement)HomeStatsGrid.Children[2], columns == 2 ? 2 : 1);
    }

    private void QuickLinksGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Width != e.PreviousSize.Width)
            ArrangeGrid(QuickLinksGrid, e.NewSize.Width >= 880 ? 4 : e.NewSize.Width >= 440 ? 2 : 1);
    }

    private static void ArrangeGrid(Grid grid, int columns)
    {
        while (grid.ColumnDefinitions.Count < columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition());
        while (grid.ColumnDefinitions.Count > columns)
            grid.ColumnDefinitions.RemoveAt(grid.ColumnDefinitions.Count - 1);
        foreach (var column in grid.ColumnDefinitions)
            column.Width = new GridLength(1, GridUnitType.Star);

        var children = grid.Children.Cast<FrameworkElement>().ToArray();
        var rows = (children.Length + columns - 1) / columns;
        while (grid.RowDefinitions.Count < rows)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        while (grid.RowDefinitions.Count > rows)
            grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        for (var index = 0; index < children.Length; index++)
        {
            Grid.SetRow(children[index], index / columns);
            Grid.SetColumn(children[index], index % columns);
        }
    }

    internal void RunSmokeChecks()
    {
        var width = ResponsiveLayoutRoot.ActualWidth;
        var wide = width - ResponsiveLayoutRoot.Padding.Left - ResponsiveLayoutRoot.Padding.Right >= 900;
        if (width <= 0 || width > HomeScrollViewer.ActualWidth + 1)
            throw new InvalidOperationException("主页内容宽度超出实际视口或尚未完成布局。");
        if (wide != _wideLayout
            || Grid.GetRow(StatusCard) != (wide ? 0 : 1)
            || Grid.GetColumn(StatusCard) != (wide ? 1 : 0)
            || Grid.GetRow(QuickLinksCard) != (wide ? 1 : 2)
            || Grid.GetRow(CustomHomeCard) != (wide ? 2 : 3)
            || Grid.GetColumnSpan(CustomHomeCard) != (wide ? 2 : 1))
        {
            throw new InvalidOperationException("主页响应式布局状态不正确。");
        }

        if (PrimaryLaunchButton is null || SecondaryLaunchButton is null)
        {
            throw new InvalidOperationException("主页缺少主操作按钮。");
        }
    }

    internal async Task RunDataSmokeChecksAsync()
    {
        await _refreshTask.WaitAsync(TimeSpan.FromSeconds(30));
        var settings = new AppSettings();
        settings.LoadConfig(persistChanges: false);
        if (GameDirectoryText.Text != settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory))
            throw new InvalidOperationException("首页未使用当前游戏目录。");
        var directory = GameDirectoryText.Text;
        var expected = await Task.Run(() => new LauncherService().GetLocalVersions(directory).Count);
        if (VersionCountText.Text != expected.ToString())
            throw new InvalidOperationException("首页版本数量与当前游戏目录不一致。");
        if (JavaStatusText.Text != JavaChipText.Text || JavaStatusText.Text.Contains("正在检测", StringComparison.Ordinal))
            throw new InvalidOperationException("首页 Java 状态未完成刷新。");
    }
}
