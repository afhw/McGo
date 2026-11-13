using McGo.Core.Configuration;
using McGo.Core.Logging;

namespace McGo.Core.Downloads;

public sealed class DownloadTaskManager
{
    private const int CompletedTaskLimit = 24;
    private readonly object _gate = new();
    private readonly Queue<DownloadTaskWorkItem> _queue = new();
    private readonly List<DownloadTaskRecord> _records = [];
    private readonly Dictionary<int, DownloadTaskWorkItem> _tasksById = [];
    private readonly List<DownloadTaskWorkItem> _failedTasks = [];
    private DownloadTaskWorkItem? _activeTask;
    private CancellationTokenSource? _activeCancellation;
    private Task _cancellationCallbacks = Task.CompletedTask;
    private bool _dispatching;
    private bool _cancelRequested;
    private int _nextId = 1;

    public static DownloadTaskManager Current { get; } = new();

    public event EventHandler? StateChanged;

    public DownloadTaskRecord? ActiveRecord
    {
        get { lock (_gate) return _activeTask?.Record; }
    }

    public string? ActiveTitle
    {
        get { lock (_gate) return _activeTask?.Title; }
    }

    public string? LastFailedTitle
    {
        get { lock (_gate) return _failedTasks.LastOrDefault()?.Title; }
    }

    public int QueuedCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    public string? NextTitle
    {
        get { lock (_gate) return _queue.TryPeek(out var task) ? task.Title : null; }
    }

    public bool IsCancelRequested
    {
        get { lock (_gate) return _cancelRequested; }
    }

    public IReadOnlyList<DownloadTaskRecord> Records
    {
        get { lock (_gate) return _records.ToArray(); }
    }

    public IReadOnlyList<DownloadTaskRecord> QueuedRecords
    {
        get { lock (_gate) return _queue.Select(task => task.Record).ToArray(); }
    }

    public bool HasAttentionRequired
    {
        get
        {
            lock (_gate)
            {
                return _activeTask is not null || _queue.Count > 0 || _records.Any(record => record.Status == "failed");
            }
        }
    }

    public string NavigationLabel
    {
        get
        {
            lock (_gate)
            {
                if (_activeTask is not null)
                {
                    return $"下载任务 {_activeTask.Record.Progress}%";
                }

                if (_queue.Count > 0)
                {
                    return $"下载队列 {_queue.Count}";
                }

                var failed = _records.Count(record => record.Status == "failed");
                return failed > 0 ? $"下载失败 {failed}" : "下载任务";
            }
        }
    }

    public DownloadTaskRecord Enqueue(
        string kind,
        string title,
        Func<DownloadTaskRecord, CancellationToken, Task<string>> operation,
        string? outputDirectory = null)
    {
        DownloadTaskWorkItem item;
        lock (_gate)
        {
            item = new DownloadTaskWorkItem(_nextId++, kind, title, operation);
            item.Record.Status = "queued";
            item.Record.OutputDirectory = outputDirectory ?? "";
            item.Record.Detail = _activeTask is null && _queue.Count == 0 ? "准备开始" : "等待前面的任务完成";
            _queue.Enqueue(item);
            _tasksById.Add(item.Id, item);
            _records.Add(item.Record);
            PruneRecordsLocked();
        }

        AppLog.Info("Tasks", $"任务已加入全局队列：{title}");
        NotifyStateChanged();
        _ = DispatchQueueAsync();
        return item.Record;
    }

