using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using NextCraftLauncher.Core.Logging;

namespace NextCraftLauncher.Download.Mods;

/// <summary>
/// Mod 资源搜索与版本查询（Modrinth / CurseForge）。
/// 两个平台分别查询后合并；CurseForge 需要 API Key，Modrinth 不需要。
/// 官方源与镜像赛跑，取先成功的一方，并记住较快的源。
/// </summary>
public static class ModCatalog
{
    public const int PageSize = 30;

    private static readonly TimeSpan VersionReuseWindow = TimeSpan.FromSeconds(60);

    private const string ModrinthApi = "https://api.modrinth.com/v2";
    private const string CurseForgeApi = "https://api.curseforge.com/v1";

    /// <summary>CurseForge 的 API Key；为空时只提供 Modrinth 结果。</summary>
    public static string? CurseForgeApiKey { get; set; }

    private static readonly HttpClient Http = CreateClient();

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
    /// 搜索 Mod 的第 <paramref name="page"/> 页（从 1 开始）。每页 30 条，
    /// 两个平台各自取一半再合并去重；结果会缓存到 <c>NCL/cache/mods/search</c>。
    /// </summary>
    public static async Task<ModSearchPage> SearchAsync(
        ModPlatform? platform,
        string query,
        string? gameVersion,
        string? loader,
        int page,
        bool ignoreCache = false,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);

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
            // 接口不支持中文全文检索：先查内置别名表，再查 MC百科，都没有就保留原词。
            var translated = ModAliases.Resolve(rawQuery) ??
                             await ModAliasOnline.ResolveAsync(rawQuery, cancellationToken);

