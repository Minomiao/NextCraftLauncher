using NextCraftLauncher.Core.Logging;

namespace NextCraftLauncher.Download.Mods;

/// <summary>
/// 后台静默预取 Mod 数据：按页拉取搜索结果，并逐个工程拉取文件列表，
/// 优先处理下载量高的工程，直到缓存达到上限或全部完成。
/// 使用低优先级的后台线程，不占用前台交互的 CPU。
/// </summary>
public static class ModPrefetcher
{
    /// <summary>每个工程之间稍作停顿，避免把接口打满。</summary>
    private static readonly TimeSpan ProjectDelay = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(400);

    private static readonly object Gate = new();

    private static CancellationTokenSource? _current;

    /// <summary>
    /// 为指定搜索条件启动预取；重复调用会取消上一轮。
    /// </summary>
    public static void Start(
        ModPlatform? platform,
        string query,
        string? gameVersion,
        string? loader,
        CancellationToken lifetime)
    {
        CancellationToken token;
        lock (Gate)
        {
            _current?.Cancel();
            _current?.Dispose();

            var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _current = cts;
            token = cts.Token;
        }

        var thread = new Thread(() => Run(platform, query, gameVersion, loader, token))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "ModPrefetcher",
        };

        thread.Start();
    }

    private static void Run(
        ModPlatform? platform,
        string query,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        try
        {
            RunAsync(platform, query, gameVersion, loader, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"Mod 预取失败：{exception.Message}");
        }
    }

    private static async Task RunAsync(
        ModPlatform? platform,
        string query,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        ModCacheBudget.EnsureMeasured();

        var prefetched = 0;
        var failures = 0;

        for (var page = 1; !cancellationToken.IsCancellationRequested; page++)
        {
            if (!ModCacheBudget.HasRoom())
            {
                LauncherLog.Info("Mod 缓存已达上限，停止预取");
                return;
            }

            ModSearchPage result;
            try
            {
                result = await ModCatalog.SearchAsync(
                    platform, query, gameVersion, loader, page, ignoreCache: false, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LauncherLog.Warn($"Mod 预取第 {page} 页失败：{exception.Message}");
                if (++failures >= 3) return;

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                continue;
            }

            if (result.Projects.Count == 0) break;
            failures = 0;
            LauncherLog.Info($"Mod 预取：第 {page} 页，{result.Projects.Count} 个工程");

            // 热门工程优先，打开详情时命中率更高。
            var handled = 0;
            foreach (var project in result.Projects.OrderByDescending(project => project.Downloads))
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (!ModCacheBudget.HasRoom()) return;

                if (ModVersionCache.IsFresh(project))
                {
                    prefetched++;
                    handled++;
                    continue;
                }

                try
                {
                    await ModCatalog.EnsureVersionsCachedAsync(project, cancellationToken);
                    prefetched++;
                    handled++;
                    failures = 0;

                    if (handled % 10 == 0)
                        LauncherLog.Info($"Mod 预取进度：第 {page} 页已处理 {handled} 个工程");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LauncherLog.Warn($"预取工程文件列表失败（{project.Name}）：{exception.Message}");
                    if (++failures >= 5) return;

                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }

                await Task.Delay(ProjectDelay, cancellationToken);
            }

            if (page >= result.TotalPages) break;
            await Task.Delay(PageDelay, cancellationToken);
        }

        LauncherLog.Info($"Mod 预取结束：已缓存 {prefetched} 个工程的文件列表");
    }
}
