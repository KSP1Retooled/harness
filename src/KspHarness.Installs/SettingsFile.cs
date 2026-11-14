using System.Text;

namespace KspHarness.Installs;

/// <summary>
/// Sets top-level values in a KSP <c>settings.cfg</c>, leaving every other line as it is.
/// </summary>
/// <remarks>
/// KSP ignores the whole file (and then overwrites it with defaults) unless it has <c>SETTINGS_FILE_VERSION</c> of at
/// least 1.1.0, so that value is added when missing. Keys the file lacks keep the game's defaults; a fresh install
/// writes the full file on its first boot. The one difference from a first boot is that key bindings come from the
/// defaults instead of the detected keyboard layout.
/// </remarks>
public static class SettingsFile
{
    public const string FileName = "settings.cfg";

    public const string VersionKey = "SETTINGS_FILE_VERSION";

    private const string DefaultVersion = "1.12.5";

    public static void CheckEntry(string key, string value)
    {
        bool badKey = key.Length == 0 || key.Any(c => char.IsWhiteSpace(c) || c is '=' or '{' or '}' or '/');
        if (badKey)
        {
            throw new ArgumentException($"not a settings.cfg key: '{key}'", nameof(key));
        }

        if (value.Contains('\n') || value.Contains('\r') || value.Contains('{') || value.Contains('}'))
        {
            throw new ArgumentException($"settings.cfg value for {key} cannot hold newlines or braces", nameof(value));
        }
    }

    public static void Apply(string path, IEnumerable<KeyValuePair<string, string>> values)
    {
        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            CheckEntry(key, value);
            pending[key] = value;
        }

        string text = File.Exists(path) ? File.ReadAllText(path) : "// KSP Game Settings\n\n";
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.ReplaceLineEndings("\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        bool hasVersion = false;
        int depth = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = StripComment(lines[i]);
            if (depth == 0 && TryParseValue(line, out string key))
            {
                hasVersion |= key == VersionKey;
                if (pending.Remove(key, out string? value))
                {
                    lines[i] = $"{key} = {value}";
                }
            }

            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
        }

        if (!hasVersion && !pending.ContainsKey(VersionKey))
        {
            lines.Insert(FirstContentLine(lines), $"{VersionKey} = {DefaultVersion}");
        }

        int insertAt = FirstNodeLine(lines);
        foreach (var (key, value) in pending)
        {
            lines.Insert(insertAt++, $"{key} = {value}");
        }

        var output = new StringBuilder();
        foreach (string line in lines)
        {
            output.Append(line).Append(newline);
        }

        // settings.cfg is copied, never linked, but deleting first keeps that true if the copy rules change.
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.WriteAllText(path, output.ToString());
    }

    /// <summary>Removes top-level values, so that the game uses its defaults for them.</summary>
    public static void Remove(string path, IEnumerable<string> keys)
    {
        var remove = keys.ToHashSet(StringComparer.Ordinal);
        string text = File.ReadAllText(path);
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var kept = new List<string>();
        int depth = 0;
        foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = StripComment(raw);
            bool drop = depth == 0 && TryParseValue(line, out string key) && remove.Contains(key);
            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
            if (!drop)
            {
                kept.Add(raw);
            }
        }

        File.Delete(path);
        File.WriteAllText(path, string.Join(newline, kept));
    }

    /// <summary>Reads the top-level values, for checks and tests.</summary>
    public static IReadOnlyDictionary<string, string> ReadTopLevel(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int depth = 0;
        foreach (string raw in File.ReadLines(path))
        {
            string line = StripComment(raw);
            if (depth == 0 && TryParseValue(line, out string key))
            {
                values[key] = line[(line.IndexOf('=') + 1)..].Trim();
            }

            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
        }

        return values;
    }

    private static string StripComment(string line)
    {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static bool TryParseValue(string line, out string key)
    {
        int equals = line.IndexOf('=');
        key = equals < 0 ? "" : line[..equals].Trim();
        return key.Length > 0 && !line.Contains('{') && !line.Contains('}');
    }

    private static int FirstContentLine(List<string> lines)
    {
        int index = lines.FindIndex(line => StripComment(line).Trim().Length > 0);
        return index < 0 ? lines.Count : index;
    }

    // New values go before the first node so that they sit with the other top-level values.
    private static int FirstNodeLine(List<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            string line = StripComment(lines[i]).Trim();
            bool nodeHeader =
                line.Length > 0
                && !line.Contains('=')
                && (line.Contains('{') || (i + 1 < lines.Count && StripComment(lines[i + 1]).Trim() == "{"));
            if (nodeHeader)
            {
                return i;
            }
        }

        return lines.Count;
    }
}
