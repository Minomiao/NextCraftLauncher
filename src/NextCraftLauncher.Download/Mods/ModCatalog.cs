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
    public const int PageSize = 20;

    private const string ModrinthApi = "https://api.modrinth.com/v2";
    private const string CurseForgeApi = "https://api.curseforge.com/v1";

    /// <summary>CurseForge 的 API Key；为空时只提供 Modrinth 结果。</summary>
    public static string? CurseForgeApiKey { get; set; }

    private static readonly HttpClient Http = CreateClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "NextCraftLauncher/0.1.0 (https://github.com/Minomiao/NextCraftLauncher)");
        return client;
    }

    // ------------------------------------------------------------ 搜索

    public static async Task<ModSearchPage> SearchAsync(
        ModPlatform? platform,
        string query,
        string? gameVersion,
        string? loader,
        int offset,
        CancellationToken cancellationToken = default)
    {
        if (platform == ModPlatform.CurseForge && string.IsNullOrWhiteSpace(CurseForgeApiKey))
            throw new InvalidOperationException("需要 CurseForge API Key（可设置环境变量 NCL_CURSEFORGE_API_KEY）");

        var tasks = new List<Task<ModSearchPage>>();

        if (platform is null or ModPlatform.Modrinth)
            tasks.Add(SearchModrinthAsync(query, gameVersion, loader, offset, cancellationToken));

        if (platform is null or ModPlatform.CurseForge && !string.IsNullOrWhiteSpace(CurseForgeApiKey))
            tasks.Add(SearchCurseForgeAsync(query, gameVersion, loader, offset, cancellationToken));

        if (tasks.Count == 0)
            return new ModSearchPage(Array.Empty<ModProject>(), offset, 0, PageSize);

        var pages = await Task.WhenAll(tasks);

        return new ModSearchPage(
            pages.SelectMany(page => page.Projects).ToList(),
            offset,
            pages.Sum(page => page.Total),
            PageSize);
    }

    private static async Task<ModSearchPage> SearchModrinthAsync(
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

        return new ModSearchPage(projects, offset, (int)Long(root, "total_hits"), PageSize);
    }

    private static async Task<ModSearchPage> SearchCurseForgeAsync(
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

        return new ModSearchPage(projects, offset, total, PageSize);
    }

    // ------------------------------------------------------------ 版本列表

    public static async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        ModProject project,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        return project.Platform == ModPlatform.Modrinth
            ? await GetModrinthVersionsAsync(project, gameVersion, loader, cancellationToken)
            : await GetCurseForgeVersionsAsync(project, gameVersion, loader, cancellationToken);
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

        try
        {
            if (platform == ModPlatform.Modrinth)
            {
                var url = $"{ModrinthApi}/projects?ids=" +
                          Uri.EscapeDataString("[" + string.Join(",", ids.Select(id => $"\"{id}\"")) + "]");
                using var document = await GetJsonAsync(url, cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ModProject>();

                return document.RootElement.EnumerateArray()
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

            var curseUrl = $"{CurseForgeApi}/mods?" +
                           string.Join("&", ids.Select(id => "modId=" + Uri.EscapeDataString(id)));
            using var curseDocument = await GetJsonAsync(curseUrl, cancellationToken, CurseForgeApiKey);
            if (!curseDocument.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ModProject>();
            }

            return data.EnumerateArray()
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
        catch (Exception exception)
        {
            LauncherLog.Warn($"查询前置 Mod 失败：{exception.Message}");
            return Array.Empty<ModProject>();
        }
    }

    // ------------------------------------------------------------ 工具

    private static async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken cancellationToken,
        string? apiKey = null)
    {
        Exception? lastError = null;

        foreach (var candidate in ModDownloadUrlResolver.BuildApiCandidates(url))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

                using var response = await Http.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return JsonDocument.Parse("{}");

                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
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

    /// <summary>API 请求：官方优先，失败后再试镜像。</summary>
    public static IReadOnlyList<string> BuildApiCandidates(string url)
    {
        var mirror = ToMirror(url);
        return string.Equals(mirror, url, StringComparison.OrdinalIgnoreCase)
            ? new[] { url }
            : new[] { url, mirror };
    }

    private static string ToMirror(string url)
        => url
            .Replace("https://api.modrinth.com", Mirror + "/modrinth", StringComparison.OrdinalIgnoreCase)
            .Replace("https://cdn.modrinth.com", Mirror, StringComparison.OrdinalIgnoreCase)
            .Replace("https://api.curseforge.com", Mirror + "/curseforge", StringComparison.OrdinalIgnoreCase)
            .Replace("https://edge.forgecdn.net", Mirror, StringComparison.OrdinalIgnoreCase)
            .Replace("https://mediafilez.forgecdn.net", Mirror, StringComparison.OrdinalIgnoreCase)
            .Replace("https://media.forgecdn.net", Mirror, StringComparison.OrdinalIgnoreCase);
}
