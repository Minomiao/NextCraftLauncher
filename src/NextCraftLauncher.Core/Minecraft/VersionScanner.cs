using NextCraftLauncher.Core.Util;

namespace NextCraftLauncher.Core.Minecraft;

public sealed record VersionSummary(
    string Id,
    string Type,
    string? InheritsFrom,
    string? Loader,
    int RequiredJava,
    string JsonPath,
    string? ClientVersion)
{
    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type,
    };
}

public static class VersionScanner
{
    /// <summary>
    /// 扫描 &lt;minecraft&gt;/versions 下所有带同名 JSON 的版本目录。
    /// </summary>
    public static IReadOnlyList<VersionSummary> Scan(string minecraftDirectory)
    {
        var versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        if (!Directory.Exists(versionsDirectory)) return Array.Empty<VersionSummary>();

        var result = new List<VersionSummary>();
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            var id = Path.GetFileName(directory);
            var jsonPath = Path.Combine(directory, id + ".json");
            if (!File.Exists(jsonPath)) continue;

            try
            {
                var info = VersionInfo.Load(jsonPath);
                var loader = DetectLoader(id, info.MainClass, info.InheritsFrom);
                result.Add(new VersionSummary(
                    id,
                    info.Type ?? "custom",
                    info.InheritsFrom,
                    loader,
                    FindJavaMajor(minecraftDirectory, info, 0),
                    jsonPath,
                    ResolveVanillaVersion(directory, id, loader, info)));
            }
            catch (Exception)
            {
                // 单个版本损坏不应该让整个列表不可用。
            }
        }

        result.Sort(static (a, b) =>
        {
            var type = TypePriority(a.Type).CompareTo(TypePriority(b.Type));
            return type != 0 ? type : NaturalStringComparer.Instance.Compare(b.Id, a.Id);
        });

        return result;
    }

    /// <summary>
    /// 版本对应的原版版本号（Mod 兼容性判断用）。安装加载器时会写入 <c>clientVersion</c>；
    /// PCL2 导入的版本读它缓存的 <c>VersionVanillaName</c>；更早的版本从
    /// 「原版版本号-加载器 加载器版本」这样的目录名里截取。
    /// 都拿不到时返回 <c>null</c>，调用方不能拿它当筛选条件。
    /// </summary>
    private static string? ResolveVanillaVersion(
        string versionDirectory,
        string id,
        string? loader,
        VersionInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.ClientVersion)) return info.ClientVersion.Trim();

        // 原版版本：版本号就是它自己。
        if (loader is null) return id;

        if (!string.IsNullOrWhiteSpace(info.InheritsFrom) &&
            !string.Equals(info.InheritsFrom, id, StringComparison.OrdinalIgnoreCase))
        {
            return info.InheritsFrom.Trim();
        }

        var cached = IniFile.Read(Path.Combine(versionDirectory, "PCL", "Setup.ini"))
            .GetString("VersionVanillaName");
        if (!string.IsNullOrWhiteSpace(cached) &&
            !string.Equals(cached.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return cached.Trim();
        }

        return ExtractVanillaVersionFromId(id);
    }

    private static string? ExtractVanillaVersionFromId(string id)
    {
        string[] markers = { "-neoforge", "-fabric", "-quilt", "-liteloader", "-forge", "-optifine" };
        var lower = id.ToLowerInvariant();

        var cut = -1;
        foreach (var marker in markers)
        {
            var index = lower.IndexOf(marker, StringComparison.Ordinal);
            if (index <= 0) continue;
            cut = cut < 0 ? index : Math.Min(cut, index);
        }

        if (cut <= 0) return null;

        var version = id[..cut].TrimEnd('-', ' ', '_');

        // 只接受形如 1.21.1 / 26.3 / 26.3-snapshot-2 的版本号，避免把自定义名字当成版本。
        return version.Length > 0 && char.IsDigit(version[0]) && version.Contains('.')
            ? version
            : null;
    }

    private static int FindJavaMajor(string minecraftDirectory, VersionInfo info, int depth)
    {
        if (info.JavaVersion is { MajorVersion: > 0 } javaVersion) return javaVersion.MajorVersion;

        if (depth < 8 && !string.IsNullOrEmpty(info.InheritsFrom))
        {
            var parentJson = Path.Combine(minecraftDirectory, "versions", info.InheritsFrom, info.InheritsFrom + ".json");
            if (File.Exists(parentJson))
            {
                try
                {
                    return FindJavaMajor(minecraftDirectory, VersionInfo.Load(parentJson), depth + 1);
                }
                catch (Exception)
                {
                    // 忽略无法读取的父版本。
                }
            }
        }

        return 8;
    }

    internal static string? DetectLoader(string id, string? mainClass, string? inheritsFrom)
    {
        // 加载器版本不再带 inheritsFrom（安装时已合并成自包含的 JSON），
        // 所以先按名称识别，再退回主类。
        var lower = id.ToLowerInvariant();
        if (lower.Contains("neoforge")) return "NeoForge";
        if (lower.Contains("fabric")) return "Fabric";
        if (lower.Contains("quilt")) return "Quilt";
        if (lower.Contains("optifine")) return "OptiFine";
        if (lower.Contains("forge")) return "Forge";

        if (mainClass?.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) == true) return "Fabric";
        if (mainClass?.Contains("quiltmc", StringComparison.OrdinalIgnoreCase) == true) return "Quilt";
        if (mainClass?.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase) == true) return "NeoForge";

        return inheritsFrom is null ? null : "Modded";
    }

    private static int TypePriority(string type) => type switch
    {
        "release" => 0,
        "snapshot" => 1,
        "old_beta" => 2,
        "old_alpha" => 3,
        _ => 4,
    };
}
