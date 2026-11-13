using McGo.Core.Downloads;

internal static class TaskManagementRegressionTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("Task IDs retain both failures after retrying the newer failure", RetryFailuresById);
        runner.Add("Legacy retry visits all remaining failures in failure order", RetryRemainingFailures);
        runner.Add("Concurrent retries enqueue once and reset progress at the queue tail", RetryResetsAndQueuesOnce);
        runner.Add("Cancel by ID removes only the selected queued task", CancelQueuedTask);
        runner.Add("Cancel by ID signals only the active task and preserves cancellation detail", CancelActiveTask);
        runner.Add("Clear queue keeps canceled tasks individually retryable", ClearQueueKeepsRetryableHistory);
        runner.Add("Unrequested cancellation exceptions are failures with error details", UnrequestedCancellationFails);
        runner.Add("Cancellation callback failures do not stop the queue", CancellationCallbackFailure);
        runner.Add("Finished task progress cannot be overwritten by late updates", IgnoreFinishedProgress);
        runner.Add("History pruning preserves failures and a recently retried task", PruneHistory);
        runner.Add("Unknown and completed task IDs cannot be retried or canceled", RejectInvalidActions);
        runner.Add("Task output directory is captured at enqueue and retained by retries", RetainOutputDirectory);
    }

    private static void RetryFailuresById()
    {
        var manager = new DownloadTaskManager();
        var attemptsA = 0;
        var attemptsB = 0;
        var first = manager.Enqueue("test", "same title", (_, _) => ++attemptsA == 1
            ? Task.FromException<string>(new InvalidOperationException("failure A"))
            : Task.FromResult("recovered A"));
        var second = manager.Enqueue("test", "same title", (_, _) => ++attemptsB == 1
            ? Task.FromException<string>(new InvalidOperationException("failure B"))
            : Task.FromResult("recovered B"));
        WaitForIdle(manager);
        Assert.Contains("failure A", first.ErrorDetail);
        Assert.Contains("failure B", second.ErrorDetail);

        Assert.True(manager.Retry(second.Id));
        WaitForIdle(manager);
        Assert.Equal("completed", second.Status);
        Assert.Equal("failed", first.Status);
        Assert.True(manager.HasAttentionRequired);
        Assert.Equal(first.Title, manager.LastFailedTitle);
        Assert.True(manager.Retry(first.Id));
        WaitForIdle(manager);
        Assert.Equal("completed", first.Status);
        Assert.Equal(2, attemptsA);
        Assert.Equal(2, attemptsB);
        Assert.Equal(2, manager.Records.Count);
        Assert.True(ReferenceEquals(first, manager.Records.Single(record => record.Id == first.Id)));
        Assert.Equal("", first.ErrorDetail);
        Assert.Equal("", second.ErrorDetail);
        Assert.False(manager.RetryLastFailed());
        Assert.False(manager.HasAttentionRequired);
    }

    private static void RetryRemainingFailures()
    {
        var manager = new DownloadTaskManager();
        var attemptsA = 0;
        var attemptsB = 0;
        var first = manager.Enqueue("test", "A", (_, _) => ++attemptsA < 3
            ? Task.FromException<string>(new InvalidOperationException("A failed"))
            : Task.FromResult("A recovered"));
        var second = manager.Enqueue("test", "B", (_, _) => ++attemptsB == 1
            ? Task.FromException<string>(new InvalidOperationException("B failed"))
            : Task.FromResult("B recovered"));
        WaitForIdle(manager);
        Assert.Equal("B", manager.LastFailedTitle);
        Assert.True(manager.Retry(first.Id));
        WaitForIdle(manager);
        Assert.Equal("A", manager.LastFailedTitle);
        Assert.True(manager.RetryLastFailed());
        WaitForIdle(manager);
        Assert.Equal("completed", first.Status);
        Assert.Equal("B", manager.LastFailedTitle);
        Assert.True(manager.RetryLastFailed());
        WaitForIdle(manager);
        Assert.Equal("completed", second.Status);
        Assert.True(manager.LastFailedTitle is null);
    }

    private static void RetryResetsAndQueuesOnce()
    {
        var manager = new DownloadTaskManager();
        var attempts = 0;
        var executionOrder = new List<string>();
        var failed = manager.Enqueue("test", "retry", (record, _) =>
        {
            if (++attempts > 1)
            {
                executionOrder.Add("retry");
                return Task.FromResult("recovered");
            }

            manager.Update(record, item =>
            {
                item.Progress = 170;
                item.CompletedFiles = 4;
                item.TotalFiles = 10;
                item.ReusedFiles = 2;
                item.DownloadedBytes = 2048;
                item.SpeedBytes = 512;
                item.LastProgressAt = DateTimeOffset.UtcNow;
                item.LastDownloadedBytes = 1024;
            });
            Assert.Equal(100, record.Progress);
            return Task.FromException<string>(new InvalidOperationException("retry me"));
        });
        WaitForIdle(manager);
        WithBlockedQueue(manager, active =>
        {
            var ahead = manager.Enqueue("test", "ahead", (_, _) =>
            {
                executionOrder.Add("ahead");
                return Task.FromResult("done");
            });
            var accepted = 0;
            Parallel.For(0, 16, _ =>
            {
                if (manager.Retry(failed.Id)) Interlocked.Increment(ref accepted);
            });
            Assert.Equal(1, accepted);
            Assert.False(manager.Retry(active.Id));
            Assert.False(manager.Retry(ahead.Id));
            Assert.False(manager.RetryLastFailed());
            Assert.SequenceEqual(new[] { ahead.Id, failed.Id }, manager.QueuedRecords.Select(record => record.Id).ToArray());
            Assert.Equal("ahead", manager.NextTitle);
            Assert.Equal("queued", failed.Status);
            Assert.Equal(0, failed.Progress);
            Assert.Equal(0, failed.CompletedFiles);
            Assert.Equal(0, failed.TotalFiles);
            Assert.Equal(0, failed.ReusedFiles);
            Assert.Equal(0L, failed.DownloadedBytes);
            Assert.Equal(0L, failed.SpeedBytes);
            Assert.Equal(default(DateTimeOffset), failed.LastProgressAt);
            Assert.Equal(0L, failed.LastDownloadedBytes);
            Assert.Equal("", failed.ErrorDetail);
            Assert.Equal(1, manager.Records.Count(record => record.Id == failed.Id));
            manager.Update(failed, item => item.Progress = 88);
            Assert.Equal(0, failed.Progress);
        });
        Assert.Equal(2, attempts);
        Assert.SequenceEqual(new[] { "ahead", "retry" }, executionOrder);
        Assert.Equal(100, failed.Progress);
    }

    private static void CancelQueuedTask()
    {
        var manager = new DownloadTaskManager();
        var executed = new List<int>();
        WithBlockedQueue(manager, active =>
        {
            Task<string> Run(DownloadTaskRecord record, CancellationToken token)
            {
                Assert.False(token.IsCancellationRequested);
                executed.Add(record.Id);
                return Task.FromResult("done");
            }

            var first = manager.Enqueue("test", "same title", Run);
            var second = manager.Enqueue("test", "same title", Run);
            var third = manager.Enqueue("test", "same title", Run);
            Assert.True(manager.Cancel(second.Id));
            Assert.Equal("canceled", second.Status);
            Assert.Equal("", second.ErrorDetail);
            Assert.False(manager.Cancel(second.Id));
            Assert.False(manager.IsCancelRequested);
            Assert.Equal(active.Id, manager.ActiveRecord!.Id);
            Assert.SequenceEqual(new[] { first.Id, third.Id }, manager.QueuedRecords.Select(record => record.Id).ToArray());
            Assert.True(manager.Cancel(first.Id));
            Assert.True(manager.Cancel(third.Id));
            Assert.Equal(0, manager.QueuedCount);
            Assert.True(manager.NextTitle is null);
        });
        Assert.Equal(0, executed.Count);
    }

    private static void CancelActiveTask()
    {
        var manager = new DownloadTaskManager();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken activeToken = default;
        var active = manager.Enqueue("test", "active", async (record, token) =>
        {
            activeToken = token;
            manager.Update(record, item => { item.Progress = 40; item.SpeedBytes = 2048; });
            await release.Task;
            token.ThrowIfCancellationRequested();
            return "unexpected";
        });
        var nextTokenCanceled = true;
        var next = manager.Enqueue("test", "next", (_, token) =>
        {
            nextTokenCanceled = token.IsCancellationRequested;
            return Task.FromResult("next completed");
        });
        try
        {
            Assert.True(manager.Cancel(active.Id));
            Assert.True(activeToken.IsCancellationRequested);
            Assert.True(manager.IsCancelRequested);
            Assert.False(manager.CancelCurrent());
            Assert.False(manager.Retry(active.Id));
            var cancelDetail = active.Detail;
            manager.Update(active, item => { item.Detail = "late progress"; item.Progress = 90; });
            Assert.Equal(cancelDetail, active.Detail);
            Assert.Equal(40, active.Progress);
            Assert.Equal(0L, active.SpeedBytes);
        }
        finally
        {
            release.TrySetResult();
            WaitForIdle(manager);
        }

        Assert.Equal("canceled", active.Status);
        Assert.Equal("", active.ErrorDetail);
        Assert.Equal("completed", next.Status);
        Assert.False(nextTokenCanceled);
        Assert.False(manager.IsCancelRequested);
        Assert.False(manager.CancelCurrent());
    }

    private static void ClearQueueKeepsRetryableHistory()
    {
        var manager = new DownloadTaskManager();
        var executions = 0;
        DownloadTaskRecord? retry = null;
        WithBlockedQueue(manager, _ =>
        {
            retry = manager.Enqueue("test", "retry after clear", (_, _) =>
            {
                executions++;
                return Task.FromResult("done");
            });
            var other = manager.Enqueue("test", "stay canceled", (_, _) => throw new InvalidOperationException("must not run"));
            Assert.Equal(2, manager.ClearQueue());
            Assert.Equal("canceled", retry.Status);
            Assert.Equal("canceled", other.Status);
            Assert.True(manager.Retry(retry.Id));
            Assert.False(manager.Retry(retry.Id));
            Assert.Equal(1, manager.QueuedCount);
        });
        Assert.Equal(1, executions);
        Assert.Equal("completed", retry!.Status);
    }

    private static void UnrequestedCancellationFails()
    {
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        var exceptions = new Func<CancellationToken, Exception>[]
        {
            _ => new OperationCanceledException("unrequested cancellation"),
            token => new OperationCanceledException("uncanceled task token", token),
            _ => new OperationCanceledException("unrelated token", unrelated.Token),
            _ => new TaskCanceledException("request timeout"),
        };
        foreach (var createException in exceptions)
        {
            var manager = new DownloadTaskManager();
            var failed = manager.Enqueue("test", "timeout", (_, token) => Task.FromException<string>(createException(token)));
            WaitForIdle(manager);
            Assert.Equal("failed", failed.Status);
            Assert.True(manager.HasAttentionRequired);
            Assert.True(failed.ErrorDetail.Length > 0);
            Assert.Equal("timeout", manager.LastFailedTitle);
        }
    }

    private static void CancellationCallbackFailure()
    {
        var manager = new DownloadTaskManager();
        var active = manager.Enqueue("test", "cancel callback", async (_, token) =>
        {
            using var registration = token.Register(() => throw new InvalidOperationException("callback failed"));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unexpected";
        });
        var next = manager.Enqueue("test", "next", (_, _) => Task.FromResult("done"));
        Assert.True(manager.CancelCurrent());
        WaitForIdle(manager);
        Assert.Equal("canceled", active.Status);
        Assert.Equal("", active.ErrorDetail);
        Assert.Equal("completed", next.Status);
    }

    private static void IgnoreFinishedProgress()
    {
        var manager = new DownloadTaskManager();
        var completed = manager.Enqueue("test", "completed", (record, _) =>
        {
            manager.Update(record, item => item.SpeedBytes = 1024);
            return Task.FromResult("finished");
        });
        var failed = manager.Enqueue("test", "failed", (_, _) =>
            Task.FromException<string>(new InvalidOperationException("outer failure", new IOException("inner failure"))));
        WaitForIdle(manager);
        manager.Update(completed, item => { item.Detail = "late update"; item.Progress = 20; item.SpeedBytes = 999; });
        manager.Update(failed, item => item.Detail = "late update");
        Assert.Equal(100, completed.Progress);
        Assert.Equal(0L, completed.SpeedBytes);
        Assert.Equal("finished", completed.Detail);
        Assert.Contains("outer failure", failed.Detail);
        Assert.Contains("inner failure", failed.ErrorDetail);
        Assert.Contains(nameof(IOException), failed.ErrorDetail);
    }

    private static void PruneHistory()
    {
        var manager = new DownloadTaskManager();
        var attempts = 0;
        var failed = manager.Enqueue("test", "old failure", (_, _) => ++attempts == 1
            ? Task.FromException<string>(new InvalidOperationException("preserve me"))
            : Task.FromResult("recovered"));
        DownloadTaskRecord? oldCanceled = null;
        WithBlockedQueue(manager, _ =>
        {
            oldCanceled = manager.Enqueue("test", "old canceled", (_, _) => Task.FromResult("unexpected"));
            Assert.True(manager.Cancel(oldCanceled.Id));
        });
        for (var index = 0; index < 30; index++)
        {
            manager.Enqueue("test", "finished " + index, (_, _) => Task.FromResult("done"));
        }

        WaitForIdle(manager);
        Assert.Equal(25, manager.Records.Count);
        Assert.Equal(24, manager.Records.Count(record => record.Status is "completed" or "canceled"));
        Assert.False(manager.Records.Any(record => record.Id == oldCanceled!.Id));
        Assert.False(manager.Retry(oldCanceled!.Id));
        Assert.True(manager.Retry(failed.Id));
        WaitForIdle(manager);
        Assert.Equal(24, manager.Records.Count);
        Assert.Equal(failed.Id, manager.Records.Last().Id);
        Assert.Equal("completed", failed.Status);
        Assert.False(manager.RetryLastFailed());
    }

    private static void RejectInvalidActions()
    {
        var manager = new DownloadTaskManager();
        Assert.False(manager.Cancel(-1));
        Assert.False(manager.Retry(-1));
        Assert.False(manager.CancelCurrent());
        Assert.False(manager.RetryLastFailed());
        var completed = manager.Enqueue("test", "completed", (_, _) => Task.FromResult("done"));
        WaitForIdle(manager);
        Assert.False(manager.Cancel(completed.Id));
        Assert.False(manager.Retry(completed.Id));
        Assert.Equal(0, manager.ClearQueue());
        Assert.Equal(1, manager.Records.Count);
    }

    private static void RetainOutputDirectory()
    {
        var manager = new DownloadTaskManager();
        var selectedDirectory = Path.Combine(Path.GetTempPath(), "mcgo-original-game");
        var originalDirectory = selectedDirectory;
        var attempts = 0;
        DownloadTaskRecord? task = null;
        WithBlockedQueue(manager, _ =>
        {
            task = manager.Enqueue("test", "directory", (record, _) =>
            {
                Assert.Equal(originalDirectory, record.OutputDirectory);
                return ++attempts == 1
                    ? Task.FromException<string>(new IOException("retry directory"))
                    : Task.FromResult("done");
            }, outputDirectory: selectedDirectory);
            selectedDirectory = Path.Combine(Path.GetTempPath(), "mcgo-other-game");
            Assert.Equal(originalDirectory, task.OutputDirectory);
        });
        Assert.Equal("failed", task!.Status);
        Assert.True(manager.Retry(task.Id));
        WaitForIdle(manager);
        Assert.Equal("completed", task.Status);
        Assert.Equal(originalDirectory, task.OutputDirectory);
        var noDirectory = manager.Enqueue("test", "no directory", (_, _) => Task.FromResult("done"));
        WaitForIdle(manager);
        Assert.Equal("", noDirectory.OutputDirectory);
    }

    private static void WithBlockedQueue(DownloadTaskManager manager, Action<DownloadTaskRecord> test)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = manager.Enqueue("test", "blocker", async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return "released";
        });
        try
        {
            test(active);
        }
        finally
        {
            release.TrySetResult();
            WaitForIdle(manager);
        }
    }

    private static void WaitForIdle(DownloadTaskManager manager)
        => Assert.True(SpinWait.SpinUntil(
            () => manager.ActiveRecord is null && manager.QueuedCount == 0,
            TimeSpan.FromSeconds(3)));
}
