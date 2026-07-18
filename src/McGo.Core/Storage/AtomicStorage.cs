using System.Text;
using System.Text.Json;

namespace McGo.Core.Storage;

public static class AtomicStorage
{
    public static string BackupPath(string path) => Path.GetFullPath(path) + ".bak";

    public static void AtomicWriteBytes(string path, byte[] data)
    {
        var targetPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteDurable(tempPath, data);
            if (File.Exists(targetPath))
            {
                File.Copy(targetPath, BackupPath(targetPath), overwrite: true);
            }

            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static void AtomicWriteText(string path, string text, Encoding? encoding = null)
        => AtomicWriteBytes(path, (encoding ?? new UTF8Encoding(false)).GetBytes(text));

    public static void SaveJsonAtomic<T>(string path, T data, bool indented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        AtomicWriteText(path, JsonSerializer.Serialize(data, options) + "\n");
    }

    public static T LoadJsonFile<T>(string path, T fallback)
        => LoadRecoverable(path, text => JsonSerializer.Deserialize<T>(text) ?? throw new JsonException("JSON root is null."), fallback);

    public static T LoadRecoverable<T>(string path, Func<string, T> parser, T fallback)
    {
        var targetPath = Path.GetFullPath(path);
        var backupPath = BackupPath(targetPath);
        if (TryLoad(targetPath, parser, out var value, out var primaryCorrupt))
        {
            return value!;
        }

        if (primaryCorrupt)
        {
            Quarantine(targetPath);
        }

        if (!TryLoad(backupPath, parser, out value, out _))
        {
            return fallback;
        }

        RestoreBackup(targetPath, backupPath);
        return value!;
    }

    private static bool TryLoad<T>(string path, Func<string, T> parser, out T? value, out bool corrupt)
    {
        value = default;
        corrupt = false;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            value = parser(File.ReadAllText(path, Encoding.UTF8));
            return true;
        }
        catch (Exception exc) when (exc is JsonException or InvalidDataException or DecoderFallbackException or FormatException)
        {
            corrupt = true;
            return false;
        }
    }

    private static void RestoreBackup(string targetPath, string backupPath)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.restore.tmp");
        try
        {
            WriteDurable(tempPath, File.ReadAllBytes(backupPath));
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void Quarantine(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var quarantinePath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, quarantinePath);
        }
        catch
        {
            // Recovery can still continue from the backup if quarantine is unavailable.
        }
    }

    private static void WriteDurable(string path, byte[] data)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup mirrors the previous implementation.
        }
    }
}
