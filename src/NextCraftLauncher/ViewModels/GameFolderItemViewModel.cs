using System;
using System.IO;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.ViewModels;

public sealed class GameFolderItemViewModel
{
    public GameFolderItemViewModel(string path)
    {
        Path = path;
        Name = BuildName(path);
    }

    public string Path { get; }

    public string Name { get; }

    private static string BuildName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(未命名)";

        var defaultDirectory = AppSettings.GetDefaultMinecraftDirectory();
        if (string.Equals(TrimEnd(path), TrimEnd(defaultDirectory), StringComparison.OrdinalIgnoreCase))
            return "官方启动器文件夹";

        var trimmed = TrimEnd(path);
        var name = System.IO.Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) name = trimmed;

        return name.StartsWith('.') && name.Length > 1 ? name[1..] : name;
    }

    private static string TrimEnd(string path)
        => path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
}
