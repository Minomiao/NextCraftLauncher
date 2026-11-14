using NextCraftLauncher.Core.Minecraft;

namespace NextCraftLauncher.Icons;

/// <summary>
/// 每个已安装版本在列表里用哪个图标：先看加载器，再看原版版本类型。
/// 图标名对应 <c>Assets/Icons/Blocks/</c> 或 <c>Assets/Icons/Sprites/</c>。
/// </summary>
public static class VersionIcon
{
    public static string NameFor(VersionSummary? summary)
    {
        if (summary is null) return "grass";

        return NameForLoader(summary.Loader) ?? NameForType(summary.Type);
    }

    /// <summary>加载器图标；不认识的加载器返回 <c>null</c>，由调用方回退到版本类型。</summary>
    public static string? NameForLoader(string? loader) => loader?.Trim().ToLowerInvariant() switch
    {
        "forge" => "anvil",
        "neoforge" => "neoforge",
        "fabric" or "fabric-api" => "fabric",
        "quilt" => "loom",
        _ => null,
    };

    public static string NameForType(string? type) => type switch
    {
        "snapshot" => "command_block",
        "old_beta" or "old_alpha" => "cobblestone",
        _ => "grass",
    };
}
