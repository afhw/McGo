using Microsoft.Win32;
using System.Diagnostics;
using System.Text;

namespace McGo.WinUI.Services;

internal sealed record InstallProgress(int Percentage, string Status);

internal sealed record InstallerLaunchOptions(
    bool Uninstall,
    string SourceDirectory,
    string InstallDirectory,
    bool SmokeTest,
    string? SmokeReportPath,
    string? SmokeScreenshotPath)
{
    public static InstallerLaunchOptions Parse(string[] arguments)
    {
        var uninstall = arguments.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
        var source = Value(arguments, "--source=") ?? AppContext.BaseDirectory;
        var installDirectory = Value(arguments, "--install-dir=") ?? InstallerService.DefaultInstallDirectory;
        return new InstallerLaunchOptions(
            uninstall,
            Path.GetFullPath(source),
            Path.GetFullPath(installDirectory),
            arguments.Contains("--installer-smoke", StringComparer.OrdinalIgnoreCase),
            Value(arguments, "--smoke-report="),
            Value(arguments, "--smoke-screenshot="));
    }

    private static string? Value(IEnumerable<string> arguments, string prefix) =>
        arguments.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
}

internal static class InstallerService
{
    private const string ProductKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\McGo";
    private const string InstallManifestName = ".mcgo-install-manifest";
    private static readonly string[] PreservedPaths = ["accounts.json", "launcher_config.ini", ".minecraft", "logs"];

