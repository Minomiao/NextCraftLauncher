using System.Text.Json;
using System.Text.Json.Nodes;

namespace NextCraftLauncher.Download.Loaders;

/// <summary>版本目录命名与安装后的重命名。</summary>
public static class VersionNaming
{
    /// <summary>去掉文件名非法字符；空名称返回空字符串。</summary>
    public static string Normalize(string? name)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;

        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '-');

        value = value.Trim().TrimEnd('.');
        return value is "." or ".." ? string.Empty : value;
    }

    /// <summary>默认名称：原版为 MC 版本号；选中加载器后为「版本-加载器 版本」。</summary>
    public static string BuildDefault(
        string minecraftVersion,
        string? loaderKey,
        string? loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(loaderKey) || string.IsNullOrWhiteSpace(loaderVersion))
            return minecraftVersion;

        var displayName = loaderKey.ToLowerInvariant() switch
        {
            "forge" => "Forge",
            "fabric" => "Fabric",
            "neoforge" => "NeoForge",
            _ => loaderKey,
        };

        return $"{minecraftVersion}-{displayName} {loaderVersion}";
    }

    /// <summary>
    /// 把安装好的版本目录改成用户指定的名称，并同步更新目录内的 JSON 与客户端 jar。
    /// 名称相同或为空时保持原样。
    /// </summary>
    public static string RenameInstalledVersion(
        string minecraftDirectory,
        string installedId,
        string? desiredName)
    {
        var name = Normalize(desiredName);
        if (name.Length == 0 ||
            string.Equals(name, installedId, StringComparison.OrdinalIgnoreCase))
        {
            return installedId;
        }

        var versionsRoot = Path.Combine(minecraftDirectory, "versions");
        var source = Path.Combine(versionsRoot, installedId);
        var target = Path.Combine(versionsRoot, name);

        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"找不到已安装的版本目录：{source}");
        if (Directory.Exists(target))
            throw new IOException($"版本名 “{name}” 已存在，请换一个名称。");

        Directory.Move(source, target);

        RenameFileIfExists(
            Path.Combine(target, installedId + ".json"),
            Path.Combine(target, name + ".json"));

        var jsonPath = Path.Combine(target, name + ".json");
        if (File.Exists(jsonPath))
        {
            var json = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject
                       ?? throw new InvalidDataException("版本 JSON 格式不正确。");

            json["id"] = name;
            File.WriteAllText(jsonPath, json.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }

        // 原版版本目录里还有客户端 jar，启动器按目录名查找它。
        RenameFileIfExists(
            Path.Combine(target, installedId + ".jar"),
            Path.Combine(target, name + ".jar"));

        return name;
    }

    private static void RenameFileIfExists(string source, string target)
    {
        if (!File.Exists(source) || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return;

        File.Move(source, target, overwrite: false);
    }
}
