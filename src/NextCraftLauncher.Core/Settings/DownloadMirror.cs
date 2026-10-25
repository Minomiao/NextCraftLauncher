namespace NextCraftLauncher.Core.Settings;

public enum DownloadMirror
{
    /// <summary>官方立即请求，BMCLAPI 延迟 800 ms 对冲，谁先完整返回用谁。</summary>
    Automatic,

    /// <summary>仅使用 Mojang 官方源。</summary>
    Official,

    /// <summary>优先使用 Mojang 的 piston-meta CDN，失败后回退官方主域名。</summary>
    MojangCdn,

    /// <summary>BMCLAPI 优先，失败后回退官方。</summary>
    BmclApi,
}
