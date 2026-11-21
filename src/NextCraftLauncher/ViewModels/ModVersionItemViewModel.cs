using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NextCraftLauncher.Download.Mods;

namespace NextCraftLauncher.ViewModels;

/// <summary>Mod 项目的一个可下载版本。</summary>
public partial class ModVersionItemViewModel : ObservableObject
{
    public ModVersionItemViewModel(ModVersion version)
    {
        Version = version;
    }

    public ModVersion Version { get; }

    public string Title => Version.DisplayName;

    /// <summary>列表右侧显示的发布日期。</summary>
    public string DateText => Version.Published is { } published
        ? published.LocalDateTime.ToString("yyyy-MM-dd")
        : string.Empty;

    public string Subtitle
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();

            if (Version.GameVersions.Count > 0)
                parts.Add(string.Join("、", Version.GameVersions.Take(4)) +
                          (Version.GameVersions.Count > 4 ? " 等" : string.Empty));

            if (Version.Loaders.Count > 0)
                parts.Add(string.Join("、", Version.Loaders.Select(Capitalize)));

            if (Version.Published is { } published)
                parts.Add(published.LocalDateTime.ToString("yyyy-MM-dd"));

            if (!string.Equals(Version.ReleaseType, "release", StringComparison.OrdinalIgnoreCase))
                parts.Add(Version.ReleaseType);

            return string.Join(" · ", parts);
        }
    }

    /// <summary>前置 Mod 的名字；加载完成后由 ViewModel 填入。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDependency))]
    private string _dependencyText = string.Empty;

    /// <summary>有前置时才占一行，避免空行把行高拉大。</summary>
    public bool HasDependency => !string.IsNullOrEmpty(DependencyText);

    private static string Capitalize(string value) => value.Length == 0
        ? value
        : char.ToUpperInvariant(value[0]) + value[1..];
}
