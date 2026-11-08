using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Mods;

/// <summary>
/// 中文名的在线补全：内置别名表没收录时，从 MC百科（mcmod.cn）的搜索结果里
/// 取「中文名 (英文名)」，结果缓存在 <c>NCL/cache/mods/aliases.json</c>，
/// 命中 30 天、未命中 1 天内不再联网。
/// </summary>
public static partial class ModAliasOnline
{
    private const string SearchUrl = "https://search.mcmod.cn/s?key=";

    private static readonly TimeSpan HitFreshness = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissFreshness = TimeSpan.FromDays(1);

    /// <summary>两次请求之间至少间隔这么久，避免被站点限流。</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1200);

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, CacheEntry> Entries = new();
    private static readonly object FileLock = new();

    private static bool _loaded;
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public static string CachePath => Path.Combine(AppSettings.DataDirectory, "cache", "mods", "aliases.json");

    /// <summary>查询中文名对应的英文关键词；失败或没找到时返回 <c>null</c>。</summary>
    public static async Task<string?> ResolveAsync(string query, CancellationToken cancellationToken)
    {
        var key = ModAliases.Normalize(query);
        if (key.Length == 0) return null;

        EnsureLoaded();

        if (Entries.TryGetValue(key, out var cached) && IsFresh(cached)) return cached.English;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            // 排队期间可能已经被别的请求填上了。
            if (Entries.TryGetValue(key, out cached) && IsFresh(cached)) return cached.English;

            var wait = MinInterval - (DateTimeOffset.UtcNow - _lastRequest);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastRequest = DateTimeOffset.UtcNow;

            var english = await SearchAsync(query, key, cancellationToken);

            Store(key, new CacheEntry(english, DateTimeOffset.UtcNow));
            if (english is not null)
                LauncherLog.Info($"MC百科：中文「{query}」→「{english}」");

            return english;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"查询 MC百科 中文名失败：{exception.Message}");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string?> SearchAsync(string query, string normalizedQuery, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        var bytes = await Http.GetByteArrayAsync(
            SearchUrl + Uri.EscapeDataString(query), timeout.Token);
        var html = Encoding.UTF8.GetString(bytes);

        string? best = null;
        var bestScore = 0;

        foreach (Match item in ResultItemRegex().Matches(html))
        {
            foreach (Match link in TitleLinkRegex().Matches(item.Groups[1].Value))
            {
                var title = WebUtility.HtmlDecode(TagRegex().Replace(link.Groups[1].Value, string.Empty)).Trim();

                foreach (var (chinese, english) in SplitTitle(title))
                {
                    foreach (var alias in chinese.Split('/', '／', '，', ','))
                    {
                        var score = ModAliases.Score(ModAliases.Normalize(alias), normalizedQuery);
                        if (score <= bestScore) continue;

                        bestScore = score;
                        best = english;
                    }
                }
            }
        }

        return bestScore >= 50 ? best : null;
    }

    /// <summary>标题可能是「铁箱子 (Iron Chest) - 更多箱子 (Iron Chests)」，逐段拆开。</summary>
    private static IEnumerable<(string Chinese, string English)> SplitTitle(string title)
    {
        foreach (var segment in title.Split(" - ", StringSplitOptions.RemoveEmptyEntries))
        {
            var match = TitlePairRegex().Match(segment);
            if (!match.Success) continue;

            var chinese = TagPrefixRegex().Replace(match.Groups[1].Value, string.Empty).Trim();
            var english = match.Groups[2].Value.Trim();
            if (chinese.Length == 0 || english.Length == 0) continue;

            yield return (chinese, english);
        }
    }

    private static bool IsFresh(CacheEntry entry)
        => DateTimeOffset.UtcNow - entry.ResolvedAt < (entry.English is null ? MissFreshness : HitFreshness);

    // ------------------------------------------------------------ 缓存文件

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (FileLock)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(CachePath)) return;

                var file = JsonSerializer.Deserialize<AliasCacheFile>(File.ReadAllBytes(CachePath), JsonOptions);
                if (file?.Entries is null) return;

                foreach (var (key, entry) in file.Entries)
                {
                    if (string.IsNullOrWhiteSpace(key) || entry is null) continue;
                    Entries[key] = new CacheEntry(entry.English, entry.ResolvedAt);
                }
            }
            catch (Exception exception)
            {
                LauncherLog.Warn($"读取中文别名缓存失败：{exception.Message}");
            }
        }
    }

    private static void Store(string key, CacheEntry entry)
    {
        Entries[key] = entry;

        lock (FileLock)
        {
            try
            {
                var file = new AliasCacheFile();
                foreach (var (name, value) in Entries)
                    file.Entries[name] = new AliasCacheEntry { English = value.English, ResolvedAt = value.ResolvedAt };

                var directory = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var temp = CachePath + ".tmp";
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions));
                File.Move(temp, CachePath, overwrite: true);
            }
            catch (Exception exception)
            {
                LauncherLog.Warn($"保存中文别名缓存失败：{exception.Message}");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            MaxConnectionsPerServer = 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // 站点会拦截非浏览器 UA，这里只用于这个搜索页。
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.mcmod.cn/");
        return client;
    }

    // ------------------------------------------------------------ 解析

    [GeneratedRegex(@"<div class=""result-item"">(.*?)<div class=""foot"">", RegexOptions.Singleline)]
    private static partial Regex ResultItemRegex();

    [GeneratedRegex(@"<a\s+[^>]*href=""[^""]*mcmod\.cn/class/\d+\.html""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex TitleLinkRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^(.*?)\s*\(([^()]+)\)\s*$")]
    private static partial Regex TitlePairRegex();

    [GeneratedRegex(@"^\[[^\]]*\]\s*")]
    private static partial Regex TagPrefixRegex();

    // ------------------------------------------------------------ 数据类型

    private readonly record struct CacheEntry(string? English, DateTimeOffset ResolvedAt);

    private sealed class AliasCacheFile
    {
        public Dictionary<string, AliasCacheEntry> Entries { get; set; } = new();
    }

    private sealed class AliasCacheEntry
    {
        public string? English { get; set; }
        public DateTimeOffset ResolvedAt { get; set; }
    }
}
