using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Java;
using McGo.Core.Minecraft;
using McGo.Core.Personalization;

namespace McGo.WinUI.Pages;

public sealed partial class HomePage : Page
{
    private readonly HomeContentService _homeContent = new();
    private int _refreshRequest;

    public HomePage()
    {
        InitializeComponent();
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

    private void OpenManage(object sender, RoutedEventArgs e) => Navigate("manage");
    private void OpenDownload(object sender, RoutedEventArgs e) => Navigate("download");
    private void OpenLaunch(object sender, RoutedEventArgs e) => Navigate("launch");
    private void OpenOnline(object sender, RoutedEventArgs e) => Navigate("online");

    private async Task RefreshAsync()
    {
        var request = ++_refreshRequest;
        JavaStatusText.Text = "Java：正在检测...";
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

        VersionCountText.Text = snapshot.VersionCount.ToString();
        AccountCountText.Text = snapshot.AccountCount.ToString();
        MemoryText.Text = snapshot.MemoryText;
        GameDirectoryText.Text = "游戏目录：" + snapshot.GameDirectory;
        CurrentAccountText.Text = snapshot.CurrentAccountText;
        JavaStatusText.Text = snapshot.JavaStatusText;
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
        var gameDirectory = settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        var versions = new LauncherService().GetLocalVersions(gameDirectory);
        var accounts = settings.LoadAccounts();
        var selectedId = settings.Config.Get("ACCOUNTS", "selected_account_id", "");
        var selectedAccount = accounts.FirstOrDefault(account => account["id"]?.GetValue<string>() == selectedId);
        var accountText = selectedAccount is null
            ? "当前账号：未选择"
            : "当前账号：" + (selectedAccount["display_name"]?.GetValue<string>() ?? selectedAccount["username"]?.GetValue<string>() ?? "未命名");
        var javaPath = settings.Config.Get("JAVA", "path", "java");
        var javaMajor = JavaRuntimeService.GetJavaMajorVersion(javaPath);
        return new HomeStatusSnapshot(
            gameDirectory,
            versions.Count,
            accounts.Count,
            AppSettings.RecommendedMemoryMb() + " MB",
            accountText,
            javaMajor > 0 ? $"Java：{javaMajor} ({javaPath})" : $"Java：未识别 ({javaPath})",
            settings.Config.Get("HOME", "content_source", "").Trim(),
            settings.Config.GetBool("HOME", "allow_network", false));
    }

    private sealed record HomeStatusSnapshot(
        string GameDirectory,
        int VersionCount,
        int AccountCount,
        string MemoryText,
        string CurrentAccountText,
        string JavaStatusText,
        string ContentSource,
        bool AllowNetwork);

    private void ResponsiveLayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 900;
        HomePrimaryColumn.Width = new GridLength(wide ? 1.2 : 1, GridUnitType.Star);
        HomeSecondaryColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(CurrentStatusCard, wide ? 1 : 0);
        Grid.SetRow(CurrentStatusCard, wide ? 0 : 1);
    }

    internal void RunSmokeChecks()
    {
        var wide = ResponsiveLayoutRoot.ActualWidth >= 900;
        if (Grid.GetRow(CurrentStatusCard) != (wide ? 0 : 1) || Grid.GetColumn(CurrentStatusCard) != (wide ? 1 : 0))
        {
            throw new InvalidOperationException("主页响应式布局状态不正确。");
        }
    }
}
