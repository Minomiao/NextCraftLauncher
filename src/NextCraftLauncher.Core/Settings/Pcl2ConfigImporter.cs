using System.Text.Json;
using Microsoft.Win32;
using NextCraftLauncher.Core.Util;

namespace NextCraftLauncher.Core.Settings;

public sealed record Pcl2Location(
    string BaseDirectory,
    string ConfigFile,
    string GameDirectory,
    string? LauncherExecutable,
    bool HasGlobalConfig,
    string? PreferredVersionId,
    IReadOnlyList<string> GameDirectories);

public sealed class Pcl2ImportResult
{
    public bool FoundConfig { get; init; }
    public int? MemoryMb { get; init; }
    public bool? Isolated { get; init; }
    public string? JavaPath { get; init; }
    public string? JavaRange { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 定位 PCL2 安装并读取 Setup.ini。PCL2 的配置位于“启动器基目录\PCL\Setup.ini”，
/// 游戏目录来自 LaunchFolderSelect（其中 $ 表示启动器基目录）。
/// </summary>
public static class Pcl2ConfigImporter
{
    private static readonly string[] LauncherFileNames =
    {
        "PCL.exe",
        "PCL2.exe",
        "Plain Craft Launcher 2.exe",
    };

    /// <summary>
    /// 支持传入：PCL 启动器 exe、PCL 配置文件夹、PCL 基目录或游戏目录。
    /// 会向上查找最多 5 层，找到包含 PCL\Setup.ini、启动器 exe 或 versions 目录的基目录。
    /// </summary>
    public static Pcl2Location? ResolveLocation(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) return null;

        string? startDirectory;
        string? selectedFile = null;

        if (File.Exists(selectedPath))
        {
            selectedFile = Path.GetFullPath(selectedPath);
            startDirectory = Path.GetDirectoryName(selectedFile);
        }
        else if (Directory.Exists(selectedPath))
        {
            startDirectory = Path.GetFullPath(selectedPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        else
        {
            return null;
        }

        if (startDirectory is null) return null;

        var candidates = new List<string>();
        var current = startDirectory;
        for (var depth = 0; depth < 6 && !string.IsNullOrEmpty(current); depth++)
        {
            candidates.Add(current);
            current = Path.GetDirectoryName(current);
        }

        // 用户选择的是 PCL 配置文件夹（...\PCL），基目录应为它的父目录。
        if (string.Equals(Path.GetFileName(startDirectory), "PCL", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(startDirectory);
            if (!string.IsNullOrEmpty(parent) &&
                !candidates.Contains(parent, StringComparer.OrdinalIgnoreCase))
                candidates.Insert(0, parent);
        }

        // 1. 优先使用存在全局配置的基目录。
        foreach (var baseDirectory in candidates)
        {
            var configFile = Path.Combine(baseDirectory, "PCL", "Setup.ini");
            if (File.Exists(configFile))
                return BuildLocation(baseDirectory, configFile, selectedFile);
        }

        // 2. 其次使用存在启动器本体的目录。
        foreach (var baseDirectory in candidates)
        {
            var launcher = FindLauncher(baseDirectory) ?? MatchSelectedFile(baseDirectory, selectedFile);
            if (launcher is not null)
                return BuildLocation(baseDirectory, Path.Combine(baseDirectory, "PCL", "Setup.ini"), launcher);
        }

        // 3. 最后接受一个真正的游戏目录（包含 versions）。
        foreach (var baseDirectory in candidates)
        {
            if (Directory.Exists(Path.Combine(baseDirectory, "versions")))
                return BuildLocation(baseDirectory, Path.Combine(baseDirectory, "PCL", "Setup.ini"), selectedFile);
        }

        return null;
    }

    public static Pcl2ImportResult Import(Pcl2Location location, string? versionId)
    {
        var messages = new List<string>();

        var global = IniFile.Read(location.ConfigFile);
        var versionConfigPath = versionId is null
            ? null
            : Path.Combine(location.GameDirectory, "versions", versionId, "PCL", "Setup.ini");
        var version = versionConfigPath is null ? IniFile.Empty : IniFile.Read(versionConfigPath);

        if (!global.Exists && !version.Exists)
        {
            messages.Add("未找到 PCL2 配置（PCL\\Setup.ini 或版本目录下的 PCL\\Setup.ini）");
            return new Pcl2ImportResult { FoundConfig = false, Messages = messages };
        }

        int? memoryMb = null;
        switch (version.GetInt("VersionRamType"))
        {
            case 1:
                if (version.GetInt("VersionRamCustom") is int versionRam)
                    memoryMb = RamIndexToMegabytes(versionRam);
                break;
            case 2:
                messages.Add("PCL2 对该版本使用自动内存分配，未导入");
                break;
            default:
                var globalRamType = global.GetInt("LaunchRamType");
                if (globalRamType == 1 && global.GetInt("LaunchRamCustom") is int globalRam)
                    memoryMb = RamIndexToMegabytes(globalRam);
                else if (globalRamType is 0 or null)
                    messages.Add("PCL2 使用自动内存分配，未导入");
                break;
        }

        bool? isolated = null;
        if (version.GetBool("VersionArgumentIndieV2") is bool versionV2)
        {
            isolated = versionV2;
        }
        else if (version.GetInt("VersionArgumentIndie") is int legacy)
        {
            if (legacy == 1) isolated = true;
            else if (legacy == 2) isolated = false;
        }

        string? javaPath = null;
        string? javaRange = null;
        switch (version.GetInt("VersionArgumentJavaV2"))
        {
            case 1:
                javaRange = version.GetString("VersionArgumentJavaRange");
                if (string.IsNullOrWhiteSpace(javaRange))
                {
                    javaRange = null;
                    messages.Add("PCL2 指定了 Java 版本范围，但范围为空");
                }
                break;
            case 2:
                if (versionId is not null)
                {
                    javaPath = FindJavaInFolder(Path.Combine(location.GameDirectory, "versions", versionId));
                    if (javaPath is null) messages.Add("PCL2 要求使用版本文件夹中的 Java，但没有找到");
                }
                break;
            case 3:
                javaPath = ParseForcedJava(version.GetString("VersionArgumentJavaSelect"));
                if (javaPath is null) messages.Add("PCL2 指定了特定 Java，但未能解析出有效路径");
                break;
        }

        if (javaPath is not null)
            messages.Add($"指定 Java：{javaPath}");
        else if (javaRange is not null)
            messages.Add($"Java 版本范围：{javaRange}");

        messages.Add("PCL2 的账号 / 离线用户名保存在系统注册表中并经过加密，未导入");

        if (!string.IsNullOrWhiteSpace(version.GetString("VersionAdvanceJvm")) ||
            !string.IsNullOrWhiteSpace(global.GetString("LaunchAdvanceJvm")))
            messages.Add("PCL2 的自定义 JVM / 游戏参数尚未导入");

        return new Pcl2ImportResult
        {
            FoundConfig = true,
            MemoryMb = memoryMb,
            Isolated = isolated,
            JavaPath = javaPath,
            JavaRange = javaRange,
            Messages = messages,
        };
    }

    /// <summary>
    /// 将 PCL2 的内存滑块索引换算为 MB（与 PageSetupLaunch 中的分段公式一致）。
    /// </summary>
    public static int RamIndexToMegabytes(int index)
    {
        double gigabytes;
        if (index <= 12) gigabytes = index * 0.1 + 0.3;
        else if (index <= 25) gigabytes = (index - 12) * 0.5 + 1.5;
        else if (index <= 33) gigabytes = (index - 25) + 8;
        else gigabytes = (index - 33) * 2.0 + 16;

        return (int)Math.Round(gigabytes * 1024, MidpointRounding.AwayFromZero);
    }

    private static Pcl2Location BuildLocation(string baseDirectory, string configFile, string? selectedFile)
    {
        var ini = IniFile.Read(configFile);
        var launcher = FindLauncher(baseDirectory) ?? MatchSelectedFile(baseDirectory, selectedFile);
        var gameDirectory = ResolveGameDirectory(baseDirectory, ini);
        var gameDirectories = ResolveGameDirectories(baseDirectory, gameDirectory);

        var preferredVersion = ini.GetString("LaunchVersionSelect");
        if (string.IsNullOrWhiteSpace(preferredVersion)) preferredVersion = null;

        return new Pcl2Location(
            baseDirectory,
            configFile,
            gameDirectory,
            launcher,
            ini.Exists,
            preferredVersion?.Trim(),
            gameDirectories);
    }

    private static string? FindLauncher(string baseDirectory)
    {
        foreach (var fileName in LauncherFileNames)
        {
            var path = Path.Combine(baseDirectory, fileName);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string? MatchSelectedFile(string baseDirectory, string? selectedFile)
    {
        if (selectedFile is null) return null;

        var directory = Path.GetDirectoryName(selectedFile);
        return string.Equals(directory, baseDirectory, StringComparison.OrdinalIgnoreCase)
            ? selectedFile
            : null;
    }

    private static string ResolveGameDirectory(string baseDirectory, IniFile global)
    {
        var configured = ResolveConfiguredPath(baseDirectory, global.GetString("LaunchFolderSelect"));
        if (configured is not null) return configured;

        if (Directory.Exists(Path.Combine(baseDirectory, "versions"))) return baseDirectory;
        return Path.Combine(baseDirectory, ".minecraft");
    }

    /// <summary>
    /// PCL2 的文件夹列表来自三处：当前文件夹、注册表中的自定义文件夹，以及自动检测到的官方启动器目录。
    /// </summary>
    private static IReadOnlyList<string> ResolveGameDirectories(string baseDirectory, string currentDirectory)
    {
        var directories = new List<string>();

        void Add(string? path)
        {
            var normalized = NormalizeDirectory(path);
            if (normalized is null) return;
            if (directories.Any(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)))
                return;
            directories.Add(normalized);
        }

        Add(currentDirectory);

        // PCL2：HKCU\Software\PCL\LaunchFolders，格式为“名称>路径|名称>路径”。
        foreach (var entry in ReadLaunchFolderEntries())
        {
            var separator = entry.IndexOf('>');
            var path = separator >= 0 ? entry[(separator + 1)..] : entry;
            Add(ResolveConfiguredPath(baseDirectory, path));
        }

        // PCL2 版本页会固定列出官方启动器文件夹（若存在 versions）。
        var official = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".minecraft");
        if (Directory.Exists(Path.Combine(official, "versions")))
            Add(official);

        return directories;
    }

    private static string? ResolveConfiguredPath(string baseDirectory, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim().Trim('"');

        if (value.StartsWith('$'))
        {
            value = value[1..].TrimStart('\\', '/');
            if (value.Length == 0) return baseDirectory;
            value = Path.Combine(baseDirectory, value);
        }
        else if (!Path.IsPathRooted(value))
        {
            value = Path.Combine(baseDirectory, value);
        }

        try
        {
            return TrimDirectoryEnd(Path.GetFullPath(value));
        }
        catch (Exception)
        {
            // 配置里的路径非法时忽略该项。
            return null;
        }
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var fullPath = TrimDirectoryEnd(Path.GetFullPath(path));
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TrimDirectoryEnd(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is null || path.Length <= root.Length) return path;
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static List<string> ReadLaunchFolderEntries()
    {
        var entries = new List<string>();

#pragma warning disable CA1416 // PCL2 是 Windows 启动器，注册表读取无需跨平台。
        // 正式版写入 PCL，开源版写入 PCLDebug。
        foreach (var keyPath in new[] { @"Software\PCL", @"Software\PCLDebug" })
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                if (key?.GetValue("LaunchFolders") is not string raw || string.IsNullOrWhiteSpace(raw))
                    continue;

                entries.AddRange(raw.Split('|', StringSplitOptions.RemoveEmptyEntries));
                break;
            }
            catch (Exception)
            {
                // 注册表不可读时忽略，继续使用其他来源。
            }
        }
#pragma warning restore CA1416

        return entries;
    }

    private static string? ParseForcedJava(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Path", out var pathElement) &&
                pathElement.ValueKind == JsonValueKind.String)
            {
                return NormalizeExistingFile(pathElement.GetString());
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 时按纯路径处理。
        }

        return NormalizeExistingFile(raw);
    }

    private static string? NormalizeExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindJavaInFolder(string directory)
    {
        try
        {
            foreach (var fileName in new[] { "java.exe", "java" })
            {
                var found = Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (found is not null) return found;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
