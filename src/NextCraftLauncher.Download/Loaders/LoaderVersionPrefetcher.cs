using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;
using NextCraftLauncher.Download.Minecraft;

namespace NextCraftLauncher.Download.Loaders;

/// <summary>
/// 后台预取核心：启动后慢慢遍历游戏版本，逐个获取 Forge / Fabric / NeoForge
/// 的加载器版本列表，并写入 <see cref="LoaderVersionStore"/>。
/// </summary>
/// <remarks>
/// 设计要点：
/// 1. 只处理正式版和快照，<c>old_beta / old_alpha</c> 没有可用加载器，直接跳过；
/// 2. 正式版优先、新版本优先，用户最可能点开的版本会最先就绪；
/// 3. 每个版本的三次请求并发发出，版本之间随机停顿，避免给镜像站造成压力；
/// 4. 本地缓存保留 72 小时且持续刷新：每扫描完一轮休眠 1 小时再检查，
///    未过期的版本直接跳过，过期或缺失的重新获取。
/// </remarks>
public sealed class LoaderVersionPrefetcher
{
    public static readonly IReadOnlyList<string> DefaultLoaders = new[] { "forge", "fabric", "neoforge" };

    private const int MinDelayMilliseconds = 350;
    private const int MaxDelayMilliseconds = 900;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ManifestCacheFreshness = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly Func<string, DownloadMirror> _mirrorProvider;
    private readonly Func<bool>? _isBusy;
    private readonly IReadOnlyList<string> _loaders;

    public LoaderVersionPrefetcher(
        Func<string, DownloadMirror>? mirrorProvider = null,
        IReadOnlyList<string>? loaders = null,
        Func<bool>? isBusy = null)
    {
        _mirrorProvider = mirrorProvider ?? (_ => DownloadMirror.Automatic);
        _loaders = loaders ?? DefaultLoaders;
        _isBusy = isBusy;
    }

    /// <summary>某个加载器版本列表写入缓存后触发（参数：加载器、游戏版本、结果）。</summary>
    public event Action<string, string, LoaderVersionResult>? LoaderVersionsCached;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // 启动时先让界面自己的清单请求完成并写入缓存，避免重复下载。
        await Task.Delay(StartupDelay, cancellationToken);

        while (true)
        {
            await RunPassAsync(cancellationToken);

            // 一轮结束后休眠再检查：缓存始终按 72 小时的有效期持续更新。
            await Task.Delay(RefreshInterval, cancellationToken);
        }
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        LoaderVersionStore.PruneExpired();
        PrismLoaderIndex.CleanupLegacyCaches();

        LauncherLog.Info("后台预取：开始扫描");

        // 提前把 Forge / NeoForge 索引拉到本地，详情页首次打开时不用再下载。
        await WarmIndexesAsync(cancellationToken);

        var manifestMirror = _mirrorProvider("game");

        MinecraftVersionManifestResult? manifest = null;
        if (IsManifestCacheFresh())
            manifest = MinecraftVersionCatalog.TryLoadCached();

        if (manifest is null)
        {
            try
            {
                manifest = await MinecraftVersionCatalog.LoadAsync(
                    mirror: manifestMirror,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 网络不可用时退回上次的清单缓存，仍然可以继续补全加载器数据。
                LauncherLog.Warn($"后台预取：获取版本清单失败，使用缓存（{exception.Message}）");
                manifest = MinecraftVersionCatalog.TryLoadCached();
            }
        }

        if (manifest is null) return;

        foreach (var version in OrderForPrefetch(manifest.Versions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsFullyCached(version.Id)) continue;

            // 用户正在下载 / 安装时暂停后台预取，把带宽让给前台任务。
            await WaitWhileBusyAsync(cancellationToken);

            await PrefetchVersionAsync(version.Id, cancellationToken);

            await Task.Delay(
                Random.Shared.Next(MinDelayMilliseconds, MaxDelayMilliseconds + 1),
                cancellationToken);
        }
    }

    private async Task WarmIndexesAsync(CancellationToken cancellationToken)
    {
        foreach (var component in new[] { PrismLoaderIndex.Forge, PrismLoaderIndex.NeoForge })
        {
            try
            {
                await PrismLoaderIndex.PreloadAsync(component, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 索引不可用时由详情页按需重试。
                LauncherLog.Warn($"后台预取：{component.Uid} 索引预加载失败（{exception.Message}）");
            }
        }
    }

    private async Task WaitWhileBusyAsync(CancellationToken cancellationToken)
    {
        while (_isBusy?.Invoke() == true)
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
    }

    private async Task PrefetchVersionAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var tasks = _loaders.Select(loader =>
            PrefetchLoaderAsync(loader, minecraftVersion, cancellationToken));

        await Task.WhenAll(tasks);
    }

    private async Task PrefetchLoaderAsync(
        string loader,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var mirror = _mirrorProvider(loader);
            var result = await LoaderVersionCatalog.LoadAsync(
                loader,
                minecraftVersion,
                mirror,
                cancellationToken);

            if (result.Error is null)
                LoaderVersionsCached?.Invoke(loader, minecraftVersion, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 单个加载器失败不影响其余版本，下一轮会重试。
            LauncherLog.Warn($"后台预取：{loader} {minecraftVersion} 失败（{exception.Message}）");
        }
    }

    private static bool IsManifestCacheFresh()
    {
        try
        {
            var path = MinecraftVersionCatalog.CacheFilePath;
            return File.Exists(path) &&
                   DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < ManifestCacheFreshness;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsFullyCached(string minecraftVersion)
        => _loaders.All(loader =>
            LoaderVersionStore.TryLoad(loader, minecraftVersion, out _, out var updatedAt) &&
            LoaderVersionStore.IsFresh(updatedAt));

    private static IEnumerable<MinecraftVersionEntry> OrderForPrefetch(
        IReadOnlyList<MinecraftVersionEntry> versions)
    {
        foreach (var version in versions)
        {
            if (IsType(version, "release")) yield return version;
        }

        foreach (var version in versions)
        {
            if (IsType(version, "snapshot")) yield return version;
        }
    }

    private static bool IsType(MinecraftVersionEntry version, string type)
        => string.Equals(version.Type, type, StringComparison.OrdinalIgnoreCase);
}
