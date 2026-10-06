using System.Text.RegularExpressions;

namespace WukongBench.Core;

// Preserves unknown keys and comments. Section-scoped writes cannot change a similarly named key elsewhere.
public sealed class IniFile(string text)
{
    private readonly List<string> lines = text.Replace("\r\n", "\n").Split('\n').ToList();
    public string? Get(string section, string key)
    {
        var active = false;
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith('[')) active = t.Equals($"[{section}]", StringComparison.OrdinalIgnoreCase);
            else if (active && t.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return t[(key.Length + 1)..];
        }
        return null;
    }
    public void Set(string section, string key, string value)
    {
        int start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) { lines.Add($"[{section}]"); start = lines.Count - 1; }
        int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        int existing = lines.FindIndex(start + 1, end - start - 1,
            l => l.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) lines[existing] = key + "=" + value;
        else lines.Insert(end, key + "=" + value);
    }
    public Dictionary<string, string> ReadMap(string section, string key) =>
        Regex.Matches(Get(section, key) ?? "", "\\(\\\"(?<k>[^\\\"]+)\\\",\\s*\\\"(?<v>[^\\\"]*)\\\"\\)")
            .ToDictionary(m => m.Groups["k"].Value, m => m.Groups["v"].Value, StringComparer.OrdinalIgnoreCase);
    public void UpdateMap(string section, string key, IReadOnlyDictionary<string, string> changes)
    {
        var values = ReadMap(section, key);
        foreach (var (k, v) in changes) values[k] = v;
        Set(section, key, "(" + string.Join(",", values.Select(p => $"(\"{p.Key}\", \"{p.Value}\")")) + ")");
    }
    public override string ToString() => string.Join("\r\n", lines);
}
