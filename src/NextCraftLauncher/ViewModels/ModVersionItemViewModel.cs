using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NextCraftLauncher.Download.Mods;

namespace NextCraftLauncher.ViewModels;

/// <summary>Mod 项目的一个可下载版本。</summary>
public partial class ModVersionItemViewModel : ObservableObject
{
    public ModVersionItemViewModel(ModVersion version, Action<ModVersionItemViewModel>? download = null)
    {
        Version = version;
        DownloadCommand = new RelayCommand(() => download?.Invoke(this));
    }

    public ModVersion Version { get; }

    public string Title => Version.DisplayName;

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
    [ObservableProperty] private string _dependencyText = string.Empty;

    public IRelayCommand DownloadCommand { get; }

    private static string Capitalize(string value) => value.Length == 0
        ? value
        : char.ToUpperInvariant(value[0]) + value[1..];
}
