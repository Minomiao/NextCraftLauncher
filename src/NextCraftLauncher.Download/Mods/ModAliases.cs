using System.Collections.Concurrent;
using System.Text;
using NextCraftLauncher.Core.Logging;

namespace NextCraftLauncher.Download.Mods;

/// <summary>
/// 常用 Mod 的中文别名表，用于把中文搜索词换成英文关键词。
/// 数据是本项目自己维护的 <c>Mods/Data/ModAliases.txt</c>，格式为
/// <c>中文别名（逗号分隔）=英文关键词</c>，<c>#</c> 开头为注释。
/// </summary>
public static class ModAliases
{
    private const string ResourceName = "NextCraftLauncher.Download.Mods.Data.ModAliases.txt";

    private static readonly Lazy<IReadOnlyList<AliasEntry>> Entries = new(Load);
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    public static int Count => Entries.Value.Count;

    /// <summary>是否包含中日韩统一表意文字。</summary>
    public static bool ContainsChinese(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

        foreach (var ch in value)
        {
            if (ch is >= '\u4e00' and <= '\u9fbb') return true;
        }

        return false;
    }

    /// <summary>把中文搜索词换成英文关键词；没有命中别名表时返回 <c>null</c>。</summary>
    public static string? Resolve(string? query)
    {
        var text = Normalize(query);
        if (text.Length == 0) return null;

        if (Cache.TryGetValue(text, out var cached)) return cached;

        var result = Match(text);
        if (Cache.Count > 512) Cache.Clear();
        Cache[text] = result;
        return result;
    }

    private static string? Match(string query)
    {
        string? best = null;
        var bestScore = 0;

        foreach (var entry in Entries.Value)
        {
            foreach (var alias in entry.Aliases)
            {
                var score = Score(alias, query);
                if (score <= bestScore) continue;

                bestScore = score;
                best = entry.English;
            }
        }

        // 只接受至少「包含」级别的匹配，避免把毫不相关的词硬套上去。
        return bestScore >= 50 ? best : null;
    }

    private static int Score(string alias, string query)
    {
        if (string.Equals(alias, query, StringComparison.Ordinal)) return 100;

        if (alias.StartsWith(query, StringComparison.Ordinal))
            return 80 - Math.Min(20, alias.Length - query.Length);

        if (alias.Contains(query, StringComparison.Ordinal))
            return 60 - Math.Min(20, alias.Length - query.Length);

        if (query.Contains(alias, StringComparison.Ordinal))
            return 50 - Math.Min(20, query.Length - alias.Length);

        return 0;
    }

    /// <summary>统一小写并去掉所有空白，中文输入法常带全角空格。</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch == '\u3000') continue;
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<AliasEntry> Load()
    {
        var stream = typeof(ModAliases).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            LauncherLog.Warn($"中文别名表 {ResourceName} 未找到，中文搜索将直接使用原始关键词");
            return Array.Empty<AliasEntry>();
        }

        try
        {
            using (stream)
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var entries = new List<AliasEntry>();

                while (reader.ReadLine() is { } line)
                {
                    var text = line.Trim();
                    if (text.Length == 0 || text.StartsWith('#')) continue;

                    var separator = text.IndexOf('=');
                    if (separator <= 0 || separator >= text.Length - 1) continue;

                    var english = text[(separator + 1)..].Trim();
                    if (english.Length == 0) continue;

                    var aliases = text[..separator]
                        .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(Normalize)
                        .Where(alias => alias.Length > 0)
                        .Distinct()
                        .ToList();

                    if (aliases.Count == 0) continue;
                    entries.Add(new AliasEntry(aliases, english));
                }

                LauncherLog.Info($"中文别名表已加载：{entries.Count} 条");
                return entries;
            }
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"读取中文别名表失败：{exception.Message}");
            return Array.Empty<AliasEntry>();
        }
    }

    private sealed record AliasEntry(IReadOnlyList<string> Aliases, string English);
}
