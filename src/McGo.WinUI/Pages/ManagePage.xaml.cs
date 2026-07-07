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
}
