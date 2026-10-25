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

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HomePage_Loaded;
        ApplyResponsiveLayout(ActualWidth);
        SizeChanged += (_, args) => ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : args.NewSize.Width);
        _ = RefreshAsync();
    }

    private async void RefreshStatus(object sender, RoutedEventArgs e) => await RefreshAsync();

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
    private void SecondaryAction(object sender, RoutedEventArgs e) => Navigate("download");

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
        settings.LoadConfig();
        var gameDirectory = settings.Config.Get("GpME", "directory", AppConstants.DefaultGameDirectory);
        var versions = new LauncherService().GetLocalVersions(gameDirectory);
        var accounts = settings.LoadAccounts();
        var selectedId = settings.Config.Get("ACCOUNTS", "selected_account_id", "");
        var selectedAccount = accounts.FirstOrDefault(account => account["id"]?.GetValue<string>() == selectedId);
        var accountName = selectedAccount is null
            ? "未选择"
            : selectedAccount["display_name"]?.GetValue<string>()
              ?? selectedAccount["username"]?.GetValue<string>()
              ?? "未命名";
        var javaPath = settings.Config.Get("JAVA", "path", "java");
        var javaMajor = JavaRuntimeService.GetJavaMajorVersion(javaPath);
        var firstVersion = versions.Count == 0 ? null : versions[0];

        var readiness = (versions.Count, accountName, javaMajor) switch
        {
            (0, _, _) => "缺少游戏版本",
            (_, "未选择", _) => "缺少账号",
            (_, _, <= 0) => "缺少 Java",
            _ => "可以启动",
        };
        var launchHint = (versions.Count, accountName, javaMajor) switch
        {
            (0, _, _) => "还没有本地版本。先到“下载”安装一个，再回来启动。",
            (_, "未选择", _) => "还没有选择账号。先添加离线或 Microsoft 账号。",
            (_, _, <= 0) => "未识别到 Java。启动页可以一键检测或下载推荐运行时。",
            _ => $"{accountName} · {firstVersion} · Java {javaMajor}，随时可以开始游戏。",
        };

        return new HomeStatusSnapshot(
            gameDirectory,
            versions.Count,
            accounts.Count,
            AppSettings.RecommendedMemoryMb() + " MB",
            accountName,
            firstVersion,
            javaMajor > 0 ? $"Java {javaMajor}" : "Java 未识别",
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
        => ApplyResponsiveLayout(e.NewSize.Width);

    /// <summary>窄窗口单列堆叠，宽窗口左右分栏；主操作卡片始终排在最前。</summary>
    /// <summary>视口宽度：XamlRoot.Size 是真实客户区（DIP），容器宽度可能按内容收缩。</summary>
    private double ViewportWidth
    {
        get
        {
            var root = XamlRoot?.Size.Width ?? 0;
            return root > 0 ? root : (ActualWidth > 0 ? ActualWidth : 0);
        }
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var wide = width >= 900;
        _wideLayout = wide;
        HomePrimaryColumn.Width = wide ? new GridLength(1.3, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        HomeSecondaryColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(StatusCard, wide ? 0 : 1);
        Grid.SetColumn(StatusCard, wide ? 1 : 0);
        Grid.SetRow(QuickLinksCard, wide ? 1 : 2);
        Grid.SetColumn(QuickLinksCard, 0);
        Grid.SetColumnSpan(QuickLinksCard, wide ? 2 : 1);
    }

    internal void RunSmokeChecks()
    {
        // 冒烟检查可能在测量完成前触发，先按当前视口重新应用一次布局再断言。
        var wide = ViewportWidth >= 900;
        ApplyResponsiveLayout(ViewportWidth);
        if (wide != _wideLayout
            || Grid.GetRow(StatusCard) != (wide ? 0 : 1)
            || Grid.GetColumn(StatusCard) != (wide ? 1 : 0)
            || Grid.GetRow(QuickLinksCard) != (wide ? 1 : 2))
        {
            throw new InvalidOperationException("主页响应式布局状态不正确。");
        }

        if (PrimaryLaunchButton is null || SecondaryLaunchButton is null)
        {
            throw new InvalidOperationException("主页缺少主操作按钮。");
        }
    }
}
