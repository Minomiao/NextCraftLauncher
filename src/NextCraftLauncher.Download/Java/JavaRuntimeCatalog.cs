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

    public static async Task<JavaRuntimeCatalogResult> LoadAsync(
        DownloadClient? client = null,
        DownloadMirror mirror = DownloadMirror.Automatic,
        CancellationToken cancellationToken = default)
    {
        var downloader = client ?? DownloadClient.Shared;

        var urls = DownloadUrlResolver.Build(JavaRuntimeDownloader.OfficialIndexUrl, mirror);

        var bytes = await downloader.DownloadBytesAutoAsync(urls, mirror, cancellationToken: cancellationToken);

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
