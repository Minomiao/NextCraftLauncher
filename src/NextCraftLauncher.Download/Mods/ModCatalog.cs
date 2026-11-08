using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using NextCraftLauncher.Core.Logging;

namespace NextCraftLauncher.Download.Mods;

/// <summary>
/// Mod 资源搜索与版本查询（Modrinth / CurseForge）。
/// 与 PCL2 一致：两个平台分别查询后合并；CurseForge 需要 API Key，
/// Modrinth 不需要。请求失败时自动改走 MCIMirror。
/// </summary>
public static class ModCatalog
{
    public const int PageSize = 40;

    /// <summary>相同搜索在 60 秒内直接复用结果（与 PCL2 的 ReloadTimeout 一致）。</summary>
    private static readonly TimeSpan SearchReuseWindow = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan VersionReuseWindow = TimeSpan.FromSeconds(60);

    private const string ModrinthApi = "https://api.modrinth.com/v2";
    private const string CurseForgeApi = "https://api.curseforge.com/v1";

    /// <summary>CurseForge 的 API Key；为空时只提供 Modrinth 结果。</summary>
    public static string? CurseForgeApiKey { get; set; }

    private static readonly HttpClient Http = CreateClient();

    private static readonly ConcurrentDictionary<string, CacheEntry<ModSearchPage>> SearchCache = new();
    private static readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<ModVersion>>> VersionCache = new();
    private static readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<ModProject>>> ProjectCache = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // 单次尝试的超时由 GetJsonAsync 逐个请求控制。
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "NextCraftLauncher/0.1.0 (https://github.com/Minomiao/NextCraftLauncher)");
        return client;
    }

    // ------------------------------------------------------------ 搜索

    /// <summary>
    /// 搜索 Mod。两个平台各自维护分页偏移，返回合并去重后的结果。
    /// 相同的搜索（含偏移）在 60 秒内直接复用，不再请求。
    /// </summary>
    public static async Task<ModSearchPage> SearchAsync(
        ModPlatform? platform,
        string query,
        string? gameVersion,
        string? loader,
        int modrinthOffset,
        int curseForgeOffset,
        CancellationToken cancellationToken = default)
    {
        if (platform == ModPlatform.CurseForge && string.IsNullOrWhiteSpace(CurseForgeApiKey))
            throw new InvalidOperationException("需要 CurseForge API Key（可设置环境变量 NCL_CURSEFORGE_API_KEY）");

        var includeModrinth = platform is null or ModPlatform.Modrinth;
        var includeCurseForge = platform is null or ModPlatform.CurseForge;
        if (includeCurseForge && string.IsNullOrWhiteSpace(CurseForgeApiKey))
            includeCurseForge = false; // 没有 Key 时只提供 Modrinth 结果（与 PCL2 一致）

        var rawQuery = query?.Trim() ?? string.Empty;
        var searchQuery = rawQuery;
        if (ModAliases.ContainsChinese(rawQuery))
        {
            // 接口不支持中文全文检索，先查自己的别名表换成英文关键词；
            // 没有收录时保留原词，至少不会比不翻译更差。
            var translated = ModAliases.Resolve(rawQuery);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                searchQuery = translated;
                LauncherLog.Info($"中文搜索：「{rawQuery}」→「{translated}」");
            }
        }

        var cacheKey = $"{platform}|{rawQuery}|{gameVersion}|{loader}|{modrinthOffset}|{curseForgeOffset}";
        if (TryGetCached(SearchCache, cacheKey, SearchReuseWindow, out var cached)) return cached;

        var modrinthTask = includeModrinth
            ? SearchModrinthAsync(searchQuery, gameVersion, loader, modrinthOffset, cancellationToken)
            : Task.FromResult(SourcePage.Empty);
        var curseForgeTask = includeCurseForge
            ? SearchCurseForgeAsync(searchQuery, gameVersion, loader, curseForgeOffset, cancellationToken)
            : Task.FromResult(SourcePage.Empty);

        var results = await Task.WhenAll(modrinthTask, curseForgeTask);
        var modrinthPage = results[0];
        var curseForgePage = results[1];

        var page = new ModSearchPage(
            MergeProjects(curseForgePage.Projects, modrinthPage.Projects),
            modrinthOffset + modrinthPage.Projects.Count,
            includeModrinth ? modrinthPage.Total : 0,
            curseForgeOffset + curseForgePage.Projects.Count,
            includeCurseForge ? curseForgePage.Total : 0,
            HasMore(modrinthPage, modrinthOffset) || HasMore(curseForgePage, curseForgeOffset));

        StoreCached(SearchCache, cacheKey, page);
        return page;

        static bool HasMore(SourcePage source, int offset)
            => source.Projects.Count > 0 && source.Total > offset + source.Projects.Count;
    }

    /// <summary>合并两个平台的结果：CurseForge 在前，按平台 ID 与 slug 去重。</summary>
    private static List<ModProject> MergeProjects(
        IReadOnlyList<ModProject> curseForge,
        IReadOnlyList<ModProject> modrinth)
    {
        var result = new List<ModProject>(curseForge.Count + modrinth.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in curseForge.Concat(modrinth))
        {
            if (!ids.Add(project.Platform + ":" + project.Id)) continue;
            if (!string.IsNullOrWhiteSpace(project.Slug) && !slugs.Add(project.Slug)) continue;
            result.Add(project);
        }

        return result;
    }

    private static async Task<SourcePage> SearchModrinthAsync(
        string query,
        string? gameVersion,
        string? loader,
        int offset,
        CancellationToken cancellationToken)
    {
        var facets = new List<string> { "[\"project_type:mod\"]" };

        if (!string.IsNullOrWhiteSpace(loader))
        {
            var tags = LoaderTags(loader);
            if (tags.Count > 0)
                facets.Add("[" + string.Join(",", tags.Select(tag => $"\"categories:{tag}\"")) + "]");
        }

        if (!string.IsNullOrWhiteSpace(gameVersion))
            facets.Add($"[\"versions:'{gameVersion}'\"]");

        var url = $"{ModrinthApi}/search?limit={PageSize}&index=relevance" +
                  $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

        if (!string.IsNullOrWhiteSpace(query))
            url += "&query=" + Uri.EscapeDataString(query);

        if (offset > 0)
            url += $"&offset={offset}";

        using var document = await GetJsonAsync(url, cancellationToken);
        var root = document.RootElement;

        var projects = new List<ModProject>();
        if (root.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hits.EnumerateArray())
            {
                var id = String(hit, "project_id") ?? String(hit, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                projects.Add(new ModProject(
                    id,
                    ModPlatform.Modrinth,
                    String(hit, "slug") ?? id,
                    String(hit, "title") ?? id,
                    String(hit, "description"),
                    Long(hit, "downloads"),
                    Date(hit, "date_modified"),
                    String(hit, "icon_url")));
            }
        }

        return new SourcePage(projects, (int)Long(root, "total_hits"));
    }

    private static async Task<SourcePage> SearchCurseForgeAsync(
        string query,
        string? gameVersion,
        string? loader,
        int offset,
        CancellationToken cancellationToken)
    {
        var url = $"{CurseForgeApi}/mods/search?gameId=432&classId=6&sortField=2&sortOrder=desc" +
                  $"&pageSize={PageSize}&index={offset}";

        var loaderType = CurseForgeLoaderType(loader);
        if (loaderType is not null) url += $"&modLoaderType={loaderType}";
        if (!string.IsNullOrWhiteSpace(gameVersion)) url += "&gameVersion=" + Uri.EscapeDataString(gameVersion);
        if (!string.IsNullOrWhiteSpace(query)) url += "&searchFilter=" + Uri.EscapeDataString(query);

        using var document = await GetJsonAsync(url, cancellationToken, CurseForgeApiKey);
        var root = document.RootElement;

        var projects = new List<ModProject>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in data.EnumerateArray())
            {
                var id = String(entry, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                projects.Add(new ModProject(
                    id,
                    ModPlatform.CurseForge,
                    String(entry, "slug") ?? id,
                    String(entry, "name") ?? id,
                    String(entry, "summary"),
                    Long(entry, "downloadCount"),
                    Date(entry, "dateModified"),
                    CurseForgeLogo(entry)));
            }
        }

        var total = root.TryGetProperty("pagination", out var pagination)
            ? (int)Long(pagination, "totalCount")
            : projects.Count;

        return new SourcePage(projects, total);
    }

    // ------------------------------------------------------------ 版本列表

    public static async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        ModProject project,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{project.Platform}:{project.Id}|{gameVersion}|{loader}";
        if (TryGetCached(VersionCache, cacheKey, VersionReuseWindow, out var cached)) return cached;

        var versions = project.Platform == ModPlatform.Modrinth
            ? await GetModrinthVersionsAsync(project, gameVersion, loader, cancellationToken)
            : await GetCurseForgeVersionsAsync(project, gameVersion, loader, cancellationToken);

        StoreCached(VersionCache, cacheKey, versions);
        return versions;
    }

    private static async Task<IReadOnlyList<ModVersion>> GetModrinthVersionsAsync(
        ModProject project,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        var url = $"{ModrinthApi}/project/{Uri.EscapeDataString(project.Id)}/version";

        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            filters.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));

        var tags = string.IsNullOrWhiteSpace(loader) ? new List<string>() : LoaderTags(loader);
        if (tags.Count > 0)
            filters.Add("loaders=" + Uri.EscapeDataString("[" + string.Join(",", tags.Select(tag => $"\"{tag}\"")) + "]"));

        if (filters.Count > 0) url += "?" + string.Join("&", filters);

        using var document = await GetJsonAsync(url, cancellationToken);
        var versions = new List<ModVersion>();

        if (document.RootElement.ValueKind != JsonValueKind.Array) return versions;

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            if (files.GetArrayLength() == 0) continue;

            var file = files[0];
            var fileUrl = String(file, "url");
            if (string.IsNullOrWhiteSpace(fileUrl)) continue;

            var dependencies = new List<string>();
            if (entry.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var dependency in deps.EnumerateArray())
                {
                    if (!string.Equals(String(dependency, "dependency_type"), "required", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dependencyId = String(dependency, "project_id");
                    if (string.IsNullOrWhiteSpace(dependencyId)) continue;
                    if (dependencyId is "P7dR8mSH" or "qvIfYCYJ") continue; // Fabric API / Quilt API
                    if (!dependencies.Contains(dependencyId)) dependencies.Add(dependencyId);
                }
            }

            versions.Add(new ModVersion(
                String(entry, "id") ?? fileUrl,
                project.Id,
                ModPlatform.Modrinth,
                String(entry, "name") ?? string.Empty,
                String(entry, "version_number"),
                String(file, "filename") ?? Path.GetFileName(new Uri(fileUrl).AbsolutePath),
                ModDownloadUrlResolver.Build(fileUrl),
                file.TryGetProperty("hashes", out var hashes) ? String(hashes, "sha1") : null,
                Long(file, "size"),
                StringArray(entry, "game_versions"),
                StringArray(entry, "loaders"),
                String(entry, "version_type") ?? "release",
                Date(entry, "date_published"),
                dependencies));
        }

        return versions;
    }

    private static async Task<IReadOnlyList<ModVersion>> GetCurseForgeVersionsAsync(
        ModProject project,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
    {
        var url = $"{CurseForgeApi}/mods/{Uri.EscapeDataString(project.Id)}/files?pageSize=50";
        var loaderType = CurseForgeLoaderType(loader);
        if (loaderType is not null) url += $"&modLoaderType={loaderType}";
        if (!string.IsNullOrWhiteSpace(gameVersion)) url += "&gameVersion=" + Uri.EscapeDataString(gameVersion);

        using var document = await GetJsonAsync(url, cancellationToken, CurseForgeApiKey);
        var versions = new List<ModVersion>();

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return versions;

        foreach (var entry in data.EnumerateArray())
        {
            var fileName = String(entry, "fileName");
            if (string.IsNullOrWhiteSpace(fileName)) continue;

            var fileId = String(entry, "id") ?? fileName;
            var downloadUrl = String(entry, "downloadUrl");
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                // 作者关闭了第三方分发时，用文件 ID 拼出官方 CDN 地址。
                if (long.TryParse(fileId, out var numericId))
                {
                    downloadUrl = $"https://edge.forgecdn.net/files/{numericId / 1000}/{numericId % 1000}/" +
                                  Uri.EscapeDataString(fileName);
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl)) continue;

            var dependencies = new List<string>();
            if (entry.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var dependency in deps.EnumerateArray())
                {
                    // 3 = required
                    if (Long(dependency, "relationType") != 3) continue;

                    var dependencyId = String(dependency, "modId");
                    if (string.IsNullOrWhiteSpace(dependencyId)) continue;
                    if (dependencyId is "306612" or "634179") continue; // Fabric API / Quilt API
                    if (!dependencies.Contains(dependencyId)) dependencies.Add(dependencyId);
                }
            }

            var gameVersions = new List<string>();
            var loaders = new List<string>();
            if (entry.TryGetProperty("gameVersions", out var rawVersions) && rawVersions.ValueKind == JsonValueKind.Array)
            {
                foreach (var raw in rawVersions.EnumerateArray())
                {
                    var value = raw.GetString();
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    if (IsLoaderName(value)) loaders.Add(value.ToLowerInvariant());
                    else if (!gameVersions.Contains(value)) gameVersions.Add(value);
                }
            }

            versions.Add(new ModVersion(
                fileId,
                project.Id,
                ModPlatform.CurseForge,
                String(entry, "displayName") ?? fileName,
                null,
                fileName,
                ModDownloadUrlResolver.Build(downloadUrl),
                CurseForgeHash(entry),
                Long(entry, "fileLength"),
                gameVersions,
                loaders,
                Long(entry, "releaseType") switch { 2 => "beta", 3 => "alpha", _ => "release" },
                Date(entry, "fileDate"),
                dependencies));
        }

        return versions;
    }

    /// <summary>查询若干项目（用于显示前置 Mod 的名字）。</summary>
    public static async Task<IReadOnlyList<ModProject>> GetProjectsAsync(
        ModPlatform platform,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return Array.Empty<ModProject>();

        var cacheKey = platform + "|" + string.Join(",", ids.OrderBy(id => id, StringComparer.Ordinal));
        if (TryGetCached(ProjectCache, cacheKey, VersionReuseWindow, out var cached)) return cached;

        IReadOnlyList<ModProject> result;
        try
        {
            if (platform == ModPlatform.Modrinth)
            {
                var url = $"{ModrinthApi}/projects?ids=" +
                          Uri.EscapeDataString("[" + string.Join(",", ids.Select(id => $"\"{id}\"")) + "]");
                using var document = await GetJsonAsync(url, cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ModProject>();

                result = document.RootElement.EnumerateArray()
                    .Select(entry => new ModProject(
                        String(entry, "id") ?? string.Empty,
                        ModPlatform.Modrinth,
                        String(entry, "slug") ?? string.Empty,
                        String(entry, "title") ?? string.Empty,
                        String(entry, "description"),
                        Long(entry, "downloads"),
                        Date(entry, "updated"),
                        String(entry, "icon_url")))
                    .Where(project => project.Id.Length > 0)
                    .ToList();
            }
            else
            {
                var curseUrl = $"{CurseForgeApi}/mods?" +
                               string.Join("&", ids.Select(id => "modId=" + Uri.EscapeDataString(id)));
                using var curseDocument = await GetJsonAsync(curseUrl, cancellationToken, CurseForgeApiKey);
                if (!curseDocument.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<ModProject>();
                }

                result = data.EnumerateArray()
                    .Select(entry => new ModProject(
                        String(entry, "id") ?? string.Empty,
                        ModPlatform.CurseForge,
                        String(entry, "slug") ?? string.Empty,
                        String(entry, "name") ?? string.Empty,
                        String(entry, "summary"),
                        Long(entry, "downloadCount"),
                        Date(entry, "dateModified"),
                        null))
                    .Where(project => project.Id.Length > 0)
                    .ToList();
            }
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"查询前置 Mod 失败：{exception.Message}");
            return Array.Empty<ModProject>();
        }

        if (result.Count > 0) StoreCached(ProjectCache, cacheKey, result);
        return result;
    }

    // ------------------------------------------------------------ 工具

    /// <summary>
    /// 与 PCL2 的 <c>DlModRequest</c> 一致：官方源与镜像交替重试，每次尝试单独设超时，
    /// 一个源卡住不会拖住整个搜索。
    /// </summary>
    private static async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken cancellationToken,
        string? apiKey = null)
    {
        Exception? lastError = null;

        foreach (var (candidate, timeoutSeconds) in ModDownloadUrlResolver.BuildApiAttempts(url))
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

                using var response = await Http.SendAsync(request, attempt.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return JsonDocument.Parse("{}");

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
                return await JsonDocument.ParseAsync(stream, cancellationToken: attempt.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 单次尝试超时或失败，换下一个地址继续。
                lastError = exception;
            }
        }

        throw new IOException($"请求 {url} 失败：{lastError?.Message}", lastError);
    }

    private static bool TryGetCached<T>(
        ConcurrentDictionary<string, CacheEntry<T>> cache,
        string key,
        TimeSpan window,
        out T value)
    {
        value = default!;
        if (!cache.TryGetValue(key, out var entry)) return false;

        if (DateTimeOffset.UtcNow - entry.StoredAt > window)
        {
            cache.TryRemove(key, out _);
            return false;
        }

        value = entry.Value;
        return true;
    }

    private static void StoreCached<T>(
        ConcurrentDictionary<string, CacheEntry<T>> cache,
        string key,
        T value)
    {
        if (cache.Count > 256) cache.Clear();
        cache[key] = new CacheEntry<T>(DateTimeOffset.UtcNow, value);
    }

    private readonly record struct CacheEntry<T>(DateTimeOffset StoredAt, T Value);

    private readonly record struct SourcePage(IReadOnlyList<ModProject> Projects, int Total)
    {
        public static SourcePage Empty => new(Array.Empty<ModProject>(), 0);
    }

    /// <summary>我们的加载器名称 → Modrinth 的 loader 标签。</summary>
    public static List<string> LoaderTags(string? loader)
    {
        var value = loader?.Trim().ToLowerInvariant() ?? string.Empty;
        return value switch
        {
            "forge" => new List<string> { "forge" },
            "fabric" => new List<string> { "fabric" },
            "neoforge" => new List<string> { "neoforge" },
            "quilt" => new List<string> { "quilt" },
            _ => new List<string>(),
        };
    }

    private static int? CurseForgeLoaderType(string? loader)
        => loader?.Trim().ToLowerInvariant() switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => null,
        };

    public static bool IsLoaderName(string value)
        => value.Trim().ToLowerInvariant() is
            "forge" or "fabric" or "neoforge" or "quilt" or "liteloader" or "cauldron" or "rift";

    private static string? CurseForgeLogo(JsonElement entry)
        => entry.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object
            ? String(logo, "thumbnailUrl") ?? String(logo, "url")
            : null;

    private static string? CurseForgeHash(JsonElement entry)
    {
        if (!entry.TryGetProperty("hashes", out var hashes) || hashes.ValueKind != JsonValueKind.Array)
            return null;

        string? md5 = null;
        foreach (var hash in hashes.EnumerateArray())
        {
            var algo = Long(hash, "algo");
            var value = String(hash, "value")?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(value)) continue;

            if (algo == 1) return value;
            if (algo == 2) md5 = value;
        }

        return md5;
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Long(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
                _ => 0,
            }
            : 0;

    private static DateTimeOffset? Date(JsonElement element, string name)
        => DateTimeOffset.TryParse(String(element, name), out var value) ? value : null;

    private static IReadOnlyList<string> StringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return array.EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }
}

