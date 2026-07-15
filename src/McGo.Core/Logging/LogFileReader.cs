using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace McGo.Core.Logging;

public sealed record LogReadResult(string Text, bool IsTruncated, long FileSize);

public static class LogFileReader
{
    public const int DefaultMaxBytes = 2 * 1024 * 1024;

    private static readonly Regex LogFilePattern = new(
        @"\.log(?:\.\d+)?(?:\.gz)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSupported(string path) => LogFilePattern.IsMatch(Path.GetFileName(path));

    public static IReadOnlyList<string> Enumerate(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupported)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    public static async Task<LogReadResult> ReadTailAsync(
        string path,
        int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        maxBytes = Math.Max(1024, maxBytes);
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fileSize = file.Length;
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
            return await ReadCompressedTailAsync(gzip, fileSize, maxBytes, cancellationToken);
        }

        var truncated = fileSize > maxBytes;
        if (truncated)
        {
            file.Seek(-maxBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(file, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: false);
        var text = await reader.ReadToEndAsync(cancellationToken);
        if (truncated)
        {
            var firstLineEnd = text.IndexOf('\n');
            if (firstLineEnd >= 0 && firstLineEnd + 1 < text.Length)
            {
                text = text[(firstLineEnd + 1)..];
            }
        }

        return new LogReadResult(text, truncated, fileSize);
    }

    private static async Task<LogReadResult> ReadCompressedTailAsync(
        Stream stream,
        long compressedSize,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: false);
        var chunks = new Queue<string>();
        var buffer = new char[32 * 1024];
        var totalCharacters = 0;
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            chunks.Enqueue(new string(buffer, 0, read));
            totalCharacters += read;
            while (totalCharacters > maxCharacters && chunks.Count > 1)
            {
                totalCharacters -= chunks.Dequeue().Length;
                truncated = true;
            }
        }

        var text = string.Concat(chunks);
        if (text.Length > maxCharacters)
        {
            text = text[^maxCharacters..];
            truncated = true;
        }

        if (truncated)
        {
            var firstLineEnd = text.IndexOf('\n');
            if (firstLineEnd >= 0 && firstLineEnd + 1 < text.Length)
            {
                text = text[(firstLineEnd + 1)..];
            }
        }

        return new LogReadResult(text, truncated, compressedSize);
    }
}
