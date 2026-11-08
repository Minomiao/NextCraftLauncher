using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Mods;

/// <summary>
/// 搜索结果的分页缓存，一页一个文件：
/// <code>
/// NCL/cache/mods/search/modrinth/1.21.1_fabric_sodium-p1.json
/// NCL/cache/mods/search/all/all_all_-p2.json
/// </code>
/// 48 小时内直接复用；“搜索”按钮会绕过缓存重新拉取并覆盖。
/// </summary>
public static class ModSearchCache
{
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(48);

    private const int MaxFiles = 400;
    private const int TrimTo = 300;

    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string RootDirectory => Path.Combine(AppSettings.DataDirectory, "cache", "mods", "search");

    public static string GetPath(
        ModPlatform? platform,
        string query,
        string? gameVersion,
        string? loader,
        int page)
    {
        var folder = platform switch
        {
            ModPlatform.Modrinth => "modrinth",
            ModPlatform.CurseForge => "curseforge",
            _ => "all",
        };

        var name = Sanitize(gameVersion) + "_" + Sanitize(loader) + "_" + Sanitize(query);
        if (name.Length > 60)
            name = name[..40] + "-" + Hash(name);

        return Path.Combine(RootDirectory, folder, $"{name}-p{Math.Max(1, page)}.json");
    }

    public static bool TryGet(
        ModPlatform? platform,
        string query,
        string? gameVersion,
        string? loader,
        int page,
        out ModSearchPage result)
    {
        result = new ModSearchPage(Array.Empty<ModProject>(), page, ModCatalog.PageSize, 0);

        try
        {
            var path = GetPath(platform, query, gameVersion, loader, page);
            if (!File.Exists(path)) return false;

            var file = JsonSerializer.Deserialize<CachedPageFile>(File.ReadAllBytes(path), JsonOptions);
            if (file is null) return false;
            if (DateTimeOffset.UtcNow - file.UpdatedAt > Freshness) return false;

            var projects = new List<ModProject>(file.Projects.Count);
            foreach (var item in file.Projects)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                projects.Add(new ModProject(
                    item.Id,
                    item.CurseForge ? ModPlatform.CurseForge : ModPlatform.Modrinth,
                    item.Slug ?? item.Id,
                    item.Name ?? item.Id,
                    item.Description,
                    item.Downloads,
                    item.Updated,
                    item.IconUrl));
            }

            result = new ModSearchPage(
                projects,
                Math.Max(1, page),
                file.PageSize > 0 ? file.PageSize : ModCatalog.PageSize,
                file.Total);
            return true;
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"读取 Mod 搜索缓存失败：{exception.Message}");
            return false;
        }
    }

    public static void Store(
        ModPlatform? platform,
        string query,
        string? gameVersion,
        string? loader,
        ModSearchPage page)
    {
        lock (FileLock)
        {
            try
            {
                var path = GetPath(platform, query, gameVersion, loader, page.Page);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var file = new CachedPageFile
                {
                    UpdatedAt = DateTimeOffset.UtcNow,
                    PageSize = page.PageSize,
                    Total = page.Total,
                    Projects = page.Projects.Select(project => new CachedProject
                    {
                        Id = project.Id,
                        CurseForge = project.Platform == ModPlatform.CurseForge,
                        Slug = project.Slug,
                        Name = project.Name,
                        Description = project.Description,
                        Downloads = project.Downloads,
                        Updated = project.Updated,
                        IconUrl = project.IconUrl,
                    }).ToList(),
                };

                var temp = path + ".tmp";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);

                ModCacheBudget.NotifyWrite(bytes.Length);

                Trim(directory);
            }
            catch (Exception exception)
            {
                LauncherLog.Warn($"写入 Mod 搜索缓存失败：{exception.Message}");
            }
        }
    }

    /// <summary>缓存文件太多时清掉最旧的一批，避免无限增长。</summary>
    private static void Trim(string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        try
        {
            var files = Directory.GetFiles(directory, "*.json");
            if (files.Length <= MaxFiles) return;

            foreach (var file in files
                         .OrderBy(File.GetLastWriteTimeUtc)
                         .Take(files.Length - TrimTo))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // 删除失败不影响本次写入。
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "all";

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '.' or '_' ? ch : '-');
        }

        var name = builder.ToString().Trim('-', '.');
        return name.Length == 0 ? "all" : name;
    }

    private static string Hash(string value)
        => Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..8];

    // ------------------------------------------------------------ 缓存文件结构

    private sealed class CachedPageFile
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
        public List<CachedProject> Projects { get; set; } = new();
    }

    private sealed class CachedProject
    {
        public string? Id { get; set; }
        public bool CurseForge { get; set; }
        public string? Slug { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public long Downloads { get; set; }
        public DateTimeOffset? Updated { get; set; }
        public string? IconUrl { get; set; }
    }
}
