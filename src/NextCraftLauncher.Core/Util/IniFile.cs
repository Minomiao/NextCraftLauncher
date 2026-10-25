namespace NextCraftLauncher.Core.Util;

/// <summary>
/// 兼容 PCL2 的 INI 读取：PCL2 使用 <c>Key:Value</c>，这里同时接受 <c>Key=Value</c>。
/// </summary>
public sealed class IniFile
{
    private readonly Dictionary<string, string> _values;

    private IniFile(bool exists, Dictionary<string, string> values)
    {
        Exists = exists;
        _values = values;
    }

    public static IniFile Empty { get; } = new(false, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public bool Exists { get; }

    public static IniFile Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return new IniFile(false, values);

        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('[')) continue;

                var colon = line.IndexOf(':');
                var equals = line.IndexOf('=');
                var separator = colon < 0 ? equals
                    : equals < 0 ? colon
                    : Math.Min(colon, equals);

                if (separator <= 0) continue;

                var key = line[..separator].Trim().TrimStart('\uFEFF');
                var value = line[(separator + 1)..].Trim();
                if (key.Length > 0) values[key] = value;
            }
        }
        catch (IOException)
        {
            return new IniFile(false, values);
        }

        return new IniFile(true, values);
    }

    public bool Contains(string key) => _values.ContainsKey(key);

    public string? GetString(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public int? GetInt(string key) => int.TryParse(GetString(key), out var value) ? value : null;

    public bool? GetBool(string key) => bool.TryParse(GetString(key), out var value) ? value : null;

    /// <summary>
    /// 写入或更新一个键，保留文件中的其他内容。使用 PCL2 的 <c>Key:Value</c> 格式。
    /// </summary>
    public static void SetValue(string path, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空。", nameof(path));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("键不能为空。", nameof(key));

        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var replaced = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
                continue;

            var colon = line.IndexOf(':');
            var equals = line.IndexOf('=');
            var separator = colon < 0 ? equals
                : equals < 0 ? colon
                : Math.Min(colon, equals);

            if (separator <= 0) continue;

            var existingKey = line[..separator].Trim().TrimStart('\uFEFF');
            if (!string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase)) continue;

            lines[index] = $"{key}:{value}";
            replaced = true;
            break;
        }

        if (!replaced) lines.Add($"{key}:{value}");

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllLines(path, lines);
    }
}
