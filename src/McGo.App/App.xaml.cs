using System.IO;
using System.Windows;

namespace McGo.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        base.OnStartup(e);
    }
}
