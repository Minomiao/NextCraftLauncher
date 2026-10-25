using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Loaders;

/// <summary>一个加载器版本，<see cref="Tag"/> 是“推荐 / 最新 / 稳定”等展示标记。</summary>
public sealed record LoaderVersion(string Version, string? Tag);

/// <summary>加载器版本列表；获取失败时 <see cref="Error"/> 非空，<see cref="Versions"/> 为空。</summary>
public sealed record LoaderVersionResult(IReadOnlyList<LoaderVersion> Versions, string? Error);

/// <summary>
/// 获取 Forge / Fabric / NeoForge 某个 Minecraft 版本的加载器版本列表。
/// 仅用于展示，不涉及安装。
/// </summary>
public static class LoaderVersionCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<string> Versions)> ForgeMavenCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forge 完整 maven 元数据体积较大，进程内短暂缓存，避免重复下载。</summary>
    private static readonly TimeSpan ForgeMavenCacheDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 从网络获取加载器列表并写入本地缓存。
    /// 调用方先用 <see cref="LoaderVersionStore.TryLoad"/> 展示缓存，再调用本方法刷新；
    /// 网络失败时回退到 72 小时内的缓存，避免已有数据退化成「获取失败」。
    /// </summary>
    public static async Task<LoaderVersionResult> LoadAsync(
        string loader,
        string minecraftVersion,
        DownloadMirror mirror,
        CancellationToken cancellationToken = default)
    {
        LoaderVersionResult? stored = null;
        if (LoaderVersionStore.TryLoad(loader, minecraftVersion, out var storedResult, out var updatedAt) &&
            LoaderVersionStore.IsFresh(updatedAt))
        {
            stored = storedResult;
        }

        LoaderVersionResult result;

        try
        {
            result = loader.ToLowerInvariant() switch
            {
                "fabric" => await LoadFabricAsync(minecraftVersion, mirror, cancellationToken),
                "forge" => await LoadForgeAsync(minecraftVersion, mirror, cancellationToken),
                "neoforge" => await LoadNeoForgeAsync(minecraftVersion, mirror, cancellationToken),
                _ => new LoaderVersionResult(Array.Empty<LoaderVersion>(), null),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = new LoaderVersionResult(Array.Empty<LoaderVersion>(), exception.Message);
        }

        if (result.Error is null)
        {
            LoaderVersionStore.Save(loader, minecraftVersion, result);
            return result;
        }

        return stored ?? result;
    }

    // ---------------------------------------------------------------- Fabric

    private static async Task<LoaderVersionResult> LoadFabricAsync(
        string minecraftVersion,
        DownloadMirror mirror,
        CancellationToken cancellationToken)
    {
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}";
        var bytes = await DownloadAsync(url, mirror, cancellationToken);
        if (bytes is null)
            return new LoaderVersionResult(Array.Empty<LoaderVersion>(), null);

        var entries = JsonSerializer.Deserialize<List<FabricLoaderEntry>>(bytes, JsonOptions)
                      ?? new List<FabricLoaderEntry>();

        var versions = new List<LoaderVersion>(entries.Count);
        foreach (var entry in entries)
        {
            var version = entry.Loader?.Version;
            if (string.IsNullOrWhiteSpace(version)) continue;
            versions.Add(new LoaderVersion(version, entry.Loader!.Stable ? "稳定" : null));
        }

        return new LoaderVersionResult(versions, null);
    }

    // ----------------------------------------------------------------- Forge

    private static async Task<LoaderVersionResult> LoadForgeAsync(
        string minecraftVersion,
        DownloadMirror mirror,
        CancellationToken cancellationToken)
    {
        // 「自动」先走 Prism 索引：一次请求拿到该 MC 版本的全部 Forge 版本，
        // 自带 recommended 标记，也不依赖 BMCL / 官方 maven 的可达性。
        if (mirror == DownloadMirror.Automatic)
        {
            var indexed = await TryLoadForgeIndexAsync(minecraftVersion, cancellationToken);
            if (indexed is not null) return indexed;
        }

        // 推荐标记与版本列表互不依赖，先并行发出，避免串行叠加两轮网络等待。
        var recommendedTask = TryGetForgeRecommendedAsync(minecraftVersion, mirror, cancellationToken);

        var useBmcl = mirror is not (DownloadMirror.Official or DownloadMirror.MojangCdn);
        List<string> versions;

        if (useBmcl)
        {
            try
            {
                versions = await LoadForgeBuildsFromBmclAsync(minecraftVersion, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                versions = new List<string>();
            }

            // 自动模式允许在 BMCL 没有结果时回退官方 maven；显式 BMCL 模式不回退。
            if (versions.Count == 0 && mirror != DownloadMirror.BmclApi)
                versions = await LoadForgeMavenVersionsAsync(minecraftVersion, mirror, cancellationToken);
        }
        else
        {
            versions = await LoadForgeMavenVersionsAsync(minecraftVersion, mirror, cancellationToken);
        }

        versions = versions
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, VersionStringComparer.Instance)
            .ToList();

        var recommended = await recommendedTask;
        var result = BuildForgeResult(versions, recommended);

        // 显式选择的源没有结果时，再用索引兜底。
        if (result.Versions.Count == 0)
        {
            var indexed = await TryLoadForgeIndexAsync(minecraftVersion, cancellationToken);
            if (indexed is not null) return indexed;
        }

        return result;
    }

    private static LoaderVersionResult BuildForgeResult(
        IReadOnlyList<string> versions,
        string? recommended)
    {
        var result = new List<LoaderVersion>(versions.Count);
        for (var index = 0; index < versions.Count; index++)
        {
            var version = versions[index];
            string? tag = null;

            if (recommended is not null &&
                string.Equals(version, recommended, StringComparison.OrdinalIgnoreCase))
            {
                tag = "推荐";
            }
            else if (index == 0)
            {
                tag = "最新";
            }

            result.Add(new LoaderVersion(version, tag));
        }

        return new LoaderVersionResult(result, null);
    }

    private static async Task<LoaderVersionResult?> TryLoadForgeIndexAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var indexed = await PrismLoaderIndex.GetVersionsAsync(
                PrismLoaderIndex.Forge, minecraftVersion, cancellationToken);
            if (indexed.Count == 0) return null;

            var result = new List<LoaderVersion>(indexed.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in indexed)
            {
                if (string.IsNullOrWhiteSpace(entry.Version)) continue;
                if (!seen.Add(entry.Version)) continue;

                var tag = entry.Recommended ? "推荐" : result.Count == 0 ? "最新" : null;
                result.Add(new LoaderVersion(entry.Version, tag));
            }

            return result.Count > 0 ? new LoaderVersionResult(result, null) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<List<string>> LoadForgeBuildsFromBmclAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var url = $"https://bmclapi2.bangbang93.com/forge/minecraft/{Uri.EscapeDataString(minecraftVersion)}";
        var bytes = await DownloadAsync(url, DownloadMirror.BmclApi, cancellationToken);
        if (bytes is null) return new List<string>();

        var entries = JsonSerializer.Deserialize<List<ForgeBuildEntry>>(bytes, JsonOptions)
                      ?? new List<ForgeBuildEntry>();

        return entries
            .Select(entry => entry.Version)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Select(version => version!)
            .ToList();
    }

    private static async Task<List<string>> LoadForgeMavenVersionsAsync(
        string minecraftVersion,
        DownloadMirror mirror,
        CancellationToken cancellationToken)
    {
        var all = await GetForgeMavenVersionsAsync(mirror, cancellationToken);
        var prefix = minecraftVersion + "-";

        return all
            .Where(version => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(version => version[prefix.Length..])
            .ToList();
    }

    private static async Task<IReadOnlyList<string>> GetForgeMavenVersionsAsync(
        DownloadMirror mirror,
        CancellationToken cancellationToken)
    {
        var key = ((int)mirror).ToString();
        if (ForgeMavenCache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < ForgeMavenCacheDuration)
        {
            return cached.Versions;
        }

        const string url = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
        var bytes = await DownloadAsync(url, mirror, cancellationToken) ?? Array.Empty<byte>();
        var versions = ParseMavenVersions(bytes);

        ForgeMavenCache[key] = (DateTimeOffset.UtcNow, versions);
        return versions;
    }

    private static async Task<string?> TryGetForgeRecommendedAsync(
        string minecraftVersion,
        DownloadMirror mirror,
        CancellationToken cancellationToken)
    {
        try
        {
            const string url = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
            var bytes = await DownloadAsync(url, mirror, cancellationToken);
            if (bytes is null) return null;

            var promotions = JsonSerializer.Deserialize<ForgePromotions>(bytes, JsonOptions);
            if (promotions?.Promos is not { } promos) return null;

            return promos.TryGetValue($"{minecraftVersion}-recommended", out var recommended)
                ? recommended
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // -------------------------------------------------------------- NeoForge

    private static async Task<LoaderVersionResult> LoadNeoForgeAsync(
        string minecraftVersion,
        DownloadMirror mirror,
        CancellationToken cancellationToken)
    {
        var (url, prefix, stripPrefix) = GetNeoForgeSource(minecraftVersion);

        // 「自动」或 maven 不可达时，优先用 Prism 索引：一次拿到全部版本。
        if (mirror == DownloadMirror.Automatic)
        {
            var indexed = await TryLoadNeoForgeIndexAsync(minecraftVersion, cancellationToken);
            if (indexed is not null) return indexed;
        }

        Exception? lastError = null;

        if (url is not null)
        {
            try
            {
                var bytes = await DownloadAsync(url, mirror, cancellationToken);
                if (bytes is not null)
                {
                    var mavenVersions = ParseMavenVersions(bytes)
                        .Where(version => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(version => stripPrefix ? version[prefix.Length..] : version)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(version => version, VersionStringComparer.Instance)
                        .ToList();

                    return BuildNeoForgeResult(mavenVersions);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
        }

        // 显式选择的源不可用时，再用索引兜底，至少能展示版本列表。
        var fallback = await TryLoadNeoForgeIndexAsync(minecraftVersion, cancellationToken);
        if (fallback is not null) return fallback;

        return lastError is null
            ? new LoaderVersionResult(Array.Empty<LoaderVersion>(), null)
            : new LoaderVersionResult(Array.Empty<LoaderVersion>(), lastError.Message);
    }

    /// <summary>从 Prism 索引取某个 MC 版本的 NeoForge 列表；无数据时返回 null。</summary>
    private static async Task<LoaderVersionResult?> TryLoadNeoForgeIndexAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var indexed = await PrismLoaderIndex.GetVersionsAsync(
                PrismLoaderIndex.NeoForge, minecraftVersion, cancellationToken);
            if (indexed.Count == 0) return null;

            // Prism 索引本身已按新→旧排列，保持原顺序，不对带 -beta 后缀的版本重排。
            var versions = new List<string>(indexed.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in indexed)
            {
                if (string.IsNullOrWhiteSpace(entry.Version)) continue;
                if (!seen.Add(entry.Version)) continue;
                versions.Add(entry.Version);
            }

            return BuildNeoForgeResult(versions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static LoaderVersionResult BuildNeoForgeResult(IReadOnlyList<string> versions)
    {
        var result = new List<LoaderVersion>(versions.Count);
        for (var index = 0; index < versions.Count; index++)
            result.Add(new LoaderVersion(versions[index], index == 0 ? "最新" : null));

        return new LoaderVersionResult(result, null);
    }

    /// <summary>
    /// NeoForge 1.20.1 位于 net/neoforged/forge，版本形如 1.20.1-47.1.x；
    /// 1.20.2 之后位于 net/neoforged/neoforge，版本形如 21.1.80（对应 MC 1.21.1）。
    /// </summary>
    private static (string? Url, string Prefix, bool StripPrefix) GetNeoForgeSource(string minecraftVersion)
    {
        const string NeoForgeMetadata =
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

        if (string.Equals(minecraftVersion, "1.20.1", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml",
                "1.20.1-",
                true);
        }

        var parts = minecraftVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var majorVersion) ||
            !int.TryParse(parts[1], out var minor))
        {
            return (null, string.Empty, false);
        }

        // 新的年份式版本号（如 26.3）：NeoForge 与 MC 同号，版本形如 26.3.0.x。
        if (majorVersion != 1)
            return (NeoForgeMetadata, $"{parts[0]}.{parts[1]}.", false);

        var patch = parts.Length > 2 && int.TryParse(parts[2], out var parsedPatch)
            ? parsedPatch
            : 0;

        // NeoForge 从 1.20.1 才开始，1.20.2 之后才有独立的 neoforge 仓库。
        if (minor < 20 || (minor == 20 && patch < 2))
            return (null, string.Empty, false);

        return (NeoForgeMetadata, $"{minor}.{patch}.", false);
    }

    // ---------------------------------------------------------------- 工具

    private static async Task<byte[]?> DownloadAsync(
        string url,
        DownloadMirror mirror,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadClient.Shared.DownloadBytesAutoAsync(
                DownloadUrlResolver.Build(url, mirror),
                mirror,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsMissing(exception))
        {
            // 该加载器没有这个 Minecraft 版本，按“无可用版本”处理。
            return null;
        }
    }

    private static bool IsMissing(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException
                {
                    StatusCode: HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.NoContent,
                })
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ParseMavenVersions(byte[] bytes)
    {
        if (bytes.Length == 0) return Array.Empty<string>();

        try
        {
            var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            return document
                .Descendants("version")
                .Select(element => element.Value.Trim())
                .Where(version => version.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private sealed class VersionStringComparer : IComparer<string>
    {
        public static VersionStringComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (x is null) return y is null ? 0 : -1;
            if (y is null) return 1;

            if (System.Version.TryParse(x, out var left) &&
                System.Version.TryParse(y, out var right))
            {
                return left.CompareTo(right);
            }

            // 带 -beta 等后缀的版本（如 21.8.10-beta）按数字主体比较。
            if (TryParseNumericCore(x, out var leftCore) && TryParseNumericCore(y, out var rightCore))
                return leftCore.CompareTo(rightCore);

            return string.CompareOrdinal(x, y);
        }

        private static bool TryParseNumericCore(string value, out System.Version version)
        {
            var dash = value.IndexOf('-');
            var core = dash >= 0 ? value[..dash] : value;
            return System.Version.TryParse(core, out version!);
        }
    }

    private sealed class FabricLoaderEntry
    {
        [JsonPropertyName("loader")] public FabricLoaderInfo? Loader { get; set; }
    }

    private sealed class FabricLoaderInfo
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("stable")] public bool Stable { get; set; }
    }

    private sealed class ForgeBuildEntry
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
    }

    private sealed class ForgePromotions
    {
        [JsonPropertyName("promos")] public Dictionary<string, string>? Promos { get; set; }
    }
}
