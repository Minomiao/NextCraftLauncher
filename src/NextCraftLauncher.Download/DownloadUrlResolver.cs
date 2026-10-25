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

        var alternate = ToAlternateOfficial(url);
        var hasAlternate = !string.IsNullOrWhiteSpace(alternate) &&
                           !string.Equals(alternate, url, StringComparison.OrdinalIgnoreCase);

        var urls = new List<string>(3);

        switch (mirror)
        {
            case DownloadMirror.Official:
                urls.Add(url);
                if (hasAlternate) urls.Add(alternate!);
                break;

            case DownloadMirror.MojangCdn:
                if (hasAlternate) urls.Add(alternate!);
                urls.Add(url);
                break;

            case DownloadMirror.BmclApi:
                if (hasMirror) urls.Add(mirrored);
                urls.Add(url);
                if (hasAlternate) urls.Add(alternate!);
                break;

            default:
                urls.Add(url);
                if (hasAlternate) urls.Add(alternate!);
                if (hasMirror) urls.Add(mirrored);
                break;
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

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

    private static string ToBmclApi(string url) => url
        .Replace("piston-meta.mojang.com", "bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
        .Replace("piston-data.mojang.com", "bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
        .Replace("launchermeta.mojang.com", "bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase)
        .Replace("libraries.minecraft.net", "bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("resources.download.minecraft.net", "bmclapi2.bangbang93.com/assets", StringComparison.OrdinalIgnoreCase)
        // 模组加载器：版本元数据与 maven
        .Replace("meta.fabricmc.net", "bmclapi2.bangbang93.com/fabric-meta", StringComparison.OrdinalIgnoreCase)
        .Replace("meta.quiltmc.org", "bmclapi2.bangbang93.com/quilt-meta", StringComparison.OrdinalIgnoreCase)
        .Replace("maven.fabricmc.net", "bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("maven.minecraftforge.net", "bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("maven.neoforged.net/releases", "bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase)
        .Replace("maven.neoforged.net", "bmclapi2.bangbang93.com/maven", StringComparison.OrdinalIgnoreCase);
}
