using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Download;

/// <summary>
/// 根据下载源偏好生成候选 URL 顺序。
/// </summary>
public static class DownloadUrlResolver
{
    public static IReadOnlyList<string> Build(string url, DownloadMirror mirror)
    {
        if (string.IsNullOrWhiteSpace(url)) return Array.Empty<string>();

        var mirrored = ToBmclApi(url);
        var hasMirror = !string.Equals(mirrored, url, StringComparison.OrdinalIgnoreCase);

        var legacyMirror = ToBmclApiLegacy(url);
        var hasLegacyMirror = !string.Equals(legacyMirror, url, StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(legacyMirror, mirrored, StringComparison.OrdinalIgnoreCase);

        var alternate = ToAlternateOfficial(url);
        var hasAlternate = !string.IsNullOrWhiteSpace(alternate) &&
                           !string.Equals(alternate, url, StringComparison.OrdinalIgnoreCase);

        var forgeAlternate = ToAlternateForgeMaven(url);
        var hasForgeAlternate = !string.IsNullOrWhiteSpace(forgeAlternate) &&
                                !string.Equals(forgeAlternate, url, StringComparison.OrdinalIgnoreCase);

        var cdn = ToNeoForgedCdn(url);
        var hasCdn = !string.IsNullOrWhiteSpace(cdn) &&
                     !string.Equals(cdn, url, StringComparison.OrdinalIgnoreCase);

        var urls = new List<string>(6);

        switch (mirror)
        {
            case DownloadMirror.Official:
                urls.Add(url);
                if (hasCdn) urls.Add(cdn!);
                if (hasForgeAlternate) urls.Add(forgeAlternate!);
                if (hasAlternate) urls.Add(alternate!);
                break;

            case DownloadMirror.MojangCdn:
                if (hasAlternate) urls.Add(alternate!);
                urls.Add(url);
                if (hasCdn) urls.Add(cdn!);
                if (hasForgeAlternate) urls.Add(forgeAlternate!);
                break;

            case DownloadMirror.BmclApi:
                if (hasCdn) urls.Add(cdn!);
                if (hasMirror) urls.Add(mirrored);
                if (hasLegacyMirror) urls.Add(legacyMirror);
                urls.Add(url);
                if (hasForgeAlternate) urls.Add(forgeAlternate!);
                if (hasAlternate) urls.Add(alternate!);
                break;

            default:
                // NeoForged 的 maven 官方域名在部分网络下 DNS 被污染，而同一个 CDN 的主机名通常能直连，
                // 因此自动模式下优先用 CDN 地址，官方域名作为备用。
                if (hasCdn) urls.Add(cdn!);
                urls.Add(url);
                if (hasForgeAlternate) urls.Add(forgeAlternate!);
                if (hasAlternate) urls.Add(alternate!);
                if (hasMirror) urls.Add(mirrored);
                if (hasLegacyMirror) urls.Add(legacyMirror);
                break;
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Forge 的 maven 同时挂在官方主机与文件站上（同一份内容），两个域名可以互为备用。
    /// </summary>
    private static string? ToAlternateForgeMaven(string url)
        => url.Contains("maven.minecraftforge.net", StringComparison.OrdinalIgnoreCase)
            ? url.Replace(
                "maven.minecraftforge.net",
                "files.minecraftforge.net/maven",
                StringComparison.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// NeoForged 的 maven 由 CDN 承载；直接使用 CDN 主机名可以绕开被污染的官方域名，
    /// 返回的文件与官方完全一致（同一 CDN、同一路径）。
    /// </summary>
    private static string? ToNeoForgedCdn(string url)
        => url.Contains("maven.neoforged.net", StringComparison.OrdinalIgnoreCase)
            ? url.Replace(
                "maven.neoforged.net",
                "1755624453.rsc.cdn77.org",
                StringComparison.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// Mojang 元数据有两个互通的主机：launchermeta 与 piston-meta。
    /// 资源文件、libraries 等其他主机不在此映射。
    /// </summary>
    private static string? ToAlternateOfficial(string url)
    {
        if (url.Contains("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
            return url.Replace("launchermeta.mojang.com", "piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase);

        if (url.Contains("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            return url.Replace("piston-meta.mojang.com", "launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase);

        return null;
    }

    private static string ToBmclApi(string url) => ToBmclApi(url, "bmclapi2.bangbang93.com");

    /// <summary>BMCLAPI 的旧入口域名，与主域名共用同一套路径，作为第二个尝试入口。</summary>
    private static string ToBmclApiLegacy(string url) => ToBmclApi(url, "bmclapi.bangbang93.com");

    private static string ToBmclApi(string url, string host) => url
        .Replace("piston-meta.mojang.com", host, StringComparison.OrdinalIgnoreCase)
        .Replace("piston-data.mojang.com", host, StringComparison.OrdinalIgnoreCase)
        .Replace("launchermeta.mojang.com", host, StringComparison.OrdinalIgnoreCase)
        .Replace("libraries.minecraft.net", host + "/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("resources.download.minecraft.net", host + "/assets", StringComparison.OrdinalIgnoreCase)
        // 模组加载器：版本元数据与 maven
        .Replace("meta.fabricmc.net", host + "/fabric-meta", StringComparison.OrdinalIgnoreCase)
        .Replace("meta.quiltmc.org", host + "/quilt-meta", StringComparison.OrdinalIgnoreCase)
        .Replace("maven.fabricmc.net", host + "/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("maven.minecraftforge.net", host + "/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("maven.neoforged.net/releases", host + "/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("maven.neoforged.net", host + "/maven", StringComparison.OrdinalIgnoreCase);
}
