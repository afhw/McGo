using System.Collections.Concurrent;
using System.Text;

namespace McGo.Core.Configuration;

public sealed class IniDocument
{
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Dictionary<string, string>> Sections => _sections;

    public static IniDocument Load(string path, bool persistChanges = true)
    {
        var targetPath = Path.GetFullPath(path);
        lock (FileLock(targetPath))
        {
            return Storage.AtomicStorage.LoadRecoverable(targetPath, Parse, new IniDocument(), persistChanges: persistChanges);
        }
    }

    // Recovery and direct INI writes share the read/merge/write transaction lock.
    internal static object FileLock(string path) => FileLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    internal IniDocument Clone()
    {
        lock (_sync)
        {
            var copy = new IniDocument();
            foreach (var (section, values) in _sections)
            {
                copy._sections[section] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            }

            return copy;
        }
    }

    internal bool ContentEquals(IniDocument other)
        => _sections.Count == other._sections.Count && _sections.All(section =>
            other._sections.TryGetValue(section.Key, out var values) &&
            section.Value.Count == values.Count && section.Value.All(pair =>
                values.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal)));

    internal void ApplyChangesFrom(IniDocument baseline, IniDocument current)
    {
        foreach (var (section, values) in current._sections)
        {
            baseline._sections.TryGetValue(section, out var original);
            foreach (var (key, value) in values)
            {
                if (original is null || !original.TryGetValue(key, out var oldValue) ||
                    !string.Equals(value, oldValue, StringComparison.Ordinal))
                {
                    Set(section, key, value);
                }
            }
        }

        // Sections exposes mutable value dictionaries; preserve key removals too.
        foreach (var (section, values) in baseline._sections)
        {
            current._sections.TryGetValue(section, out var updated);
            foreach (var key in values.Keys)
            {
                if ((updated is null || !updated.ContainsKey(key)) && _sections.TryGetValue(section, out var target))
                {
                    target.Remove(key);
                }
            }
        }
    }

    private static IniDocument Parse(string text)
    {
        var document = new IniDocument();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("INI file is empty.");
        }

        var section = "";
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']') || line.Length <= 2)
                {
                    throw new InvalidDataException($"Invalid INI section: {line}");
                }

                section = line[1..^1].Trim();
                if (section.Length == 0)
                {
                    throw new InvalidDataException("INI section name cannot be empty.");
                }

                document.EnsureSection(section);
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                separator = line.IndexOf(':');
            }

            if (separator < 0)
            {
                throw new InvalidDataException($"Invalid INI entry: {line}");
            }

            var key = line[..separator].Trim();
            if (section.Length == 0 || key.Length == 0)
            {
                throw new InvalidDataException($"Invalid INI entry: {line}");
            }

            document.Set(section, key, line[(separator + 1)..].Trim());
        }

        if (document._sections.Count == 0)
        {
            throw new InvalidDataException("INI file contains no sections.");
        }

        return document;
    }

    public void Save(string path)
    {
        var targetPath = Path.GetFullPath(path);
        lock (FileLock(targetPath))
        {
            var snapshot = Clone();
            var builder = new StringBuilder();
            foreach (var section in snapshot._sections.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append('[').Append(section.Key).AppendLine("]");
                foreach (var pair in section.Value.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    builder.Append(pair.Key).Append(" = ").AppendLine(pair.Value);
                }

                builder.AppendLine();
            }

            Storage.AtomicStorage.AtomicWriteText(targetPath, builder.ToString());
        }
    }

    public void EnsureDefaults(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> defaults)
    {
        lock (_sync)
        {
            foreach (var (section, values) in defaults)
            {
                EnsureSection(section);
                foreach (var (key, value) in values)
                {
                    if (!_sections[section].ContainsKey(key))
                    {
                        _sections[section][key] = value;
                    }
                }
            }
        }
    }

    public string Get(string section, string key, string fallback = "")
    {
        lock (_sync)
        {
            return _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : fallback;
        }
    }

    public int GetInt(string section, string key, int fallback = 0)
        => int.TryParse(Get(section, key), out var value) ? value : fallback;

    public bool GetBool(string section, string key, bool fallback = false)
        => bool.TryParse(Get(section, key), out var value) ? value : fallback;

    public void Set(string section, string key, string value)
    {
        lock (_sync)
        {
            EnsureSection(section);
            _sections[section][key] = value;
        }
    }

    private void EnsureSection(string section)
    {
        if (!_sections.ContainsKey(section))
        {
            _sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
