using System.Text.Json;
using McGo.Core.App;

namespace McGo.Core.Configuration;

public sealed record GameDirectoryEntry(string Name, string Path);

public static class GameDirectoryCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static IReadOnlyList<GameDirectoryEntry> Read(IniDocument config)
    {
        var current = NormalizePath(CurrentDirectory(config));
        var entries = new List<GameDirectoryEntry>();
        var paths = new HashSet<string>(PathComparer);
        var json = config.Get("GAME", "directories");
        if (!string.IsNullOrWhiteSpace(json))
        {
            List<GameDirectoryEntry?>? stored;
            try
            {
                stored = JsonSerializer.Deserialize<List<GameDirectoryEntry?>>(json, JsonOptions);
            }
            catch (JsonException)
            {
                stored = null;
            }

            foreach (var entry in stored ?? [])
            {
                if (entry is null) continue;
                string path;
                try
                {
                    path = NormalizePath(entry.Path);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (paths.Add(path)) entries.Add(new GameDirectoryEntry(DisplayName(entry.Name, path), path));
            }
        }

        if (paths.Add(current)) entries.Insert(0, new GameDirectoryEntry(DisplayName(null, current), current));
        return entries.AsReadOnly();
    }

    public static GameDirectoryEntry Add(IniDocument config, string path, string? name = null)
    {
        var normalized = NormalizePath(path);
        var entries = Read(config).ToList();
        var entry = entries.FirstOrDefault(item => PathComparer.Equals(item.Path, normalized));
        if (entry is null)
        {
            entry = new GameDirectoryEntry(DisplayName(name, normalized), normalized);
            entries.Add(entry);
        }

        Write(config, entries);
        return entry;
    }

    public static void Select(IniDocument config, string path)
    {
        var entries = Read(config).ToList();
        var entry = entries[Find(entries, path)];
        ChangeCurrentDirectory(config, entry.Path);
        Write(config, entries);
    }

    public static void Remove(IniDocument config, string path)
    {
        var entries = Read(config).ToList();
        var index = Find(entries, path);
        if (entries.Count == 1) throw new InvalidOperationException("Cannot remove the last game directory.");
        var removed = entries[index];
        entries.RemoveAt(index);
        if (SamePath(CurrentDirectory(config), removed.Path)) ChangeCurrentDirectory(config, entries[0].Path);
        Write(config, entries);
    }

    public static void Rename(IniDocument config, string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entries = Read(config).ToList();
        var index = Find(entries, path);
        entries[index] = entries[index] with { Name = name.Trim() };
        Write(config, entries);
    }

    public static bool SamePath(string first, string second)
        => PathComparer.Equals(NormalizePath(first), NormalizePath(second));

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new ArgumentException("Invalid game directory path.", nameof(path));
            var relative = path[(Path.GetPathRoot(path)?.Length ?? 0)..];
            if (relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Any(InvalidPathPart))
            {
                throw new ArgumentException("Invalid game directory path.", nameof(path));
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Invalid game directory path.", nameof(path), exception);
        }
    }

    private static bool InvalidPathPart(string part)
    {
        if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return true;
        if (!OperatingSystem.IsWindows() || part is "." or "..") return false;
        if (part.EndsWith(' ') || part.EndsWith('.')) return true;
        var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
               (stem.Length == 4 && stem[..3] is "COM" or "LPT" && stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }

    private static string CurrentDirectory(IniDocument config)
        => config.Get("GAME", "directory", AppConstants.DefaultGameDirectory);

    private static string DisplayName(string? name, string path)
        => !string.IsNullOrWhiteSpace(name) ? name.Trim() : Path.GetFileName(path) is { Length: > 0 } leaf ? leaf : path;

    private static int Find(List<GameDirectoryEntry> entries, string path)
    {
        var normalized = NormalizePath(path);
        var index = entries.FindIndex(entry => PathComparer.Equals(entry.Path, normalized));
        return index >= 0 ? index : throw new ArgumentException("Game directory is not in the catalog.", nameof(path));
    }

    private static void ChangeCurrentDirectory(IniDocument config, string path)
    {
        var current = CurrentDirectory(config);
        if (SamePath(current, path)) return;
        if (string.IsNullOrWhiteSpace(config.Get("GAME", "legacy_settings_directory")))
        {
            config.Set("GAME", "legacy_settings_directory", current);
        }

        config.Set("GAME", "directory", path);
    }

    private static void Write(IniDocument config, List<GameDirectoryEntry> entries)
    {
        config.Set("GAME", "directories", JsonSerializer.Serialize(entries));
        config.Set("GAME", "directory", CurrentDirectory(config));
    }
}
