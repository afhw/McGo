using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace McGo.WinUI.Pages;

public sealed partial class ManagePage : Page
{
    public ManagePage()
    {
        InitializeComponent();
    }

    private static void Navigate(string tag)
    {
        if (App.MainWindow is MainWindow window)
        {
            window.NavigateToTag(tag);
        }
    }

    private void OpenAccounts(object sender, RoutedEventArgs e) => Navigate("accounts");
    private void OpenSettings(object sender, RoutedEventArgs e) => Navigate("settings");
    private void OpenLogs(object sender, RoutedEventArgs e) => Navigate("logs");
    private void OpenResources(object sender, RoutedEventArgs e) => Navigate("resources");
    private void OpenModpacks(object sender, RoutedEventArgs e) => Navigate("modpacks");
    private void OpenAbout(object sender, RoutedEventArgs e) => Navigate("about");

    private void ResponsiveLayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 900;
        ManageFirstColumn.Width = new GridLength(1, GridUnitType.Star);
        ManageSecondColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ManageThirdColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(EnvironmentCard, wide ? 0 : 1);
        Grid.SetColumn(EnvironmentCard, wide ? 1 : 0);
        Grid.SetRow(DiagnosticsCard, wide ? 0 : 2);
        Grid.SetColumn(DiagnosticsCard, wide ? 2 : 0);
    }

    internal void RunSmokeChecks()
    {
        var wide = ResponsiveLayoutRoot.ActualWidth >= 900;
        if (Grid.GetRow(EnvironmentCard) != (wide ? 0 : 1)
            || Grid.GetColumn(EnvironmentCard) != (wide ? 1 : 0)
            || Grid.GetRow(DiagnosticsCard) != (wide ? 0 : 2))
        {
            throw new InvalidOperationException("管理页响应式布局状态不正确。");
        }
    }
}
