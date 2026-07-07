using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Minecraft;

namespace McGo.WinUI.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        Refresh();
    }

    private void RefreshStatus(object sender, RoutedEventArgs e) => Refresh();

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

    private void Refresh()
    {
        var settings = new AppSettings();
        settings.LoadConfig();
        var gameDir = settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        var versions = new LauncherService().GetLocalVersions(gameDir);
        var accounts = settings.LoadAccounts();
        VersionCountText.Text = versions.Count.ToString();
        AccountCountText.Text = accounts.Count.ToString();
        MemoryText.Text = AppSettings.RecommendedMemoryMb() + " MB";
        SummaryText.Text = $"当前游戏目录：{gameDir}\nWinUI 3 客户端正在接管现有 C# Core 服务。";
    }
}
