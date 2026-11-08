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

    /// <summary>封面图地址（可能为空）。</summary>
    public string? IconUrl => Project.IconUrl;

    /// <summary>图标缓存键，形如 <c>modrinth/P7dR8mSH</c>。</summary>
    public string IconKey => ModIconCache.MakeKey(Project.Platform, Project.Id);

    public string Subtitle => $"{Project.PlatformName} · 下载 {DownloadsText}";

    public string DownloadsText => Project.Downloads >= 10000
        ? $"{Project.Downloads / 10000.0:0.#} 万"
        : $"{Project.Downloads}";

    public IRelayCommand SelectCommand { get; }
}
