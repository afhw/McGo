using System.Text.Json.Nodes;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using McGo.Core.App;
using McGo.Core.Configuration;
using McGo.Core.Downloads;
using McGo.Core.Formatting;
using McGo.Core.Installation;

namespace McGo.WinUI.Pages;

public sealed partial class DownloadPage : Page
{
    private readonly AppSettings _settings = new();
    private readonly InstallServices _installServices = new();
    private readonly DownloaderService _downloader = new();
    private readonly Queue<DownloadWorkItem> _downloadQueue = new();
    private readonly List<DownloadTaskRecord> _taskRecords = [];
    private DownloadWorkItem? _activeTask;
    private DownloadWorkItem? _lastFailedTask;
    private CancellationTokenSource? _activeCts;
    private bool _isDispatching;
    private bool _cancelRequested;
    private int _nextTaskId = 1;

    public DownloadPage()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        _settings.LoadConfig();
        GameDirectoryBox.Text = _settings.Config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);
        JavaPathBox.Text = _settings.Config.Get("JAVA", "path", "java");
        MirrorBox.SelectedIndex = _settings.Config.Get("DOWNLOAD", "mirror_source", "official") == "bmclapi" ? 1 : 0;
    }

    private static void Navigate(string tag)
    {
        if (App.MainWindow is MainWindow window)
        {
            window.NavigateToTag(tag);
        }
    }

    private void StayOnDownloads(object sender, RoutedEventArgs e) => AppendDownload("已停留在下载任务页。");
    private void OpenResources(object sender, RoutedEventArgs e) => Navigate("resources");
    private void OpenModpacks(object sender, RoutedEventArgs e) => Navigate("modpacks");

    private async void RefreshRemoteVersions(object sender, RoutedEventArgs e)
    {
        try
        {
            DownloadButton.IsEnabled = false;
            AppendDownload("正在刷新远程版本...");
            var versions = await _installServices.GetRemoteVersionsAsync(ComboText(VersionTypeBox, "release"), ComboText(MirrorBox, "official"));
            RemoteVersionsBox.ItemsSource = versions;
            if (versions.Count > 0)
            {
                RemoteVersionsBox.SelectedIndex = 0;
            }

            AppendDownload($"已获取 {versions.Count} 个远程版本。");
        }
        catch (Exception exc)
        {
            AppendDownload("刷新远程版本失败：" + exc.Message);
        }
        finally
        {
            DownloadButton.IsEnabled = true;
        }
    }

    private async void DownloadSelectedVersion(object sender, RoutedEventArgs e)
    {
        var versionId = RemoteVersionsBox.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(versionId))
        {
            AppendDownload("请先选择远程版本。");
            return;
        }

        var mirror = ComboText(MirrorBox, "official");
        var gameDir = GameDirectoryBox.Text.Trim();
        var javaPath = JavaPathBox.Text.Trim();
        var installTypes = SelectedInstallTypes();
        if (installTypes.Any(type => type is "forge" or "neoforge") && string.IsNullOrWhiteSpace(javaPath))
        {
            AppendDownload("自动安装 Forge 或 NeoForge 需要可用的 Java 路径。");
            return;
        }

        _settings.Config.Set("GAME", "directory", gameDir);
        _settings.Config.Set("JAVA", "path", javaPath);
        _settings.Config.Set("DOWNLOAD", "mirror_source", mirror);
        _settings.SaveConfig();

        var title = installTypes.Count == 0
            ? $"下载 {versionId}"
            : $"下载 {versionId} 并安装 {string.Join(" + ", installTypes.Select(InstallTypeLabel))}";
        var task = new DownloadWorkItem(
            _nextTaskId++,
            "download",
            title,
            (record, token) => ExecuteDownloadTaskAsync(record, versionId, mirror, gameDir, javaPath, [.. installTypes], token));
        QueueDownloadTask(task);
        await DispatchDownloadQueueAsync();
    }

    private async Task<string> ExecuteDownloadTaskAsync(
        DownloadTaskRecord record,
        string versionId,
        string mirror,
        string gameDir,
        string javaPath,
        IReadOnlyList<string> installTypes,
        CancellationToken cancellationToken)
    {
        ResetTaskSpeed(record);
        AppendDownload($"正在下载 {versionId}...");
        var (metadata, usedMirror) = await _installServices.GetVersionMetadataWithFallbackAsync(
            versionId,
            mirror,
            message => UpdateFromAnyThread(() =>
            {
                record.Detail = message;
                AppendDownload(message);
                UpdateTaskCenter();
            }),
            cancellationToken);
        var progress = new Progress<DownloadProgress>(snapshot => ApplyDownloadProgress(record, snapshot));
        var summary = await _downloader.DownloadGameFilesAsync(
            metadata,
            gameDir,
            versionId,
            usedMirror,
            IncludeAssetsCheckBox.IsChecked == true,
            progress,
            cancellationToken);

        var resultLines = new List<string>
        {
            $"下载完成：{summary.CompletedJobs}/{summary.TotalJobs}，复用 {summary.ReusedJobs}，写入 {AppFormatter.FormatBytes(summary.DownloadedBytes)}",
        };
        AppendDownload(resultLines[0]);

        if (installTypes.Count > 0)
        {
            var installProgress = new Progress<DownloadSnapshot>(snapshot => ApplyInstallProgress(record, snapshot));
            var engine = new InstallerEngine(
                versionId,
                usedMirror,
                gameDir,
                javaPath,
                message => UpdateFromAnyThread(() =>
                {
                    record.Detail = message;
                    AppendDownload(message);
                    UpdateTaskCenter();
                }),
                installProgress,
                _settings.Config.GetBool("GAME", "enable_resource_isolation", true));
            var result = await engine.InstallSequenceAsync(installTypes, cancellationToken);
            resultLines.Add(result.Message);
            AppendDownload(result.Message);
        }

        return string.Join("；", resultLines);
    }

    private void QueueDownloadTask(DownloadWorkItem task)
    {
        _downloadQueue.Enqueue(task);
        _taskRecords.Add(task.Record);
        SetTaskRecord(task.Record, "queued", 0, _activeTask is null ? "准备开始" : "等待前面的任务完成");
        AppendDownload($"已加入队列：{task.Title}");
        UpdateTaskCenter();
    }

    private async Task DispatchDownloadQueueAsync()
    {
        if (_isDispatching)
        {
            return;
        }

        _isDispatching = true;
        try
        {
            while (_activeTask is null && _downloadQueue.Count > 0)
            {
                var task = _downloadQueue.Dequeue();
                _activeTask = task;
                _activeCts = new CancellationTokenSource();
                _cancelRequested = false;
                SetTaskRecord(task.Record, "running", Math.Max(0, task.Record.Progress), "任务已开始");
                UpdateTaskCenter();

                try
                {
                    var result = await task.ExecuteAsync(task.Record, _activeCts.Token);
                    SetTaskRecord(task.Record, "completed", 100, string.IsNullOrWhiteSpace(result) ? "任务完成" : result);
                    AppendDownload($"任务完成：{task.Title}");
                }
                catch (OperationCanceledException)
                {
                    SetTaskRecord(task.Record, "canceled", task.Record.Progress, "任务已取消");
                    AppendDownload($"任务已取消：{task.Title}");
                }
                catch (Exception exc)
                {
                    var message = AppSettings.ConciseDownloadError(exc.Message);
                    SetTaskRecord(task.Record, "failed", task.Record.Progress, message);
                    _lastFailedTask = task;
                    AppendDownload($"任务失败：{task.Title}：{message}");
                }
                finally
                {
                    _activeCts.Dispose();
                    _activeCts = null;
                    _activeTask = null;
                    _cancelRequested = false;
                    UpdateTaskCenter();
                }
            }
        }
        finally
        {
            _isDispatching = false;
            UpdateTaskCenter();
        }
    }

    private async void CancelCurrentTask(object sender, RoutedEventArgs e)
    {
        if (_activeTask is null || _activeCts is null)
        {
            AppendDownload("当前没有可取消的任务。");
            return;
        }

        _cancelRequested = true;
        SetTaskRecord(_activeTask.Record, "running", _activeTask.Record.Progress, "正在取消...");
        _activeCts.Cancel();
        AppendDownload($"正在取消任务：{_activeTask.Title}");
        await Task.CompletedTask;
    }

    private async void RetryFailedTask(object sender, RoutedEventArgs e)
    {
        if (_lastFailedTask is null)
        {
            AppendDownload("当前没有可以重试的失败任务。");
            return;
        }

        var task = _lastFailedTask;
        _lastFailedTask = null;
        ResetTaskRecord(task.Record);
        _downloadQueue.Enqueue(task);
        SetTaskRecord(task.Record, "queued", 0, "准备重试");
        AppendDownload($"已重新加入队列：{task.Title}");
        UpdateTaskCenter();
        await DispatchDownloadQueueAsync();
    }

    private void ClearDownloadQueue(object sender, RoutedEventArgs e)
    {
        var count = _downloadQueue.Count;
        while (_downloadQueue.Count > 0)
        {
            var task = _downloadQueue.Dequeue();
            SetTaskRecord(task.Record, "canceled", task.Record.Progress, "已从等待队列移除");
        }

        AppendDownload($"已清空等待队列：移除 {count} 个任务。");
        UpdateTaskCenter();
    }

    private List<string> SelectedInstallTypes()
    {
        var installTypes = new List<string>();
        if (InstallFabricCheckBox.IsChecked == true) installTypes.Add("fabric");
        if (InstallForgeCheckBox.IsChecked == true) installTypes.Add("forge");
        if (InstallNeoForgeCheckBox.IsChecked == true) installTypes.Add("neoforge");
        if (InstallFabricApiCheckBox.IsChecked == true) installTypes.Add("fabric_api");
        if (InstallOptifineCheckBox.IsChecked == true) installTypes.Add("optifine");
        return installTypes;
    }

    private void AppendDownload(string message)
    {
        DownloadLogBox.Text += (DownloadLogBox.Text.Length == 0 ? "" : Environment.NewLine) + message;
        DownloadLogBox.SelectionStart = DownloadLogBox.Text.Length;
    }

    private void ApplyDownloadProgress(DownloadTaskRecord record, DownloadProgress snapshot)
    {
        var speed = UpdateSpeed(record, snapshot.DownloadedBytes);
        record.Progress = (int)Math.Clamp(Math.Round(snapshot.Progress * 100), 0, 100);
        record.CompletedFiles = snapshot.CompletedJobs;
        record.TotalFiles = snapshot.TotalJobs;
        record.ReusedFiles = snapshot.ReusedJobs;
        record.DownloadedBytes = snapshot.DownloadedBytes;
        record.SpeedBytes = speed;
        var file = Path.GetFileName(snapshot.CurrentFile);
        record.Detail = $"[{snapshot.Phase}] {snapshot.CompletedJobs}/{snapshot.TotalJobs}，复用 {snapshot.ReusedJobs}，写入 {AppFormatter.FormatBytes(snapshot.DownloadedBytes)}，{AppFormatter.FormatBytes(speed)}/s，{file}";
        DownloadProgressBar.Value = record.Progress;
        UpdateTaskCenter();
    }

    private void ApplyInstallProgress(DownloadTaskRecord record, DownloadSnapshot snapshot)
    {
        var speed = UpdateSpeed(record, snapshot.DownloadedBytes);
        record.Progress = (int)Math.Clamp(Math.Round(snapshot.Progress * 100), 0, 100);
        record.CompletedFiles = snapshot.CompletedFiles;
        record.TotalFiles = snapshot.TotalFiles;
        record.ReusedFiles = snapshot.ReusedFiles;
        record.DownloadedBytes = snapshot.DownloadedBytes;
        record.SpeedBytes = snapshot.SpeedBytes > 0 ? snapshot.SpeedBytes : speed;
        record.Detail = $"[{snapshot.Phase}] {snapshot.CompletedFiles}/{snapshot.TotalFiles}，复用 {snapshot.ReusedFiles}，{AppFormatter.FormatBytes(record.SpeedBytes)}/s，{snapshot.CurrentFile}";
        DownloadProgressBar.Value = record.Progress;
        UpdateTaskCenter();
    }

    private static long UpdateSpeed(DownloadTaskRecord record, long downloadedBytes)
    {
        var now = DateTimeOffset.UtcNow;
        if (record.LastProgressAt == default)
        {
            record.LastProgressAt = now;
            record.LastDownloadedBytes = downloadedBytes;
            return 0;
        }

        var elapsed = Math.Max(0.001, (now - record.LastProgressAt).TotalSeconds);
        var delta = Math.Max(0, downloadedBytes - record.LastDownloadedBytes);
        record.LastProgressAt = now;
        record.LastDownloadedBytes = downloadedBytes;
        return (long)(delta / elapsed);
    }

    private static void ResetTaskSpeed(DownloadTaskRecord record)
    {
        record.LastProgressAt = default;
        record.LastDownloadedBytes = 0;
        record.SpeedBytes = 0;
    }

    private void SetTaskRecord(DownloadTaskRecord record, string status, int progress, string detail)
    {
        record.Status = status;
        record.Progress = Math.Clamp(progress, 0, 100);
        record.Detail = detail;
        if (status is "queued" or "running")
        {
            ResetTaskSpeed(record);
        }

        UpdateTaskCenter();
    }

    private static void ResetTaskRecord(DownloadTaskRecord record)
    {
        record.Status = "queued";
        record.Progress = 0;
        record.Detail = "";
        record.CompletedFiles = 0;
        record.TotalFiles = 0;
        record.ReusedFiles = 0;
        record.DownloadedBytes = 0;
        record.SpeedBytes = 0;
        ResetTaskSpeed(record);
    }

    private void UpdateTaskCenter()
    {
        var activeTitle = _activeTask?.Title ?? "无";
        var nextTitle = _downloadQueue.Count > 0 ? _downloadQueue.Peek().Title : "无";
        CurrentTaskText.Text = $"当前任务：{activeTitle}";
        TaskQueueText.Text = $"队列：{_downloadQueue.Count} | 下一个：{nextTitle}";
        CancelTaskButton.IsEnabled = _activeTask is not null && !_cancelRequested;
        RetryTaskButton.IsEnabled = _lastFailedTask is not null && _activeTask is null;
        ClearQueueButton.IsEnabled = _downloadQueue.Count > 0;
        if (_activeTask is not null)
        {
            DownloadProgressBar.Value = _activeTask.Record.Progress;
        }
        else if (_downloadQueue.Count == 0)
        {
            DownloadProgressBar.Value = _taskRecords.LastOrDefault()?.Progress ?? 0;
        }

        TaskRecordsBox.Text = RenderTaskRecords();
        TaskRecordsBox.SelectionStart = TaskRecordsBox.Text.Length;
    }

    private string RenderTaskRecords()
    {
        if (_taskRecords.Count == 0)
        {
            return "暂无下载任务";
        }

        var order = new Dictionary<string, int>
        {
            ["running"] = 0,
            ["queued"] = 1,
            ["failed"] = 2,
            ["canceled"] = 3,
            ["completed"] = 4,
        };
        var builder = new StringBuilder();
        foreach (var record in _taskRecords
                     .OrderBy(record => order.GetValueOrDefault(record.Status, 9))
                     .ThenBy(record => record.Status is "running" or "queued" ? record.Id : -record.Id)
                     .Take(50))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append('[').Append(StatusLabel(record.Status)).Append("] ")
                .Append(record.Title).AppendLine();
            builder.Append(record.Progress).Append("%");
            if (record.TotalFiles > 0)
            {
                builder.Append(" | ")
                    .Append(record.CompletedFiles).Append('/').Append(record.TotalFiles)
                    .Append(" | 复用 ").Append(record.ReusedFiles);
            }

            if (record.DownloadedBytes > 0)
            {
                builder.Append(" | 写入 ").Append(AppFormatter.FormatBytes(record.DownloadedBytes));
            }

            if (record.SpeedBytes > 0)
            {
                builder.Append(" | ").Append(AppFormatter.FormatBytes(record.SpeedBytes)).Append("/s");
            }

            if (!string.IsNullOrWhiteSpace(record.Detail))
            {
                builder.AppendLine();
                builder.Append(record.Detail);
            }
        }

        return builder.ToString();
    }

    private void UpdateFromAnyThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }

    private static string ComboText(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? fallback;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }

    private static string InstallTypeLabel(string installType)
        => installType switch
        {
            "fabric" => "Fabric",
            "forge" => "Forge",
            "neoforge" => "NeoForge",
            "fabric_api" => "Fabric API",
            "optifine" => "OptiFine",
            _ => installType,
        };

    private static string StatusLabel(string status)
        => status switch
        {
            "queued" => "等待中",
            "running" => "进行中",
            "completed" => "已完成",
            "failed" => "失败",
            "canceled" => "已取消",
            _ => status,
        };

    private sealed record DownloadWorkItem(
        int Id,
        string Kind,
        string Title,
        Func<DownloadTaskRecord, CancellationToken, Task<string>> ExecuteAsync)
    {
        public DownloadTaskRecord Record { get; } = new(Id, Kind, Title);
    }

    private sealed class DownloadTaskRecord(int id, string kind, string title)
    {
        public int Id { get; } = id;
        public string Kind { get; } = kind;
        public string Title { get; } = title;
        public string Status { get; set; } = "queued";
        public int Progress { get; set; }
        public string Detail { get; set; } = "";
        public int CompletedFiles { get; set; }
        public int TotalFiles { get; set; }
        public int ReusedFiles { get; set; }
        public long DownloadedBytes { get; set; }
        public long SpeedBytes { get; set; }
        public DateTimeOffset LastProgressAt { get; set; }
        public long LastDownloadedBytes { get; set; }
    }
}
