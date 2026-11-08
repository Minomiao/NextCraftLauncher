namespace NextCraftLauncher.Download.Mods;

public enum ModPlatform
{
    CurseForge,
    Modrinth,
}

/// <summary>社区 Mod 项目（Modrinth / CurseForge）。</summary>
public sealed record ModProject(
    string Id,
    ModPlatform Platform,
    string Slug,
    string Name,
    string? Description,
    long Downloads,
    DateTimeOffset? Updated,
    string? IconUrl)
{
    public string PlatformName => Platform == ModPlatform.Modrinth ? "Modrinth" : "CurseForge";
}

/// <summary>项目的单个可下载文件。</summary>
public sealed record ModVersion(
    string Id,
    string ProjectId,
    ModPlatform Platform,
    string Name,
    string? VersionNumber,
    string FileName,
    IReadOnlyList<string> DownloadUrls,
    string? Sha1,
    long Size,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    string ReleaseType,
    DateTimeOffset? Published,
    IReadOnlyList<string> Dependencies)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FileName : Name;
}

public sealed record ModSearchPage(
    IReadOnlyList<ModProject> Projects,
    int Page,
    int PageSize,
    int Total)
{
    /// <summary>总页数，按接口返回的结果总数与每页条数计算。</summary>
    public int TotalPages => PageSize <= 0 ? 1 : Math.Max(1, (Total + PageSize - 1) / PageSize);
}
