using System.Text.Json;
using NextCraftLauncher.Core.Settings;
using NextCraftLauncher.Core.Util;

namespace NextCraftLauncher.Core.Minecraft;

public enum VersionIsolationSource
{
    None,
    NclVersionConfig,
    PclVersionConfig,
    HmclVersionConfig,
    FolderLayout,
    LauncherDefault,
}

public sealed record VersionIsolation(bool IsIsolated, VersionIsolationSource Source)
{
    public string Description => Source switch
    {
        VersionIsolationSource.NclVersionConfig => IsIsolated ? "开启（版本设置）" : "关闭（版本设置）",
        VersionIsolationSource.PclVersionConfig => IsIsolated ? "开启（版本设置）" : "关闭（版本设置）",
        VersionIsolationSource.HmclVersionConfig => IsIsolated ? "开启（HMCL 版本设置）" : "关闭（HMCL 版本设置）",
        VersionIsolationSource.FolderLayout => "开启（版本目录内已有 Mod / 存档）",
        VersionIsolationSource.LauncherDefault => IsIsolated ? "开启（下载设置）" : "关闭（下载设置）",
        _ => "关闭（未检测到隔离配置）",
    };
}

/// <summary>
/// 判断某个版本是否隔离。
/// <para/>
/// 判定顺序：
/// <list type="number">
/// <item>版本目录的 <c>NCL/Setup.ini</c>（本启动器自己的配置）；</item>
/// <item>版本目录的 <c>PCL/Setup.ini</c> 的 <c>VersionArgumentIndieV2</c>（兼容 PCL2）；</item>
/// <item>迁移 PCL2 的老键 <c>VersionArgumentIndie</c>（1 开 / 2 关）；</item>
/// <item>兼容 HMCL 的 <c>hmclversion.cfg</c>；</item>
/// <item>版本目录里已有 mods 文件或 saves 存档时开启；</item>
/// <item>否则按下载设置里的全局策略（结合是否可装 Mod、是否正式版）计算。</item>
/// </list>
/// <para/>
/// 判定结果会**同时**写入 <c>NCL/Setup.ini</c> 与 <c>PCL/Setup.ini</c>：
/// 前者是我们自己的状态，后者保证与 PCL2 的行为一致。
/// 两边都有值但不一致时，以修改时间较新的文件为准，再把另一个写成相同值。
/// </summary>
public static class VersionIsolationDetector
{
    private const string IsolationKey = "VersionArgumentIndieV2";

    private const string LegacyIsolationKey = "VersionArgumentIndie";

    public static VersionIsolation Detect(
        string minecraftDirectory,
        string versionId,
        string? effectiveType = null,
        bool modable = false,
        DefaultIsolationMode defaultMode = DefaultIsolationMode.All)
    {
        var versionDirectory = Path.Combine(minecraftDirectory, "versions", versionId);
        var nclPath = Path.Combine(versionDirectory, "NCL", "Setup.ini");
        var pclPath = Path.Combine(versionDirectory, "PCL", "Setup.ini");

        var nclIni = IniFile.Read(nclPath);
        var pclIni = IniFile.Read(pclPath);

        // 1. 自己的版本级设置；与 PCL 并存时按修改时间取新的一份。
        bool? own = nclIni.GetBool(IsolationKey);
        bool? fromPcl = pclIni.GetBool(IsolationKey);

        if (own is not null || fromPcl is not null)
        {
            var (isolated, fromOwn) = Resolve(own, fromPcl, nclPath, pclPath);
            Persist(versionDirectory, isolated);

            return new VersionIsolation(
                isolated,
                fromOwn ? VersionIsolationSource.NclVersionConfig : VersionIsolationSource.PclVersionConfig);
        }

        // 2. PCL2 的老键：-1 未决定，0 使用全局，1 手动开启，2 手动关闭。
        if (pclIni.GetInt(LegacyIsolationKey) is int legacy && legacy > 0)
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

        var result = defaultMode switch
        {
            DefaultIsolationMode.Off => false,
            DefaultIsolationMode.Moddable => modable,
            DefaultIsolationMode.NonRelease => !isRelease,
            DefaultIsolationMode.ModdableOrNonRelease => !isRelease || modable,
            _ => true,
        };

        Persist(versionDirectory, result);
        return new VersionIsolation(result, VersionIsolationSource.LauncherDefault);
    }

    /// <summary>两边都有值且不一致时，用较新的文件；只一边有值就用它。</summary>
    private static (bool Isolated, bool FromOwn) Resolve(bool? own, bool? fromPcl, string ownPath, string pclPath)
    {
        if (own is null) return (fromPcl!.Value, false);
        if (fromPcl is null) return (own.Value, true);
        if (own.Value == fromPcl.Value) return (own.Value, true);

        try
        {
            return File.GetLastWriteTimeUtc(pclPath) > File.GetLastWriteTimeUtc(ownPath)
                ? (fromPcl.Value, false)
                : (own.Value, true);
        }
        catch (IOException)
        {
            return (own.Value, true);
        }
    }

    /// <summary>把判定结果同时写进 NCL 与 PCL 两个配置；值已一致时不重复写盘。</summary>
    private static void Persist(string versionDirectory, bool isolated)
    {
        if (!Directory.Exists(versionDirectory)) return;

        Write(Path.Combine(versionDirectory, "NCL", "Setup.ini"), isolated);
        Write(Path.Combine(versionDirectory, "PCL", "Setup.ini"), isolated);
    }

    private static void Write(string path, bool isolated)
    {
        if (IniFile.Read(path).GetBool(IsolationKey) == isolated) return;
        IniFile.SetValue(path, IsolationKey, isolated ? "True" : "False");
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

    /// <summary>mods 目录下存在文件，或 saves 目录下存在子目录。</summary>
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
