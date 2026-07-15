using System.Text;
using System.Text.RegularExpressions;

namespace McGo.Core.Logging;

public sealed class AppLogWriter
{
    private static readonly Regex SensitiveValuePattern = new(
        """(?i)(access[_-]?token|refresh[_-]?token|password|passwd|secret|密码|口令)(\s*[:=]\s*)("[^"]*"|[^\s,;]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly int _backupCount;

    public AppLogWriter(string directory, long maxBytes = 2 * 1024 * 1024, int backupCount = 5)
    {
        DirectoryPath = Path.GetFullPath(directory);
        _maxBytes = Math.Max(1024, maxBytes);
        _backupCount = Math.Max(1, backupCount);
    }

    public string DirectoryPath { get; }

    public string FilePath => Path.Combine(DirectoryPath, "mcgo.log");

    public void Write(string level, string source, string message, Exception? exception = null)
    {
        try
        {
            var safeLevel = string.IsNullOrWhiteSpace(level) ? "INFO" : level.Trim().ToUpperInvariant();
            var safeSource = string.IsNullOrWhiteSpace(source) ? "App" : source.Trim();
            var safeMessage = Redact(message);
            var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{safeLevel}] [{safeSource}] {safeMessage}";
            if (exception is not null)
            {
                entry += Environment.NewLine + Redact(exception.ToString());
            }

            entry += Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(entry);
            lock (_gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded(bytes.Length);
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
            }
        }
        catch
        {
            // Logging must never interrupt the operation being recorded.
        }
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var redacted = SensitiveValuePattern.Replace(value, match => match.Groups[1].Value + match.Groups[2].Value + "[REDACTED]");
        return BearerPattern.Replace(redacted, "Bearer [REDACTED]");
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var file = new FileInfo(FilePath);
        if (!file.Exists || file.Length + incomingBytes <= _maxBytes)
        {
            return;
        }

        var oldest = FilePath + "." + _backupCount;
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _backupCount - 1; index >= 1; index--)
        {
            var source = FilePath + "." + index;
            if (File.Exists(source))
            {
                File.Move(source, FilePath + "." + (index + 1), overwrite: true);
            }
        }

        File.Move(FilePath, FilePath + ".1", overwrite: true);
    }
}

public static class AppLog
{
    private static readonly AppLogWriter Writer = new(Path.Combine(AppContext.BaseDirectory, "logs"));

    public static string DirectoryPath => Writer.DirectoryPath;

    public static string FilePath => Writer.FilePath;

    public static void Info(string source, string message) => Writer.Write("INFO", source, message);

    public static void Warning(string source, string message) => Writer.Write("WARN", source, message);

    public static void Error(string source, string message, Exception? exception = null) => Writer.Write("ERROR", source, message, exception);
}
