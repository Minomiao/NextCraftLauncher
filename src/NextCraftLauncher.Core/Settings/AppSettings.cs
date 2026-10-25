using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextCraftLauncher.Core.Settings;

public sealed class AppSettings
{
    public string MinecraftDirectory { get; set; } = GetDefaultMinecraftDirectory();
    public List<string> GameDirectories { get; set; } = new();
    public List<string> HiddenVersions { get; set; } = new();
    public string? JavaPath { get; set; }
    public string PlayerName { get; set; } = "Player";
    public int MemoryMb { get; set; } = 4096;

    /// <summary>下载原版时是否开启版本隔离。</summary>
    public bool DownloadVersionIsolated { get; set; } = true;

    /// <summary>游戏版本列表 / 清单的获取源，同时用于游戏文件与 Java 下载（默认自动）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DownloadMirror GameMirror { get; set; } = DownloadMirror.Automatic;

    /// <summary>Forge 版本列表的获取源（仅影响显示，默认自动）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DownloadMirror ForgeMirror { get; set; } = DownloadMirror.Automatic;

    /// <summary>Fabric 版本列表的获取源（仅影响显示，默认自动）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DownloadMirror FabricMirror { get; set; } = DownloadMirror.Automatic;

    /// <summary>NeoForge 版本列表的获取源（仅影响显示，默认自动）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DownloadMirror NeoForgeMirror { get; set; } = DownloadMirror.Automatic;

    /// <summary>启动器数据目录，固定位于启动器 exe 同级的 NCL 文件夹。</summary>
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "NCL");

    public static string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    public static string GetDefaultMinecraftDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ".minecraft");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                var fallback = new AppSettings();
                fallback.Normalize();
                return fallback;
            }

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    /// <summary>补齐字段、规范化路径并确保当前游戏目录在文件夹列表中。</summary>
    public void Normalize()
    {
        MinecraftDirectory = NormalizePath(MinecraftDirectory) ?? GetDefaultMinecraftDirectory();

        GameMirror = NormalizeMirror(GameMirror);
        ForgeMirror = NormalizeMirror(ForgeMirror);
        FabricMirror = NormalizeMirror(FabricMirror);
        NeoForgeMirror = NormalizeMirror(NeoForgeMirror);

        var directories = new List<string>();

        foreach (var path in GameDirectories ?? new List<string>())
        {
            var normalized = NormalizePath(path);
            if (normalized is null) continue;
            if (!directories.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
                directories.Add(normalized);
        }

        if (!directories.Any(item => string.Equals(item, MinecraftDirectory, StringComparison.OrdinalIgnoreCase)))
            directories.Insert(0, MinecraftDirectory);

        GameDirectories = directories;

        var hiddenVersions = new List<string>();
        foreach (var path in HiddenVersions ?? new List<string>())
        {
            var normalized = NormalizePath(path);
            if (normalized is null) continue;
            if (!hiddenVersions.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
                hiddenVersions.Add(normalized);
        }

        HiddenVersions = hiddenVersions;
    }

    /// <summary>旧配置或手工编辑可能写入未定义的枚举值，回退到自动。</summary>
    private static DownloadMirror NormalizeMirror(DownloadMirror mirror)
        => Enum.IsDefined(typeof(DownloadMirror), mirror) ? mirror : DownloadMirror.Automatic;

    /// <summary>转为完整路径，并去掉目录末尾的斜杠（根目录除外）。</summary>
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(fullPath);
            if (root is not null && fullPath.Length > root.Length)
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return fullPath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        catch (Exception)
        {
            // 配置写入失败不应打断启动流程。
        }
    }
}
