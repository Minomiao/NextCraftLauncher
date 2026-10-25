using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Loaders;

/// <summary>Prism 元数据中的一个加载器组件。</summary>
public sealed record PrismLoaderComponent(string Uid, string CacheFileName);

/// <summary>Prism 索引中的一条加载器版本。</summary>
public sealed record PrismLoaderVersion(string Version, bool Recommended);

/// <summary>
/// Prism Launcher 元数据索引（GitHub Pages）。
/// 一次请求即可拿到某个加载器的全部版本，并按 <c>requires</c> 的 Minecraft 版本分组。
/// 目前用于 Forge 与 NeoForge：官方 maven 在部分网络下不可达，
/// 索引比逐个请求 maven 元数据更快、更稳，还自带 recommended 标记。
/// </summary>
/// <remarks>
/// Fabric 没有使用该索引：Prism 的 Fabric Loader 条目只声明依赖 intermediary，
/// 不包含 MC 版本，无法像 Forge / NeoForge 那样按游戏版本过滤。
/// </remarks>
public static class PrismLoaderIndex
{
    public static readonly PrismLoaderComponent NeoForge = new("net.neoforged", "neoforge");
    public static readonly PrismLoaderComponent Forge = new("net.minecraftforge", "forge");

    private const string IndexUrlTemplate = "https://meta.prismlauncher.org/v1/{0}/index.json";

    /// <summary>磁盘索引的有效期，过期后重新下载。</summary>
    private static readonly TimeSpan DiskFreshness = TimeSpan.FromHours(6);

    /// <summary>进程内解析结果的有效期。</summary>
    private static readonly TimeSpan MemoryFreshness = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly ConcurrentDictionary<string, ComponentState> States =
        new(StringComparer.OrdinalIgnoreCase);

    public static string CacheFilePath(PrismLoaderComponent component) => Path.Combine(
        AppSettings.DataDirectory,
        "cache",
        "indexes",
        component.CacheFileName + ".json");

    /// <summary>返回某个 Minecraft 版本可用的加载器版本（保持索引中的顺序）。</summary>
    public static async Task<IReadOnlyList<PrismLoaderVersion>> GetVersionsAsync(
        PrismLoaderComponent component,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        await PreloadAsync(component, cancellationToken);

        var state = States[component.Uid];
        return state.ByMinecraft.TryGetValue(minecraftVersion, out var versions)
            ? versions
            : Array.Empty<PrismLoaderVersion>();
    }

    /// <summary>提前下载并解析索引，避免用户首次打开详情页时才付这笔时间。</summary>
    public static async Task PreloadAsync(
        PrismLoaderComponent component,
        CancellationToken cancellationToken = default)
    {
        var state = States.GetOrAdd(component.Uid, _ => new ComponentState());
        await EnsureLoadedAsync(component, state, cancellationToken);
    }

    /// <summary>清理旧版本使用的分散缓存目录。</summary>
    public static void CleanupLegacyCaches()
    {
        try
        {
            var legacy = Path.Combine(AppSettings.DataDirectory, "cache", "neoforge");
            if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true);
        }
        catch (Exception)
        {
            // 被占用时下次再清理。
        }
    }

    private static async Task EnsureLoadedAsync(
        PrismLoaderComponent component,
        ComponentState state,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - state.LoadedAt < MemoryFreshness) return;

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow - state.LoadedAt < MemoryFreshness) return;

            var bytes = TryReadDiskCache(component, freshOnly: true);
            if (bytes is null)
            {
                try
                {
                    // 索引托管在 GitHub Pages，与下载源无关。
                    bytes = await DownloadClient.Shared.DownloadBytesAutoAsync(
                        new[] { string.Format(IndexUrlTemplate, component.Uid) },
                        DownloadMirror.Automatic,
                        cancellationToken: cancellationToken);
                    TrySaveDiskCache(component, bytes);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // 网络失败时允许使用过期索引，至少还能列出已有版本。
                    bytes = TryReadDiskCache(component, freshOnly: false);
                }
            }

            if (bytes is null)
                throw new IOException($"无法获取 {component.Uid} 版本索引。");

            Parse(state, bytes);
            state.LoadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static void Parse(ComponentState state, byte[] bytes)
    {
        var data = JsonSerializer.Deserialize<PrismIndex>(bytes, JsonOptions);
        if (data?.Versions is null)
            throw new InvalidDataException("加载器版本索引格式不正确。");

        var grouped = new Dictionary<string, List<PrismLoaderVersion>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in data.Versions)
        {
            if (string.IsNullOrWhiteSpace(entry.Version)) continue;

            var minecraft = entry.Requires?
                .FirstOrDefault(requirement =>
                    string.Equals(requirement.Uid, "net.minecraft", StringComparison.OrdinalIgnoreCase))?
                .MinecraftVersion;

            if (string.IsNullOrWhiteSpace(minecraft)) continue;

            if (!grouped.TryGetValue(minecraft, out var list))
                grouped[minecraft] = list = new List<PrismLoaderVersion>();

            list.Add(new PrismLoaderVersion(entry.Version, entry.Recommended));
        }

        var result = new Dictionary<string, IReadOnlyList<PrismLoaderVersion>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (minecraft, versions) in grouped)
            result[minecraft] = versions;

        state.ByMinecraft = result;
    }

    private static byte[]? TryReadDiskCache(PrismLoaderComponent component, bool freshOnly)
    {
        try
        {
            var path = CacheFilePath(component);
            if (!File.Exists(path)) return null;

            if (freshOnly && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= DiskFreshness)
                return null;

            return File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TrySaveDiskCache(PrismLoaderComponent component, byte[] bytes)
    {
        try
        {
            var path = CacheFilePath(component);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);

            CleanupLegacyCaches();
        }
        catch (Exception)
        {
            // 缓存写入失败不影响本次结果。
        }
    }

    private sealed class ComponentState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, IReadOnlyList<PrismLoaderVersion>> ByMinecraft { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset LoadedAt { get; set; }
    }

    private sealed class PrismIndex
    {
        [JsonPropertyName("versions")] public List<PrismIndexEntry>? Versions { get; set; }
    }

    private sealed class PrismIndexEntry
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("recommended")] public bool Recommended { get; set; }
        [JsonPropertyName("requires")] public List<PrismRequirement>? Requires { get; set; }
    }

    private sealed class PrismRequirement
    {
        [JsonPropertyName("uid")] public string? Uid { get; set; }
        [JsonPropertyName("equals")] public string? MinecraftVersion { get; set; }
    }
}
