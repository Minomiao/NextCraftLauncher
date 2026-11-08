using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Mods;

/// <summary>
/// Mod 相关缓存（搜索结果、工程文件列表、图标）的总大小上限。
/// 超过 500 MB 后按最后写入时间从旧到新删除，降到 450 MB 以下。
/// </summary>
public static class ModCacheBudget
{
    public const long Limit = 500L * 1024 * 1024;

    private const long Target = 450L * 1024 * 1024;

    private static readonly object Gate = new();

    /// <summary>已知总字节数；-1 表示还没量过。</summary>
    private static long _size = -1;

    public static string RootDirectory => Path.Combine(AppSettings.DataDirectory, "cache", "mods");

    /// <summary>还能不能继续往缓存里写。</summary>
    public static bool HasRoom()
    {
        lock (Gate)
        {
            EnsureMeasured();
            return _size >= 0 && _size < Limit;
        }
    }

    /// <summary>启动时在后台量一次总量，之后用增量维护。</summary>
    public static void EnsureMeasured()
    {
        if (_size >= 0) return;

        try
        {
            _size = Measure();
        }
        catch (Exception)
        {
            _size = 0;
        }
    }

    /// <summary>写入缓存后调用，累计大小并在超限时清理。</summary>
    public static void NotifyWrite(long bytes)
    {
        if (bytes <= 0) return;

        lock (Gate)
        {
            if (_size < 0) return; // 还没量过，等下一次测量
            _size += bytes;
            if (_size <= Limit) return;

            Trim();
        }
    }

    private static long Measure()
    {
        if (!Directory.Exists(RootDirectory)) return 0;

        long total = 0;
        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch (Exception)
            {
                // 文件刚好被删除时忽略。
            }
        }

        return total;
    }

    private static void Trim()
    {
        try
        {
            var files = Directory.EnumerateFiles(RootDirectory, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.LastWriteTimeUtc)
                .ToList();

            long size = files.Sum(info => info.Length);
            foreach (var file in files)
            {
                if (size <= Target) break;

                try
                {
                    var length = file.Length;
                    file.Delete();
                    size -= length;
                }
                catch (Exception)
                {
                }
            }

            _size = size;
            LauncherLog.Info($"Mod 缓存超出上限，已清理到 {size / 1024.0 / 1024.0:0} MB");
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"清理 Mod 缓存失败：{exception.Message}");
        }
    }
}
