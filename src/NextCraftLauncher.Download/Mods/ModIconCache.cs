using System.Security.Cryptography;
using System.Text;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Mods;

/// <summary>
/// Mod 图标的本地缓存。目录按「平台 / 工程 ID-链接哈希.扩展名」组织：
/// <code>
/// NCL/cache/mods/icons/modrinth/P7dR8mSH-1f2e3d4c.png
/// NCL/cache/mods/icons/curseforge/238222-9a8b7c6d.webp
/// </code>
/// 同一工程换了图标链接会生成新文件，旧文件在下次下载成功时清掉。
/// 缓存 14 天后在后台重新拉取。
/// </summary>
public static class ModIconCache
{
    public static readonly TimeSpan Freshness = TimeSpan.FromDays(14);

    private const long MaxSize = 4 * 1024 * 1024;
    private const int MaxConcurrentDownloads = 4;

    private static readonly SemaphoreSlim Gate = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private static readonly HttpClient Http = CreateClient();

    public static string RootDirectory => Path.Combine(AppSettings.DataDirectory, "cache", "mods", "icons");

    /// <summary>生成缓存键：<c>平台/工程 ID</c>。</summary>
    public static string MakeKey(ModPlatform platform, string projectId)
        => $"{(platform == ModPlatform.Modrinth ? "modrinth" : "curseforge")}/{SafeName(projectId)}";

    public static bool IsFresh(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists &&
                   info.Length > 0 &&
                   DateTimeOffset.UtcNow - info.LastWriteTimeUtc < Freshness;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>保证图标已缓存，返回本地文件路径；失败时返回 <c>null</c>。</summary>
    public static async Task<string?> EnsureAsync(string cacheKey, string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var path = GetPath(cacheKey, url);
        if (IsFresh(path)) return path;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (IsFresh(path)) return path;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var tempPath = path + ".part";
            Exception? lastError = null;

            foreach (var candidate in ModDownloadUrlResolver.Build(url))
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(20));

                try
                {
                    using var response = await Http.GetAsync(
                        candidate, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                    response.EnsureSuccessStatusCode();

                    if (response.Content.Headers.ContentLength is { } length && length > MaxSize)
                        throw new InvalidDataException($"图标超过 {MaxSize / 1024 / 1024} MB");

                    await using (var input = await response.Content.ReadAsStreamAsync(attempt.Token))
                    await using (var output = new FileStream(
                                     tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        await input.CopyToAsync(output, attempt.Token);
                    }

                    if (new FileInfo(tempPath).Length == 0)
                        throw new InvalidDataException("图标文件为空");

                    File.Move(tempPath, path, overwrite: true);
                    RemoveStale(cacheKey, path);
                    return path;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    TryDelete(tempPath);
                }
            }

            LauncherLog.Warn($"下载 Mod 图标失败（{cacheKey}）：{lastError?.Message}");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string GetPath(string cacheKey, string url)
    {
        var (folder, id) = SplitKey(cacheKey);
        return Path.Combine(
            RootDirectory,
            folder,
            id + "-" + UrlHash(url) + GetExtension(url));
    }

    private static (string Folder, string Id) SplitKey(string cacheKey)
    {
        var index = cacheKey.IndexOf('/');
        if (index <= 0 || index >= cacheKey.Length - 1)
            return ("misc", SafeName(cacheKey));

        var folder = SafeName(cacheKey[..index]);
        var id = SafeName(cacheKey[(index + 1)..]);
        return (folder, id);
    }

    /// <summary>同一个工程只保留当前链接对应的那份图标。</summary>
    private static void RemoveStale(string cacheKey, string currentPath)
    {
        try
        {
            var (folder, id) = SplitKey(cacheKey);
            var directory = Path.Combine(RootDirectory, folder);
            if (!Directory.Exists(directory)) return;

            foreach (var file in Directory.EnumerateFiles(directory, id + "-*"))
            {
                if (string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(file);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响显示。
        }
    }

    private static string GetExtension(string url)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            return extension is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" ? extension : ".png";
        }
        catch (Exception)
        {
            return ".png";
        }
    }

    private static string UrlHash(string url)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))
            .ToLowerInvariant()[..8];

    private static string SafeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        }

        var name = builder.ToString().Trim('-', '.');
        return name.Length == 0 ? "icon" : name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = MaxConcurrentDownloads,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "NextCraftLauncher/0.1.0 (https://github.com/Minomiao/NextCraftLauncher)");
        return client;
    }
}
