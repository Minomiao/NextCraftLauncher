using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download.Mods;

/// <summary>
/// 工程文件列表（版本列表）的本地缓存，一个工程一个文件：
/// <code>
/// NCL/cache/mods/versions/modrinth/AANobbMI.json
/// NCL/cache/mods/versions/curseforge/238222.json
/// </code>
/// 48 小时内直接复用，打开工程详情时不需要再联网。
/// </summary>
public static class ModVersionCache
{
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(48);

    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string RootDirectory => Path.Combine(AppSettings.DataDirectory, "cache", "mods", "versions");

    public static string GetPath(ModProject project)
        => Path.Combine(
            RootDirectory,
            project.Platform == ModPlatform.Modrinth ? "modrinth" : "curseforge",
            SafeName(project.Id) + ".json");

    /// <summary>只看文件时间，不解析内容；预取时用来跳过已有缓存。</summary>
    public static bool IsFresh(ModProject project)
    {
        try
        {
            var info = new FileInfo(GetPath(project));
            return info.Exists &&
                   info.Length > 0 &&
                   DateTimeOffset.UtcNow - info.LastWriteTimeUtc < Freshness;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryGet(ModProject project, out IReadOnlyList<ModVersion> versions)
    {
        versions = Array.Empty<ModVersion>();

        try
        {
            var path = GetPath(project);
            if (!File.Exists(path)) return false;

            var file = JsonSerializer.Deserialize<CachedVersionFile>(File.ReadAllBytes(path), JsonOptions);
            if (file is null) return false;
            if (DateTimeOffset.UtcNow - file.UpdatedAt > Freshness) return false;

            var list = new List<ModVersion>(file.Versions.Count);
            foreach (var item in file.Versions)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;

                list.Add(new ModVersion(
                    item.Id,
                    item.ProjectId ?? project.Id,
                    project.Platform,
                    item.Name ?? item.FileName ?? item.Id,
                    item.VersionNumber,
                    item.FileName ?? item.Id,
                    item.DownloadUrls ?? new List<string>(),
                    item.Sha1,
                    item.Size,
                    item.GameVersions ?? new List<string>(),
                    item.Loaders ?? new List<string>(),
                    item.ReleaseType ?? "release",
                    item.Published,
                    item.Dependencies ?? new List<string>()));
            }

            versions = list;
            return true;
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"读取 Mod 文件列表缓存失败：{exception.Message}");
            return false;
        }
    }

    public static void Store(ModProject project, IReadOnlyList<ModVersion> versions)
    {
        lock (FileLock)
        {
            try
            {
                var path = GetPath(project);
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var file = new CachedVersionFile
                {
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Versions = versions.Select(version => new CachedVersion
                    {
                        Id = version.Id,
                        ProjectId = version.ProjectId,
                        Name = version.Name,
                        VersionNumber = version.VersionNumber,
                        FileName = version.FileName,
                        DownloadUrls = version.DownloadUrls.ToList(),
                        Sha1 = version.Sha1,
                        Size = version.Size,
                        GameVersions = version.GameVersions.ToList(),
                        Loaders = version.Loaders.ToList(),
                        ReleaseType = version.ReleaseType,
                        Published = version.Published,
                        Dependencies = version.Dependencies.ToList(),
                    }).ToList(),
                };

                var bytes = JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);

                ModCacheBudget.NotifyWrite(bytes.Length);
            }
            catch (Exception exception)
            {
                LauncherLog.Warn($"写入 Mod 文件列表缓存失败：{exception.Message}");
            }
        }
    }

    private static string SafeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        }

        var name = builder.ToString().Trim('-', '.');
        return name.Length == 0 ? "project" : name;
    }

    private sealed class CachedVersionFile
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public List<CachedVersion> Versions { get; set; } = new();
    }

    private sealed class CachedVersion
    {
        public string? Id { get; set; }
        public string? ProjectId { get; set; }
        public string? Name { get; set; }
        public string? VersionNumber { get; set; }
        public string? FileName { get; set; }
        public List<string>? DownloadUrls { get; set; }
        public string? Sha1 { get; set; }
        public long Size { get; set; }
        public List<string>? GameVersions { get; set; }
        public List<string>? Loaders { get; set; }
        public string? ReleaseType { get; set; }
        public DateTimeOffset? Published { get; set; }
        public List<string>? Dependencies { get; set; }
    }
}
