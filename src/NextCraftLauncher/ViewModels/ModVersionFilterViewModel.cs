using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NextCraftLauncher.ViewModels;

/// <summary>
/// 模组详情页顶部的版本条：一格代表一个游戏版本，「全部」用 <see cref="GameVersion"/> = null 表示。
/// </summary>
public partial class ModVersionFilterViewModel : ObservableObject
{
    public ModVersionFilterViewModel(string? line, string name, Action<string?> select)
    {
        Line = line;
        Name = name;
        SelectCommand = new RelayCommand(() => select(line));
    }

    /// <summary>版本线（如 <c>1.21</c>，包含 1.21 / 1.21.1 …）；null 表示「全部」。</summary>
    public string? Line { get; }

    /// <summary>是否为「推荐」格（按已安装版本匹配）。</summary>
    public bool IsRecommend { get; init; }

    public string Name { get; }

    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
}
