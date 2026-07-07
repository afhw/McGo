using System.Text;
using System.Text.Json;

namespace McGo.Core.Storage;

public static class AtomicStorage
{
    public static void AtomicWriteBytes(string path, byte[] data)
    {
        var targetPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(targetPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, data);
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
    {
        if (!File.Exists(path))
        {
            return fallback;
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? fallback;
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
