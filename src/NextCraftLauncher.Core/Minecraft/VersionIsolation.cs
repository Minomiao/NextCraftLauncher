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
    PclGlobalConfig,
    LauncherDefault,
}

public sealed record VersionIsolation(bool IsIsolated, VersionIsolationSource Source)
{
    public string Description => Source switch
    {
        VersionIsolationSource.PclVersionConfig => IsIsolated ? "开启（PCL2 版本设置）" : "关闭（PCL2 版本设置）",
        VersionIsolationSource.HmclVersionConfig => IsIsolated ? "开启（HMCL 版本设置）" : "关闭（HMCL 版本设置）",
        VersionIsolationSource.FolderLayout => "开启（版本目录内已有 Mod / 存档）",
        VersionIsolationSource.PclGlobalConfig => IsIsolated ? "开启（PCL2 全局设置）" : "关闭（PCL2 全局设置）",
        VersionIsolationSource.LauncherDefault => IsIsolated ? "开启（启动器默认设置）" : "关闭（启动器默认设置）",
        _ => "关闭（未检测到隔离配置）",
    };
}

/// <summary>
/// 按优先级判断版本隔离：版本级 PCL2 / HMCL 配置 → 目录特征（mods 有文件 / saves 有子目录）
/// → 启动器的「默认版本隔离」策略。隔离后游戏目录为 versions/&lt;id&gt;。
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

        if (versionIni.GetBool("VersionArgumentIndieV2") is bool versionV2)
            return new VersionIsolation(versionV2, VersionIsolationSource.PclVersionConfig);

        // 老版本的版本级设置：-1 未决定，0 使用全局，1 手动开启，2 手动关闭。
        if (versionIni.GetInt("VersionArgumentIndie") is int legacy)
        {
            if (legacy == 1) return new VersionIsolation(true, VersionIsolationSource.PclVersionConfig);
            if (legacy == 2) return new VersionIsolation(false, VersionIsolationSource.PclVersionConfig);
        }

        var hmcl = ReadHmclIsolation(Path.Combine(versionDirectory, "hmclversion.cfg"));
        if (hmcl is not null)
            return new VersionIsolation(hmcl.Value, VersionIsolationSource.HmclVersionConfig);

        if (HasIsolationMarkers(versionDirectory))
            return new VersionIsolation(true, VersionIsolationSource.FolderLayout);

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

        return new VersionIsolation(isolated, VersionIsolationSource.LauncherDefault);
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