/// <summary>Mod 下载地址与 API 的镜像处理（与 PCL2 的 DlSourceModGet 一致）。</summary>
public static class ModDownloadUrlResolver
{
    private const string Mirror = "https://mod.mcimirror.top";

    public static IReadOnlyList<string> Build(string url)
    {
        var mirror = ToMirror(url);
        return string.Equals(mirror, url, StringComparison.OrdinalIgnoreCase)
            ? new[] { url }
            : new[] { mirror, url };
    }

    /// <summary>
    /// API 请求的尝试顺序与单次超时（秒），与 PCL2 的 <c>DlModRequest</c> 一致：
    /// 官方源 → 镜像 → 官方源 → 镜像。
    /// </summary>
    public static IReadOnlyList<(string Url, int TimeoutSeconds)> BuildApiAttempts(string url)
    {
        var mirror = ToMirror(url);
        if (string.Equals(mirror, url, StringComparison.OrdinalIgnoreCase))
            return new[] { (url, 30) };

        // Modrinth 的官方接口本身就慢，首次给它多一点的余量（PCL2 也是 20s）。
        var firstTimeout = url.Contains("api.modrinth.com", StringComparison.OrdinalIgnoreCase) ? 20 : 10;
        return new[]
        {
            (url, firstTimeout),
            (mirror, 10),
            (url, 30),
            (mirror, 30),
        };
    }

    public static string ToMirror(string url)
        => url
            .Replace("https://api.modrinth.com", Mirror + "/modrinth", StringComparison.OrdinalIgnoreCase)
            .Replace("https://cdn.modrinth.com", Mirror, StringComparison.OrdinalIgnoreCase)
            .Replace("https://api.curseforge.com", Mirror + "/curseforge", StringComparison.OrdinalIgnoreCase)
            .Replace("https://edge.forgecdn.net", Mirror, StringComparison.OrdinalIgnoreCase)
            .Replace("https://mediafilez.forgecdn.net", Mirror, StringComparison.OrdinalIgnoreCase)
            .Replace("https://media.forgecdn.net", Mirror, StringComparison.OrdinalIgnoreCase);
}