    public static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "McGo");

    public static async Task InstallAsync(
        string sourceDirectory,
        string installDirectory,
        bool desktopShortcut,
        bool registerInstallation,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        sourceDirectory = Path.GetFullPath(sourceDirectory);
        installDirectory = Path.GetFullPath(installDirectory);
        ValidateInstallPaths(sourceDirectory, installDirectory);
        EnsureClientIsNotRunning();

        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !IsUserDataPath(Path.GetRelativePath(sourceDirectory, path)))
            .ToArray();
        if (!files.Any(path => string.Equals(Path.GetFileName(path), "McGo.WinUI.exe", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("安装源中缺少 McGo.WinUI.exe。");
        }

        Directory.CreateDirectory(installDirectory);
        var installedFiles = new List<string>(files.Length);
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = files[index];
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(installDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await CopyFileAsync(sourcePath, destinationPath, cancellationToken);
            installedFiles.Add(relativePath);

            var percentage = Math.Max(1, (index + 1) * 94 / files.Length);
            progress?.Report(new InstallProgress(percentage, $"正在安装 {Path.GetFileName(relativePath)}"));
        }

        await File.WriteAllLinesAsync(
            Path.Combine(installDirectory, InstallManifestName),
            installedFiles.Order(StringComparer.OrdinalIgnoreCase),
            Encoding.UTF8,
            cancellationToken);

        if (registerInstallation)
        {
            progress?.Report(new InstallProgress(96, "正在创建快捷方式"));
            CreateShortcut(StartMenuShortcutPath(), Path.Combine(installDirectory, "McGo.WinUI.exe"));
            if (desktopShortcut)
            {
                CreateShortcut(DesktopShortcutPath(), Path.Combine(installDirectory, "McGo.WinUI.exe"));
            }
            else
            {
                File.Delete(DesktopShortcutPath());
            }

            RegisterInstallation(installDirectory);
        }

        progress?.Report(new InstallProgress(100, "安装完成"));
    }

    public static void BeginUninstall(string installDirectory, bool removeUserData, bool unregisterInstallation)
    {
        installDirectory = Path.GetFullPath(installDirectory);
        if (unregisterInstallation)
        {
            Registry.CurrentUser.DeleteSubKeyTree(ProductKey, throwOnMissingSubKey: false);
            File.Delete(StartMenuShortcutPath());
            File.Delete(DesktopShortcutPath());
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"mcgo-uninstall-{Guid.NewGuid():N}.ps1");
        var manifestPath = Path.Combine(installDirectory, InstallManifestName);
        var installedFiles = File.Exists(manifestPath)
            ? File.ReadAllLines(manifestPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            : [];
        var script = BuildCleanupScript(Environment.ProcessId, installDirectory, installedFiles, removeUserData, scriptPath);
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
        });
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        await input.CopyToAsync(output, bufferSize, cancellationToken);
    }

    private static void ValidateInstallPaths(string sourceDirectory, string installDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"安装源不存在：{sourceDirectory}");
        }

        if (SamePath(sourceDirectory, installDirectory) || IsSubdirectory(sourceDirectory, installDirectory))
        {
            throw new InvalidOperationException("安装目录不能位于安装源内部。");
        }
    }

    private static void EnsureClientIsNotRunning()
    {
        var currentProcessId = Environment.ProcessId;
        if (Process.GetProcessesByName("McGo.WinUI").Any(process => process.Id != currentProcessId))
        {
            throw new InvalidOperationException("McGo 正在运行。请关闭客户端后再继续。");
        }
    }

    private static bool IsUserDataPath(string relativePath)
    {
        var firstPart = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return PreservedPaths.Contains(firstPart, StringComparer.OrdinalIgnoreCase);
    }

    private static void RegisterInstallation(string installDirectory)
    {
        var executable = Path.Combine(installDirectory, "McGo.WinUI.exe");
        using var key = Registry.CurrentUser.CreateSubKey(ProductKey, writable: true);
        key.SetValue("DisplayName", "McGo");
        key.SetValue("DisplayVersion", McGo.Core.App.AppConstants.AppVersion);
        key.SetValue("Publisher", "McGo");
        key.SetValue("DisplayIcon", executable);
        key.SetValue("InstallLocation", installDirectory);
        key.SetValue("UninstallString", $"\"{executable}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建 Windows 快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
        shortcut.IconLocation = targetPath + ",0";
        shortcut.Description = "McGo Minecraft 启动器";
        shortcut.Save();
    }

    private static string BuildCleanupScript(
        int processId,
        string installDirectory,
        IReadOnlyCollection<string> installedFiles,
        bool removeUserData,
        string scriptPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Wait-Process -Id {processId} -ErrorAction SilentlyContinue");
        builder.AppendLine("Start-Sleep -Milliseconds 300");
        if (removeUserData)
        {
            builder.AppendLine($"Remove-Item -LiteralPath '{EscapePowerShell(installDirectory)}' -Recurse -Force -ErrorAction SilentlyContinue");
        }
        else
        {
            foreach (var relativePath in installedFiles.OrderByDescending(path => path.Length))
            {
                var path = Path.GetFullPath(Path.Combine(installDirectory, relativePath));
                if (IsSubdirectory(installDirectory, path))
                {
                    builder.AppendLine($"Remove-Item -LiteralPath '{EscapePowerShell(path)}' -Force -ErrorAction SilentlyContinue");
                }
            }

            builder.AppendLine($"Remove-Item -LiteralPath '{EscapePowerShell(Path.Combine(installDirectory, InstallManifestName))}' -Force -ErrorAction SilentlyContinue");
            foreach (var relativeDirectory in installedFiles
                         .Select(Path.GetDirectoryName)
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(path => path!.Length))
            {
                var path = Path.GetFullPath(Path.Combine(installDirectory, relativeDirectory!));
                if (IsSubdirectory(installDirectory, path))
                {
                    builder.AppendLine($"Remove-Item -LiteralPath '{EscapePowerShell(path)}' -Force -ErrorAction SilentlyContinue");
                }
            }

            builder.AppendLine($"Remove-Item -LiteralPath '{EscapePowerShell(installDirectory)}' -Force -ErrorAction SilentlyContinue");
        }

        builder.AppendLine($"Remove-Item -LiteralPath '{EscapePowerShell(scriptPath)}' -Force -ErrorAction SilentlyContinue");
        return builder.ToString();
    }

    private static string StartMenuShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "McGo.lnk");

    private static string DesktopShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "McGo.lnk");

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSubdirectory(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != "." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != "..";
    }
}