            if (!string.IsNullOrWhiteSpace(translated))
            {
                searchQuery = translated;
                LauncherLog.Info($"中文搜索：「{rawQuery}」→「{translated}」");
            }
        }

        if (!ignoreCache &&
            ModSearchCache.TryGet(platform, rawQuery, gameVersion, loader, page, out var cached))
        {
            LauncherLog.Info($"Mod 搜索命中缓存：「{rawQuery}」第 {page} 页");
            return cached;
        }

        var platformCount = (includeModrinth ? 1 : 0) + (includeCurseForge ? 1 : 0);
        if (platformCount == 0)
            return new ModSearchPage(Array.Empty<ModProject>(), page, PageSize, 0);

        // 两个平台都开启时各取一半，合起来刚好一页。
        var limit = Math.Max(1, PageSize / platformCount);
        var offset = (page - 1) * limit;

        var modrinthTask = includeModrinth
            ? SearchModrinthAsync(searchQuery, gameVersion, loader, offset, limit, cancellationToken)
            : Task.FromResult(SourcePage.Empty);
        var curseForgeTask = includeCurseForge
            ? SearchCurseForgeAsync(searchQuery, gameVersion, loader, offset, limit, cancellationToken)
            : Task.FromResult(SourcePage.Empty);

        var results = await Task.WhenAll(modrinthTask, curseForgeTask);
        var modrinthPage = results[0];
        var curseForgePage = results[1];

        var total = (includeModrinth ? modrinthPage.Total : 0) +
                    (includeCurseForge ? curseForgePage.Total : 0);

        var result = new ModSearchPage(
            MergeProjects(curseForgePage.Projects, modrinthPage.Projects),
            page,
            PageSize,
            total);

        ModSearchCache.Store(platform, rawQuery, gameVersion, loader, result);
        return result;
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
        int limit,
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

        var url = $"{ModrinthApi}/search?limit={limit}&index=relevance" +
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
        int limit,
        CancellationToken cancellationToken)
    {
        var url = $"{CurseForgeApi}/mods/search?gameId=432&classId=6&sortField=2&sortOrder=desc" +
                  $"&pageSize={limit}&index={offset}";

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
        var unfiltered = string.IsNullOrWhiteSpace(gameVersion) && string.IsNullOrWhiteSpace(loader);

        if (unfiltered && ModVersionCache.TryGet(project, out var cached)) return cached;

        var versions = await FetchVersionsAsync(project, gameVersion, loader, cancellationToken);

        // 只缓存不带筛选的完整列表，预取回来也是这一份。
        if (unfiltered) ModVersionCache.Store(project, versions);

        return versions;
    }

    /// <summary>给后台预取使用：缓存已有且未过期时直接返回。</summary>
    public static async Task EnsureVersionsCachedAsync(
        ModProject project,
        CancellationToken cancellationToken = default)
    {
        if (ModVersionCache.IsFresh(project)) return;

        var versions = await FetchVersionsAsync(project, null, null, cancellationToken);
        ModVersionCache.Store(project, versions);
    }

    private static Task<IReadOnlyList<ModVersion>> FetchVersionsAsync(
        ModProject project,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken)
        => project.Platform == ModPlatform.Modrinth
            ? GetModrinthVersionsAsync(project, gameVersion, loader, cancellationToken)
            : GetCurseForgeVersionsAsync(project, gameVersion, loader, cancellationToken);

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

    // ------------------------------------------------------------ 请求

    /// <summary>首个源的等待时间。</summary>
    private static readonly TimeSpan FirstAttemptTimeout = TimeSpan.FromSeconds(12);

    private static readonly TimeSpan SecondAttemptTimeout = TimeSpan.FromSeconds(15);

    /// <summary>首个源迟迟不回应时，等这么久就让另一个源一起抢。</summary>
    private static readonly TimeSpan HedgeDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>上次成功的源；下次优先尝试它，避免每次都先等一次慢源。</summary>
    private static volatile bool _preferMirror;

    /// <summary>
    /// 官方源与镜像赛跑：先问上次成功的那个，2.5 秒内没回应就同时问另一个，
    /// 谁先成功用谁；成功的源会被记住。比固定顺序重试少一次超时等待。
    /// </summary>
    private static async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken cancellationToken,
        string? apiKey = null)
    {
        var mirror = ModDownloadUrlResolver.ToMirror(url);
        if (string.Equals(mirror, url, StringComparison.OrdinalIgnoreCase))
        {
            var (direct, _) = await FetchJsonAsync(url, SecondAttemptTimeout, apiKey, cancellationToken);
            return direct;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var preferred = _preferMirror ? mirror : url;
        var fallback = _preferMirror ? url : mirror;

        var first = FetchJsonAsync(preferred, FirstAttemptTimeout, apiKey, linked.Token);
        var firstOrDelay = await Task.WhenAny(first, Task.Delay(HedgeDelay, cancellationToken));

        if (firstOrDelay == first)
        {
            try
            {
                var (document, _) = await first;
                RememberSource(preferred);
                linked.Cancel();
                return document;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // 首选源失败，立即换另一个。
            }
        }

        // 首选源还没回来或已经失败：另一个源也开跑，两个一起等。
        var pending = new List<Task<(JsonDocument Document, string Url)>>();
        if (!first.IsCompleted) pending.Add(first);
        pending.Add(FetchJsonAsync(fallback, SecondAttemptTimeout, apiKey, linked.Token));

        Exception? lastError = null;
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending);
            pending.Remove(finished);

            try
            {
                var (document, source) = await finished;
                RememberSource(source);
                linked.Cancel();
                ObserveFaults(pending);
                return document;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
        }

        throw new IOException($"请求 {url} 失败：{lastError?.Message}", lastError);
    }

    private static async Task<(JsonDocument Document, string Url)> FetchJsonAsync(
        string url,
        TimeSpan timeout,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

        using var response = await Http.SendAsync(request, attempt.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (JsonDocument.Parse("{}"), url);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
        return (await JsonDocument.ParseAsync(stream, cancellationToken: attempt.Token), url);
    }

    private static void RememberSource(string url)
        => _preferMirror = ModDownloadUrlResolver.IsMirror(url);

    /// <summary>取消落败的请求后观察它的异常，避免未观察任务异常。</summary>
    private static void ObserveFaults(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks)
        {
            _ = task.ContinueWith(
                static finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
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

/// <summary>Mod 下载地址与 API 的镜像处理。</summary>
public static class ModDownloadUrlResolver
{
    public const string MirrorRoot = "https://mod.mcimirror.top";

    public static IReadOnlyList<string> Build(string url)
    {
        var mirror = ToMirror(url);
        return string.Equals(mirror, url, StringComparison.OrdinalIgnoreCase)
            ? new[] { url }
            : new[] { mirror, url };
    }

    public static bool IsMirror(string url)
        => url.StartsWith(MirrorRoot, StringComparison.OrdinalIgnoreCase);

    public static string ToMirror(string url)
        => url
            .Replace("https://api.modrinth.com", MirrorRoot + "/modrinth", StringComparison.OrdinalIgnoreCase)
            .Replace("https://cdn.modrinth.com", MirrorRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("https://api.curseforge.com", MirrorRoot + "/curseforge", StringComparison.OrdinalIgnoreCase)
            .Replace("https://edge.forgecdn.net", MirrorRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("https://mediafilez.forgecdn.net", MirrorRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("https://media.forgecdn.net", MirrorRoot, StringComparison.OrdinalIgnoreCase);
}
