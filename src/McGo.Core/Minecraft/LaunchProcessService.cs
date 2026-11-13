using System.Diagnostics;

namespace McGo.Core.Minecraft;

public static class LaunchProcessService
{
    public static async Task RunPreLaunchCommandAsync(string command, string runtimeDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(command)) return;

        Directory.CreateDirectory(runtimeDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : "/bin/sh",
            WorkingDirectory = runtimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        startInfo.ArgumentList.Add(command);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("启动前命令无法执行。");
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"启动前命令失败，退出码 {process.ExitCode}。");
    }
}
