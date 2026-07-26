using System.Text;

namespace McGo.Core.Configuration;

public sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Dictionary<string, string>> Sections => _sections;

    public static IniDocument Load(string path)
        => Storage.AtomicStorage.LoadRecoverable(path, Parse, new IniDocument());

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
        var builder = new StringBuilder();
        foreach (var section in _sections.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('[').Append(section.Key).AppendLine("]");
            foreach (var pair in section.Value.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(pair.Key).Append(" = ").AppendLine(pair.Value);
            }

            builder.AppendLine();
        }

        Storage.AtomicStorage.AtomicWriteText(path, builder.ToString());
    }

    public void EnsureDefaults(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> defaults)
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

    public string Get(string section, string key, string fallback = "")
        => _sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : fallback;

    public int GetInt(string section, string key, int fallback = 0)
        => int.TryParse(Get(section, key), out var value) ? value : fallback;

    public bool GetBool(string section, string key, bool fallback = false)
        => bool.TryParse(Get(section, key), out var value) ? value : fallback;

    public void Set(string section, string key, string value)
    {
        EnsureSection(section);
        _sections[section][key] = value;
    }

    private void EnsureSection(string section)
    {
        if (!_sections.ContainsKey(section))
        {
            _sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
