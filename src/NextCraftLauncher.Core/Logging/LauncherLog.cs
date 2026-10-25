using System.Text;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Core.Logging;

/// <summary>
/// 简单的按天文件日志：写入 <c>NCL/log/launcher-yyyy-MM-dd.log</c>，
/// 自动清理 7 天前的日志。日志失败不会影响主流程。
/// </summary>
public static class LauncherLog
{
    private static readonly object Gate = new();
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static bool _initialized;
    private static DateTime _lastPruneDate = DateTime.MinValue;

    public static string DirectoryPath => Path.Combine(AppSettings.DataDirectory, "log");

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;

            TryCreateDirectory();
            PruneCore(DateTime.UtcNow);

            WriteCore("INFO", "启动器启动", null);
            WriteCore("INFO",
                $"系统 {Environment.OSVersion} · .NET {Environment.Version} · 目录 {AppContext.BaseDirectory}",
                null);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Info("启动器退出");
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null)
        => Write("ERROR", message, exception);

    /// <summary>游戏标准输出 / 错误。</summary>
    public static void Game(string message) => Write("GAME", message, null);

    /// <summary>加载器安装器输出。</summary>
    public static void Installer(string message) => Write("INSTALL", message, null);

    private static void Write(string level, string message, Exception? exception)
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                _initialized = true;
                TryCreateDirectory();
            }

            var now = DateTime.UtcNow;
            if (now.Date != _lastPruneDate)
                PruneCore(now);

            WriteCore(level, message, exception);
        }
    }

    private static void WriteCore(string level, string message, Exception? exception)
    {
        try
        {
            var now = DateTime.Now;
            var path = Path.Combine(DirectoryPath, $"launcher-{now:yyyy-MM-dd}.log");

            var builder = new StringBuilder(message.Length + 64);
            builder.Append('[').Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append("] [").Append(level).Append("] ").AppendLine(message);

            if (exception is not null)
                builder.AppendLine(exception.ToString());

            File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
        }
        catch (Exception)
        {
            // 日志写入失败不能影响启动器。
        }
    }

    private static void PruneCore(DateTime utcNow)
    {
        _lastPruneDate = utcNow.Date;

        try
        {
            var directory = DirectoryPath;
            if (!Directory.Exists(directory)) return;

            foreach (var file in Directory.EnumerateFiles(directory, "*.log"))
            {
                try
                {
                    if (utcNow - File.GetLastWriteTimeUtc(file) > Retention)
                        File.Delete(file);
                }
                catch (Exception)
                {
                    // 文件被占用时留到下次。
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响日志写入。
        }
    }

    private static void TryCreateDirectory()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        catch (Exception)
        {
            // 目录创建失败时后续写入会静默失败。
        }
    }
}
