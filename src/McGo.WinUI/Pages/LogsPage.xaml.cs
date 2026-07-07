using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace McGo.WinUI.Pages;

public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        InitializeComponent();
    }

    private void LoadLatestLog(object sender, RoutedEventArgs e)
    {
        var logsDir = Path.Combine(Environment.CurrentDirectory, "logs");
        if (!Directory.Exists(logsDir))
        {
            LogOutputBox.Text = "logs 目录不存在。";
            return;
        }

        var latest = Directory.GetFiles(logsDir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        LogOutputBox.Text = latest is null ? "没有找到日志文件。" : File.ReadAllText(latest);
    }
}
