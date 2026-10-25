using System;
using CommunityToolkit.Mvvm.Input;

namespace NextCraftLauncher.ViewModels;

public enum LoaderVersionRowKind
{
    Header,
    Version,
    Empty,
}

/// <summary>
/// 版本详情页加载器列表的扁平行（分组标题、加载器版本或空占位），
/// 与游戏版本列表共用同一套行样式。
/// </summary>
public sealed class LoaderVersionListRowViewModel
{
    private LoaderVersionListRowViewModel(
        LoaderVersionRowKind kind,
        LoaderVersionGroupViewModel? group,
        LoaderVersionItemViewModel? item,
        string? text,
        Action? toggle)
    {
        Kind = kind;
        Group = group;
        Item = item;
        Text = text;
        ToggleCommand = toggle is null ? null : new RelayCommand(toggle);
    }

    public LoaderVersionRowKind Kind { get; }

    public LoaderVersionGroupViewModel? Group { get; }

    public LoaderVersionItemViewModel? Item { get; }

    public string? Text { get; }

    public bool HasTag => Item?.HasTag ?? false;

    /// <summary>加载器版本号；选中状态与安装命令由 <see cref="Item"/> 提供。</summary>
    public string? Version => Item?.Version;

    public string? Tag => Item?.Tag;

    public IRelayCommand? ToggleCommand { get; }

    public bool IsHeader => Kind == LoaderVersionRowKind.Header;

    public bool IsVersion => Kind == LoaderVersionRowKind.Version;

    public bool IsEmpty => Kind == LoaderVersionRowKind.Empty;

    public string? Title => Group?.DisplayName;

    public string? Status => Group?.StatusText;

    public string? Error => Group?.ErrorText;

    public string? Chevron => Group is null ? null : Group.IsExpanded ? "▾" : "▸";

    public static LoaderVersionListRowViewModel CreateHeader(
        LoaderVersionGroupViewModel group,
        Action toggle)
        => new(LoaderVersionRowKind.Header, group, null, null, toggle);

    public static LoaderVersionListRowViewModel CreateVersion(LoaderVersionItemViewModel item)
        => new(LoaderVersionRowKind.Version, null, item, null, null);

    public static LoaderVersionListRowViewModel CreateEmpty(
        LoaderVersionGroupViewModel group,
        string text)
        => new(LoaderVersionRowKind.Empty, group, null, text, null);
}
