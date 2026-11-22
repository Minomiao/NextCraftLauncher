using System.Text.Json;
using System.Text.RegularExpressions;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Java;

public sealed record JavaRuntimeCatalogEntry(
    string ComponentName,
    string VersionName,
    int MajorVersion,
    DateTimeOffset? Released,
    string Platform,
    string ManifestUrl,
    string? ManifestSha1,
    long ManifestSize);

public sealed record JavaRuntimeCatalogResult(
    string Platform,
    IReadOnlyList<JavaRuntimeCatalogEntry> Runtimes);

/// <summary>
/// 列出 Mojang 为当前平台提供的全部 Java 运行时（jre-legacy、java-runtime-*）。
/// </summary>
public static class JavaRuntimeCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>最近一次成功获取的清单缓存，用于启动和离线时展示。</summary>
    public static string CacheFilePath => Path.Combine(
        AppSettings.DataDirectory,
        "cache",
        "java_runtime_index.json");

    /// <summary>读取上次缓存的清单；不存在或损坏时返回 null。</summary>
    public static JavaRuntimeCatalogResult? TryLoadCached()
    {
        try
        {
            if (!File.Exists(CacheFilePath)) return null;
            return Parse(File.ReadAllBytes(CacheFilePath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TrySaveCache(byte[] bytes)
    {
        try
        {
            var directory = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllBytes(CacheFilePath, bytes);
        }
        catch (Exception)
        {
            // 缓存写入失败不影响下载。
        }
    }

    public static async Task<JavaRuntimeCatalogResult> LoadAsync(
        DownloadClient? client = null,
        DownloadMirror mirror = DownloadMirror.Automatic,
        CancellationToken cancellationToken = default)
    {
        var downloader = client ?? DownloadClient.Shared;

        var urls = DownloadUrlResolver.Build(JavaRuntimeDownloader.OfficialIndexUrl, mirror);

        var bytes = await downloader.DownloadBytesAutoAsync(urls, mirror, cancellationToken: cancellationToken);

        // 解析与缓存写入都是 CPU/磁盘操作，放到线程池，避免阻塞调用方（UI 线程）。
        return await Task.Run(() =>
        {
            var result = Parse(bytes);
            TrySaveCache(bytes);
            return result;
        }, cancellationToken);
    }

    private static JavaRuntimeCatalogResult Parse(byte[] bytes)
    {
        var index = JsonSerializer.Deserialize<
                        Dictionary<string, Dictionary<string, List<JavaRuntimeIndexEntry>>>>(
                        bytes, JsonOptions)
                    ?? throw new InvalidDataException("无法解析 Java 运行时清单。");

        var platform = JavaRuntimePlatform.GetKey();
        if (!index.TryGetValue(platform, out var components) || components.Count == 0)
            throw new InvalidOperationException($"Java 运行时清单中没有平台 {platform} 的数据。");

        var runtimes = new List<JavaRuntimeCatalogEntry>();

        foreach (var (componentName, entries) in components)
        {
            // minecraft-java-exe 不是 Java 运行时。
            if (string.Equals(componentName, "minecraft-java-exe", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var entry in entries)
            {
                if (entry.Manifest?.Url is not { Length: > 0 } manifestUrl) continue;

                var versionName = entry.Version?.Name ?? string.Empty;
                runtimes.Add(new JavaRuntimeCatalogEntry(
                    componentName,
                    versionName,
                    ParseMajor(versionName),
                    ParseReleased(entry.Version?.Released),
                    platform,
                    manifestUrl,
                    string.IsNullOrWhiteSpace(entry.Manifest.Sha1) ? null : entry.Manifest.Sha1,
                    entry.Manifest.Size));
            }
        }

        runtimes.Sort(static (a, b) =>
        {
            var major = b.MajorVersion.CompareTo(a.MajorVersion);
            return major != 0
                ? major
                : string.Compare(a.ComponentName, b.ComponentName, StringComparison.OrdinalIgnoreCase);
        });

        return new JavaRuntimeCatalogResult(platform, runtimes);
    }

    private static int ParseMajor(string? versionName)
    {
        if (string.IsNullOrWhiteSpace(versionName)) return 0;
        var match = Regex.Match(versionName, @"\d+");
        return match.Success && int.TryParse(match.Value, out var major) ? major : 0;
    }

    private static DateTimeOffset? ParseReleased(string? released)
        => DateTimeOffset.TryParse(released, out var value) ? value : null;
}
