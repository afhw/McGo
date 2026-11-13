using McGo.Core.Minecraft;

internal static class LaunchWorkflowTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("Launch preflight runs outside its caller and observes cancellation", () =>
        {
            using var temp = TempDir.Create();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            var callerThread = Environment.CurrentManagedThreadId;
            var probeThread = callerThread;
            var service = new LaunchPreflightService(javaMajorVersionProvider: _ =>
            {
                probeThread = Environment.CurrentManagedThreadId;
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return 8;
            });
            var validation = service.ValidateAsync(temp.Path, "1.12.2", "java", cancellationToken: cancellation.Token);
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(validation.IsCompleted);
                Assert.True(probeThread != callerThread);
                cancellation.Cancel();
            }
            finally { release.Set(); }
            ExpectCanceled(validation);
        });

        runner.Add("Prelaunch command supports success and reports nonzero exit", () =>
        {
            using var temp = TempDir.Create();
            LaunchProcessService.RunPreLaunchCommandAsync("exit 0", temp.Path).GetAwaiter().GetResult();
            Assert.Throws(() => LaunchProcessService.RunPreLaunchCommandAsync("exit 7", temp.Path).GetAwaiter().GetResult());
        });

        runner.Add("Prelaunch command cancellation stops a running process", () =>
        {
            using var temp = TempDir.Create();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var command = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";
            var operation = LaunchProcessService.RunPreLaunchCommandAsync(command, temp.Path, cancellation.Token);
            Assert.False(operation.IsCompleted);
            ExpectCanceled(operation.WaitAsync(TimeSpan.FromSeconds(10)));
        });
    }

    private static void ExpectCanceled(Task task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation.");
    }
}
