using NextCraftLauncher.Core.Util;

namespace NextCraftLauncher.Core.Minecraft;

public sealed record VersionSummary(
    string Id,
    string Type,
    string? InheritsFrom,
    string? Loader,
    int RequiredJava,
    string JsonPath)
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
                result.Add(new VersionSummary(
                    id,
                    info.Type ?? "custom",
                    info.InheritsFrom,
                    DetectLoader(id, info.MainClass, info.InheritsFrom),
                    FindJavaMajor(minecraftDirectory, info, 0),
                    jsonPath));
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

    private static string? DetectLoader(string id, string? mainClass, string? inheritsFrom)
    {
        if (inheritsFrom is null) return null;

        var lower = id.ToLowerInvariant();
        if (lower.Contains("neoforge")) return "NeoForge";
        if (lower.Contains("fabric")) return "Fabric";
        if (lower.Contains("quilt")) return "Quilt";
        if (lower.Contains("optifine")) return "OptiFine";
        if (lower.Contains("forge")) return "Forge";

        if (mainClass?.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) == true) return "Fabric";
        if (mainClass?.Contains("quiltmc", StringComparison.OrdinalIgnoreCase) == true) return "Quilt";
        if (mainClass?.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase) == true) return "NeoForge";

        return "Modded";
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