    public void Update(DownloadTaskRecord record, Action<DownloadTaskRecord> update)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeTask?.Record, record) || record.Status != "running" || _cancelRequested)
            {
                return;
            }

            update(record);
            record.Progress = Math.Clamp(record.Progress, 0, 100);
        }

        NotifyStateChanged();
    }

    public bool CancelCurrent()
    {
        int? id;
        lock (_gate)
        {
            id = _activeTask?.Id;
        }

        return id.HasValue && Cancel(id.Value);
    }

    public bool Cancel(int id)
    {
        string title;
        bool wasRunning;
        lock (_gate)
        {
            if (!_tasksById.TryGetValue(id, out var task))
            {
                return false;
            }

            wasRunning = ReferenceEquals(task, _activeTask) && task.Record.Status == "running";
            if (wasRunning)
            {
                if (_activeCancellation is null || _cancelRequested || task.Record.Status != "running")
                {
                    return false;
                }

                _cancelRequested = true;
                task.Record.Detail = "正在取消...";
                task.Record.SpeedBytes = 0;
                // CancelAsync signals the token immediately without running callbacks under _gate.
                _cancellationCallbacks = _activeCancellation.CancelAsync();
            }
            else
            {
                if (task.Record.Status != "queued")
                {
                    return false;
                }

                var count = _queue.Count;
                for (var index = 0; index < count; index++)
                {
                    var queued = _queue.Dequeue();
                    if (queued.Id != id)
                    {
                        _queue.Enqueue(queued);
                    }
                }

                task.Record.Status = "canceled";
                task.Record.Detail = "已从等待队列移除";
                task.Record.SpeedBytes = 0;
                PruneRecordsLocked();
            }

            title = task.Title;
        }

        AppLog.Info("Tasks", wasRunning ? $"正在取消全局任务：{title}" : $"已取消排队任务：{title}");
        NotifyStateChanged();
        return true;
    }

    public bool RetryLastFailed()
    {
        int? id;
        lock (_gate)
        {
            id = _failedTasks.LastOrDefault()?.Id;
        }

        return id.HasValue && Retry(id.Value);
    }

    public bool Retry(int id)
    {
        DownloadTaskWorkItem task;
        lock (_gate)
        {
            if (!_tasksById.TryGetValue(id, out task!) ||
                task.Record.Status is not ("failed" or "canceled"))
            {
                return false;
            }

            _failedTasks.Remove(task);
            ResetRecord(task.Record);
            task.Record.Detail = _activeTask is null && _queue.Count == 0 ? "准备重试" : "等待前面的任务完成";
            _queue.Enqueue(task);
            _records.Remove(task.Record);
            _records.Add(task.Record);
        }

        AppLog.Info("Tasks", $"任务已重新加入全局队列：{task.Title}");
        NotifyStateChanged();
        _ = DispatchQueueAsync();
        return true;
    }

    public int ClearQueue()
    {
        int count;
        lock (_gate)
        {
            count = _queue.Count;
            while (_queue.TryDequeue(out var task))
            {
                task.Record.Status = "canceled";
                task.Record.Detail = "已从等待队列移除";
                task.Record.SpeedBytes = 0;
            }

            PruneRecordsLocked();
        }

        AppLog.Info("Tasks", $"全局等待队列已清空：移除 {count} 个任务。");
        NotifyStateChanged();
        return count;
    }

    private async Task DispatchQueueAsync()
    {
        lock (_gate)
        {
            if (_dispatching)
            {
                return;
            }

            _dispatching = true;
        }

        try
        {
            while (true)
            {
                DownloadTaskWorkItem task;
                CancellationTokenSource cancellation;
                lock (_gate)
                {
                    if (_activeTask is not null || !_queue.TryDequeue(out task!))
                    {
                        break;
                    }

                    _activeTask = task;
                    cancellation = new CancellationTokenSource();
                    _activeCancellation = cancellation;
                    _cancellationCallbacks = Task.CompletedTask;
                    _cancelRequested = false;
                    task.Record.Status = "running";
                    task.Record.Detail = "任务已开始";
                }

                AppLog.Info("Tasks", $"全局任务开始：{task.Title}");
                NotifyStateChanged();
                try
                {
                    var result = await task.Operation(task.Record, cancellation.Token);
                    lock (_gate)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        task.Record.Status = "completed";
                        task.Record.Progress = 100;
                        task.Record.Detail = string.IsNullOrWhiteSpace(result) ? "任务完成" : result;
                    }

                    AppLog.Info("Tasks", $"全局任务完成：{task.Title}");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        task.Record.Status = "canceled";
                        task.Record.Detail = "任务已取消";
                    }

                    AppLog.Info("Tasks", $"全局任务已取消：{task.Title}");
                }
                catch (Exception exc)
                {
                    var message = AppSettings.ConciseDownloadError(exc.Message);
                    lock (_gate)
                    {
                        task.Record.Status = "failed";
                        task.Record.Detail = message;
                        task.Record.ErrorDetail = AppLogWriter.Redact(exc.ToString());
                        _failedTasks.Add(task);
                    }

                    AppLog.Error("Tasks", $"全局任务失败：{task.Title}；{message}", exc);
                }
                finally
                {
                    Task cancellationCallbacks;
                    lock (_gate)
                    {
                        cancellationCallbacks = _cancellationCallbacks;
                        _activeCancellation = null;
                        _activeTask = null;
                        _cancelRequested = false;
                        task.Record.SpeedBytes = 0;
                        PruneRecordsLocked();
                    }

                    try
                    {
                        await cancellationCallbacks;
                    }
                    catch (Exception exc)
                    {
                        AppLog.Warning("Tasks", "任务取消回调失败：" + exc.Message);
                    }
                    finally
                    {
                        cancellation.Dispose();
                    }

                    NotifyStateChanged();
                }
            }
        }
        finally
        {
            bool dispatchAgain;
            lock (_gate)
            {
                _dispatching = false;
                dispatchAgain = _activeTask is null && _queue.Count > 0;
            }

            NotifyStateChanged();
            if (dispatchAgain)
            {
                _ = DispatchQueueAsync();
            }
        }
    }

    private void PruneRecordsLocked()
    {
        var finished = _records.Where(record => record.Status is "completed" or "canceled").ToList();
        foreach (var record in finished.Take(Math.Max(0, finished.Count - CompletedTaskLimit)))
        {
            _records.Remove(record);
            _tasksById.Remove(record.Id);
        }
    }

    private static void ResetRecord(DownloadTaskRecord record)
    {
        record.Status = "queued";
        record.Progress = 0;
        record.Detail = "";
        record.ErrorDetail = "";
        record.CompletedFiles = 0;
        record.TotalFiles = 0;
        record.ReusedFiles = 0;
        record.DownloadedBytes = 0;
        record.SpeedBytes = 0;
        record.LastProgressAt = default;
        record.LastDownloadedBytes = 0;
    }

    private void NotifyStateChanged()
    {
        foreach (EventHandler handler in StateChanged?.GetInvocationList().Cast<EventHandler>() ?? [])
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception exc)
            {
                AppLog.Warning("Tasks", "刷新任务界面失败：" + exc.Message);
            }
        }
    }

    private sealed record DownloadTaskWorkItem(
        int Id,
        string Kind,
        string Title,
        Func<DownloadTaskRecord, CancellationToken, Task<string>> Operation)
    {
        public DownloadTaskRecord Record { get; } = new(Id, Kind, Title);
    }
}

public sealed class DownloadTaskRecord(int id, string kind, string title)
{
    public int Id { get; } = id;
    public string Kind { get; } = kind;
    public string Title { get; } = title;
    public string Status { get; set; } = "queued";
    public int Progress { get; set; }
    public string Detail { get; set; } = "";
    public string ErrorDetail { get; internal set; } = "";
    public string OutputDirectory { get; set; } = "";
    public int CompletedFiles { get; set; }
    public int TotalFiles { get; set; }
    public int ReusedFiles { get; set; }
    public long DownloadedBytes { get; set; }
    public long SpeedBytes { get; set; }
    public DateTimeOffset LastProgressAt { get; set; }
    public long LastDownloadedBytes { get; set; }
}
