using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NextCraftLauncher.ViewModels;

/// <summary>
/// 模组详情页版本列表的扁平行：分组标题（可折叠）或具体文件。
/// 每个文件行都带着自己的目标版本：推荐页是分组对应的本地版本，其余页面为空。
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
        VersionItemViewModel? target,
        Action<string>? toggle,
        Action<ModVersionListRowViewModel>? select)
    {
        IsHeader = isHeader;
        Key = key;
        Title = title;
        Items = items ?? Array.Empty<ModVersionItemViewModel>();
        _isExpanded = isExpanded;
        Version = version;
        Target = target;
        ToggleCommand = toggle is null || key is null ? null : new RelayCommand(() => toggle(key));
        SelectCommand = select is null ? null : new RelayCommand(() => select(this));
    }

    public bool IsHeader { get; }

    public bool IsVersion => !IsHeader;

    /// <summary>分组标识，用于记住展开状态。</summary>
    public string? Key { get; }

    public string? Title { get; }

    public IReadOnlyList<ModVersionItemViewModel> Items { get; }

    public ModVersionItemViewModel? Version { get; }

    /// <summary>该文件要落到的本地版本；只在推荐页有值。</summary>
    public VersionItemViewModel? Target { get; }

    public IRelayCommand? ToggleCommand { get; }

    public IRelayCommand? SelectCommand { get; }

    public string CountText => IsHeader ? $"{Items.Count} 个" : string.Empty;

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private bool _isSelected;

    public static ModVersionListRowViewModel CreateHeader(
        string key,
        string title,
        IReadOnlyList<ModVersionItemViewModel> items,
        bool expanded,
        Action<string> toggle,
        VersionItemViewModel? target = null)
        => new(true, key, title, items, expanded, null, target, toggle, null);

    public static ModVersionListRowViewModel CreateVersion(
        ModVersionItemViewModel version,
        VersionItemViewModel? target,
        Action<ModVersionListRowViewModel> select)
        => new(false, null, null, null, false, version, target, null, select);
}
