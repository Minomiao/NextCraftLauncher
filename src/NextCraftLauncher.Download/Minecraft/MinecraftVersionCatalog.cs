using System.Text.Json;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Minecraft;

/// <summary>
/// 获取 Mojang 的完整版本清单（包含正式版、快照和远古版本）。
/// </summary>
public static class MinecraftVersionCatalog
{
    public const string OfficialManifestUrl =
        "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>最近一次成功获取的清单缓存，用于启动时立即展示。</summary>
    public static string CacheFilePath => Path.Combine(
        AppSettings.DataDirectory,
        "cache",
        "version_manifest_v2.json");

    /// <summary>读取上次缓存的清单；不存在或损坏时返回 null。</summary>
    public static MinecraftVersionManifestResult? TryLoadCached()
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

    private static MinecraftVersionManifestResult Parse(byte[] bytes)
    {
        var manifest = JsonSerializer.Deserialize<MinecraftVersionManifest>(bytes, JsonOptions)
                       ?? throw new InvalidDataException("无法解析 Minecraft 版本清单。");

        var versions = manifest.Versions
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .ToList();

        if (versions.Count == 0)
            throw new InvalidDataException("Minecraft 版本清单为空。");

        return new MinecraftVersionManifestResult(
            versions,
            manifest.Latest?.Release,
            manifest.Latest?.Snapshot);
    }

    public static async Task<MinecraftVersionManifestResult> LoadAsync(
        DownloadClient? client = null,
        DownloadMirror mirror = DownloadMirror.Automatic,
        CancellationToken cancellationToken = default)
    {
        var downloader = client ?? DownloadClient.Shared;

        var urls = DownloadUrlResolver.Build(OfficialManifestUrl, mirror);

        // 多个候选源并行竞速（显式选择 BMCLAPI 时按优先级顺序回退）。
        var bytes = await downloader.DownloadBytesAutoAsync(urls, mirror, cancellationToken: cancellationToken);

        var result = Parse(bytes);
        TrySaveCache(bytes);
        return result;
    }
}
