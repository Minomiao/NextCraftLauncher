using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Loaders;

/// <summary>
/// 加载器版本列表的本地缓存，按「加载器 / 游戏版本」分文件保存：
/// <c>NCL/cache/loaders/forge/1.21.1.json</c>。
/// 写入使用临时文件 + 覆盖移动，避免进程退出时留下半截 JSON。
/// </summary>
public static class LoaderVersionStore
{
    /// <summary>
    /// 缓存有效期与留存时间：72 小时。超过后不再展示，由后台任务重新获取；
    /// 长期未刷新的文件会在下一轮预取开始时清理。
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(72);

    private const int SchemaVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string RootDirectory => Path.Combine(AppSettings.DataDirectory, "cache", "loaders");

    public static string GetPath(string loader, string minecraftVersion)
        => Path.Combine(RootDirectory, SafeName(loader), SafeName(minecraftVersion) + ".json");

    public static bool IsFresh(DateTimeOffset updatedAt)
        => DateTimeOffset.UtcNow - updatedAt < Freshness;

    /// <summary>读取缓存；文件不存在、损坏或格式不符时返回 false。</summary>
    public static bool TryLoad(
        string loader,
        string minecraftVersion,
        out LoaderVersionResult result,
        out DateTimeOffset updatedAt)
    {
        result = new LoaderVersionResult(Array.Empty<LoaderVersion>(), null);
        updatedAt = default;

        try
        {
            var path = GetPath(loader, minecraftVersion);
            if (!File.Exists(path)) return false;

            var data = JsonSerializer.Deserialize<LoaderVersionFile>(File.ReadAllBytes(path), JsonOptions);
            if (data is null || data.SchemaVersion != SchemaVersion) return false;

            var versions = (data.Versions ?? new List<LoaderVersionEntry>())
                .Where(item => !string.IsNullOrWhiteSpace(item.Version))
                .Select(item => new LoaderVersion(item.Version!, item.Tag))
                .ToList();

            result = new LoaderVersionResult(versions, data.Error);
            updatedAt = data.UpdatedAt;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>写入缓存；失败时返回 false，不影响内存中的结果。</summary>
    public static bool Save(string loader, string minecraftVersion, LoaderVersionResult result)
    {
        try
        {
            var path = GetPath(loader, minecraftVersion);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var data = new LoaderVersionFile
            {
                SchemaVersion = SchemaVersion,
                Loader = loader,
                Minecraft = minecraftVersion,
                UpdatedAt = DateTimeOffset.UtcNow,
                Error = result.Error,
                Versions = result.Versions
                    .Select(item => new LoaderVersionEntry { Version = item.Version, Tag = item.Tag })
                    .ToList(),
            };

            var tempPath = path + ".tmp";
            File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>删除超过留存时间的缓存文件，避免目录无限增长。</summary>
    public static void PruneExpired()
    {
        try
        {
            if (!Directory.Exists(RootDirectory)) return;

            var cutoff = DateTime.UtcNow - Freshness;
            foreach (var file in Directory.EnumerateFiles(RootDirectory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch (Exception)
                {
                    // 被占用的文件留到下一轮。
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
                catch (Exception)
                {
                    // 同上。
                }
            }
        }
        catch (Exception)
        {
            // 清理失败不影响正常使用。
        }
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        return builder.ToString();
    }

    private sealed class LoaderVersionFile
    {
        public int SchemaVersion { get; set; }
        public string? Loader { get; set; }
        public string? Minecraft { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? Error { get; set; }
        public List<LoaderVersionEntry>? Versions { get; set; }
    }

    private sealed class LoaderVersionEntry
    {
        public string? Version { get; set; }
        public string? Tag { get; set; }
    }
}
