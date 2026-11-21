using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NextCraftLauncher.ViewModels;

/// <summary>
/// 模组详情页版本列表的扁平行：分组标题（可折叠）或具体文件，
/// 与加载器列表共用同一套行样式。
/// </summary>
public sealed partial class ModVersionListRowViewModel : ObservableObject
{
    private ModVersionListRowViewModel(
        bool isHeader,
        string? key,
        string? title,
        IReadOnlyList<ModVersionItemViewModel>? items,
        bool isExpanded,
        ModVersionItemViewModel? version,
        Action<string>? toggle)
    {
        IsHeader = isHeader;
        Key = key;
        Title = title;
        Items = items ?? Array.Empty<ModVersionItemViewModel>();
        _isExpanded = isExpanded;
        Version = version;
        ToggleCommand = toggle is null || key is null ? null : new RelayCommand(() => toggle(key));
    }

    public bool IsHeader { get; }

    public bool IsVersion => !IsHeader;

    /// <summary>分组标识（加载器 + 发布类型），用于记住展开状态。</summary>
    public string? Key { get; }

    public string? Title { get; }

    public IReadOnlyList<ModVersionItemViewModel> Items { get; }

    public ModVersionItemViewModel? Version { get; }

    public IRelayCommand? ToggleCommand { get; }

    public string CountText => IsHeader ? $"{Items.Count} 个" : string.Empty;

    [ObservableProperty] private bool _isExpanded;

    public string Chevron => IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));

    public static ModVersionListRowViewModel CreateHeader(
        string key,
        string title,
        IReadOnlyList<ModVersionItemViewModel> items,
        bool expanded,
        Action<string> toggle)
        => new(true, key, title, items, expanded, null, toggle);

    public static ModVersionListRowViewModel CreateVersion(ModVersionItemViewModel version)
        => new(false, null, null, null, false, version, null);
}
