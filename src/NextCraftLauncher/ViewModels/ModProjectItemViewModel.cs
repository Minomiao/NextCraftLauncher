using System;
using CommunityToolkit.Mvvm.Input;
using NextCraftLauncher.Download.Mods;

namespace NextCraftLauncher.ViewModels;

/// <summary>搜索结果里的一个 Mod 项目。</summary>
public sealed class ModProjectItemViewModel
{
    public ModProjectItemViewModel(ModProject project, Action<ModProjectItemViewModel>? select = null)
    {
        Project = project;
        SelectCommand = new RelayCommand(() => select?.Invoke(this));
    }

    public ModProject Project { get; }

    public string Name => Project.Name;

    public string Description => Project.Description ?? string.Empty;

    public string Subtitle => $"{Project.PlatformName} · 下载 {DownloadsText}";

    public string DownloadsText => Project.Downloads >= 10000
        ? $"{Project.Downloads / 10000.0:0.#} 万"
        : $"{Project.Downloads}";

    public IRelayCommand SelectCommand { get; }
}
