using System.Text.Json;
using NextCraftLauncher.Core.Settings;
using NextCraftLauncher.Core.Util;

namespace NextCraftLauncher.Core.Minecraft;

public enum VersionIsolationSource
{
    None,
    PclVersionConfig,
    HmclVersionConfig,
    FolderLayout,
    LauncherDefault,
}

public sealed record VersionIsolation(bool IsIsolated, VersionIsolationSource Source)
{
    public string Description => Source switch
    {
        VersionIsolationSource.PclVersionConfig => IsIsolated ? "开启（版本设置）" : "关闭（版本设置）",
        VersionIsolationSource.HmclVersionConfig => IsIsolated ? "开启（HMCL 版本设置）" : "关闭（HMCL 版本设置）",
        VersionIsolationSource.FolderLayout => "开启（版本目录内已有 Mod / 存档）",
        VersionIsolationSource.LauncherDefault => IsIsolated ? "开启（下载设置）" : "关闭（下载设置）",
        _ => "关闭（未检测到隔离配置）",
    };
}

/// <summary>
/// 判断某个版本是否隔离，行为与 PCL2 的 <c>McInstance.PathIndie</c> 一致：
/// <list type="number">
/// <item>版本目录的 <c>PCL/Setup.ini</c> 已有 <c>VersionArgumentIndieV2</c> 时直接采用；</item>
/// <item>否则迁移老的 <c>VersionArgumentIndie</c>（1 开 / 2 关）；</item>
/// <item>否则兼容 HMCL 的 <c>hmclversion.cfg</c>；</item>
/// <item>否则版本目录里已有 mods 文件或 saves 存档时开启；</item>
/// <item>否则按下载设置里的全局策略（结合是否可装 Mod、是否正式版）计算。</item>
/// </list>
/// 与 PCL2 一样，判定结果会写回该版本的 <c>PCL/Setup.ini</c>，之后这个版本就固定使用该值；
/// 想让它重新跟随全局策略，删掉这一项即可（PCL2 的「初始化版本独立设置」也是这么做的）。
/// </summary>
public static class VersionIsolationDetector
{
    public static VersionIsolation Detect(
        string minecraftDirectory,
        string versionId,
        string? effectiveType = null,
        bool modable = false,
        DefaultIsolationMode defaultMode = DefaultIsolationMode.All)
    {
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionId);
        var versionIni = IniFile.Read(Path.Combine(versionDirectory, "PCL", "Setup.ini"));

        // 1. 版本级设置（PCL2 写入的 PCL/Setup.ini）
        if (versionIni.GetBool("VersionArgumentIndieV2") is bool versionV2)
            return new VersionIsolation(versionV2, VersionIsolationSource.PclVersionConfig);

        // 2. 老版本的版本级设置：-1 未决定，0 使用全局，1 手动开启，2 手动关闭。
        if (versionIni.GetInt("VersionArgumentIndie") is int legacy && legacy > 0)
        {
            var migrated = legacy == 1;
            Persist(versionDirectory, migrated);
            return new VersionIsolation(migrated, VersionIsolationSource.PclVersionConfig);
        }

        // 3. HMCL 的版本独立配置
        if (ReadHmclIsolation(Path.Combine(versionDirectory, "hmclversion.cfg")) is bool hmcl)
        {
            Persist(versionDirectory, hmcl);
            return new VersionIsolation(hmcl, VersionIsolationSource.HmclVersionConfig);
        }

        // 4. 版本目录里已经有 Mod / 存档时必须是隔离的
        if (HasIsolationMarkers(versionDirectory))
        {
            Persist(versionDirectory, true);
            return new VersionIsolation(true, VersionIsolationSource.FolderLayout);
        }

        // 5. 全局策略
        var isRelease = !string.Equals(effectiveType, "snapshot", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(effectiveType, "old_beta", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(effectiveType, "old_alpha", StringComparison.OrdinalIgnoreCase);

        var isolated = defaultMode switch
        {
            DefaultIsolationMode.Off => false,
            DefaultIsolationMode.Moddable => modable,
            DefaultIsolationMode.NonRelease => !isRelease,
            DefaultIsolationMode.ModdableOrNonRelease => !isRelease || modable,
            _ => true,
        };

        Persist(versionDirectory, isolated);
        return new VersionIsolation(isolated, VersionIsolationSource.LauncherDefault);
    }

    /// <summary>
    /// 与 PCL2 一致：判定结果写回版本级配置，之后这个版本不再跟随全局策略。
    /// </summary>
    private static void Persist(string versionDirectory, bool isolated)
    {
        if (!Directory.Exists(versionDirectory)) return;

        IniFile.SetValue(
            Path.Combine(versionDirectory, "PCL", "Setup.ini"),
            "VersionArgumentIndieV2",
            isolated ? "True" : "False");
    }

    private static bool? ReadHmclIsolation(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var text = File.ReadAllText(path);

            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                foreach (var key in new[] { "isolate", "isolation" })
                {
                    if (!document.RootElement.TryGetProperty(key, out var value)) continue;

                    if (value.ValueKind == JsonValueKind.True) return true;
                    if (value.ValueKind == JsonValueKind.False) return false;
                    if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                        return parsed;
                }

                return null;
            }
            catch (JsonException)
            {
                // HMCL 旧版本可能写成键值对文本。
                foreach (var line in text.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("isolate", StringComparison.OrdinalIgnoreCase)) continue;

                    var separator = trimmed.IndexOf('=');
                    if (separator > 0 && bool.TryParse(trimmed[(separator + 1)..].Trim(), out var parsed))
                        return parsed;
                }

                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 与 PCL2 一致：mods 目录下存在文件，或 saves 目录下存在子目录。
    /// </summary>
    private static bool HasIsolationMarkers(string versionDirectory)
    {
        try
        {
            var mods = Path.Combine(versionDirectory, "mods");
            if (Directory.Exists(mods) && Directory.EnumerateFiles(mods).Any()) return true;

            var saves = Path.Combine(versionDirectory, "saves");
            if (Directory.Exists(saves) && Directory.EnumerateDirectories(saves).Any()) return true;
        }
        catch (IOException)
        {
            return false;
        }

        return false;
    }
}
