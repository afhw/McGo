using System.Diagnostics;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace McGo.WinUI.Pages;

public sealed partial class LogsPage : Page
{
    private readonly Dictionary<string, LogFileItem> _logByLabel = [];
    private CancellationTokenSource? _readCancellation;
    private bool _loadingLogs;
    private bool _loadedOnce;

    public LogsPage()
    {
        InitializeComponent();
        Loaded += LogsPageLoaded;
    }

    private async void LogsPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        await RefreshLogListAsync(loadLatest: true);
    }

    private async void RefreshLogs(object sender, RoutedEventArgs e) => await RefreshLogListAsync(loadLatest: true);

    private async void ReloadSelectedLog(object sender, RoutedEventArgs e) => await LoadSelectedLogAsync();

    private void ClearLogOutput(object sender, RoutedEventArgs e)
    {
        LogOutputBox.Text = "";
        LogStatusText.Text = "日志显示已清空。";
    }

    private void OpenLogsFolder(object sender, RoutedEventArgs e)
    {
        var directory = SelectedLog() is { } selected
            ? Path.GetDirectoryName(selected.Path)!
            : AppLog.DirectoryPath;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        LogStatusText.Text = "已打开日志目录：" + directory;
    }

    private async void LogFileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingLogs)
        {
            await LoadSelectedLogAsync();
        }
    }

    private async Task RefreshLogListAsync(bool loadLatest)
    {
        var selectedPath = SelectedLog()?.Path;
        _loadingLogs = true;
        _logByLabel.Clear();
        var files = new List<LogFileItem>();
        foreach (var source in FindLogSources())
        {
            try
            {
                files.AddRange(LogFileReader.Enumerate(source.Directory)
                    .Select(path => new LogFileItem(source.Name, path)));
            }
            catch (Exception exc)
            {
                AppLog.Warning("Logs", $"扫描日志目录失败：{source.Directory}；{exc.Message}");
            }
        }

        files = files
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => File.GetLastWriteTimeUtc(item.Path))
            .ToList();
        var labels = new List<string>();
        foreach (var item in files)
        {
            var info = new FileInfo(item.Path);
            var label = $"{item.Source} / {info.Name}    {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}    {FormatSize(info.Length)}";
            labels.Add(label);
            _logByLabel[label] = item;
        }

        LogFilesBox.ItemsSource = labels;
        if (labels.Count > 0)
        {
            var selectedLabel = selectedPath is null
                ? null
                : _logByLabel.FirstOrDefault(pair => string.Equals(pair.Value.Path, selectedPath, StringComparison.OrdinalIgnoreCase)).Key;
            LogFilesBox.SelectedItem = selectedLabel ?? labels[0];
        }

        _loadingLogs = false;
        LogStatusText.Text = labels.Count == 0
            ? "没有找到日志文件。McGo 运行后会在 logs 目录生成日志。"
            : $"已发现 {labels.Count} 个日志文件。";
        if (loadLatest && labels.Count > 0)
        {
            await LoadSelectedLogAsync();
        }
        else if (labels.Count == 0)
        {
            LogOutputBox.Text = "没有找到日志文件。";
        }
    }

    private async Task LoadSelectedLogAsync()
    {
        var selected = SelectedLog();
        if (selected is null)
        {
            LogOutputBox.Text = "请先选择日志文件。";
            return;
        }

        _readCancellation?.Cancel();
        _readCancellation?.Dispose();
        _readCancellation = new CancellationTokenSource();
        var cancellationToken = _readCancellation.Token;
        LogStatusText.Text = "正在读取：" + selected.Path;
        try
        {
            var result = await LogFileReader.ReadTailAsync(selected.Path, cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            LogOutputBox.Text = result.IsTruncated
                ? "[文件较大，仅显示末尾 2 MB]" + Environment.NewLine + result.Text
                : result.Text;
            LogOutputBox.SelectionStart = LogOutputBox.Text.Length;
            LogStatusText.Text = $"{selected.Source} / {Path.GetFileName(selected.Path)}    {FormatSize(result.FileSize)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exc)
        {
            LogOutputBox.Text = "读取日志失败：" + exc.Message;
            LogStatusText.Text = "读取日志失败。";
            AppLog.Error("Logs", "读取日志失败：" + selected.Path, exc);
        }
    }

    public void RunSmokeChecks()
    {
        if (LogFilesBox.Items.Count == 0 || LogFilesBox.SelectedItem is null)
        {
            throw new InvalidOperationException("日志页未发现或未选中应用日志。");
        }

        if (string.IsNullOrWhiteSpace(LogOutputBox.Text))
        {
            throw new InvalidOperationException("日志页未自动显示最新日志内容。");
        }
    }

    private LogFileItem? SelectedLog()
        => LogFilesBox.SelectedItem is string label && _logByLabel.TryGetValue(label, out var item) ? item : null;

    private static IReadOnlyList<LogSource> FindLogSources()
    {
        var sources = new List<LogSource> { new("McGo", AppLog.DirectoryPath) };
        AddGameLogSource(sources, Path.Combine(AppContext.BaseDirectory, AppConstants.ConfigFile));

        var developmentRoot = FindDevelopmentRoot();
        if (developmentRoot is not null)
        {
            sources.Add(new LogSource("旧版 McGo", Path.Combine(developmentRoot, "logs")));
            AddGameLogSource(sources, Path.Combine(developmentRoot, AppConstants.ConfigFile));
        }

        return sources
            .Select(source => source with { Directory = Path.GetFullPath(source.Directory) })
            .DistinctBy(source => source.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddGameLogSource(ICollection<LogSource> sources, string configPath)
    {
        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            var config = IniDocument.Load(configPath);
            var gameDirectory = config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
            if (!Path.IsPathRooted(gameDirectory))
            {
                gameDirectory = Path.Combine(Path.GetDirectoryName(configPath)!, gameDirectory);
            }

            sources.Add(new LogSource("Minecraft", Path.Combine(gameDirectory, "logs")));
        }
        catch (Exception exc)
        {
            AppLog.Warning("Logs", $"读取游戏目录配置失败：{configPath}；{exc.Message}");
        }
    }

    private static string? FindDevelopmentRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) &&
                File.Exists(Path.Combine(current.FullName, AppConstants.ConfigFile)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:F1} MB"
            : $"{bytes / 1024d:F1} KB";

    private sealed record LogSource(string Name, string Directory);
    private sealed record LogFileItem(string Source, string Path);
}
