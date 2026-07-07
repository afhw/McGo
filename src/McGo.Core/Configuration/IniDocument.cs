using System.Text;

namespace McGo.Core.Configuration;

public sealed class IniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Dictionary<string, string>> Sections => _sections;

    public static IniDocument Load(string path)
    {
        var document = new IniDocument();
        if (!File.Exists(path))
        {
            return document;
        }

        var section = "";
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
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
                continue;
            }

            document.Set(section, line[..separator].Trim(), line[(separator + 1)..].Trim());
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
