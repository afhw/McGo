using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;

namespace McGo.WinUI.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = "版本 " + AppConstants.AppVersion;
    }
}
