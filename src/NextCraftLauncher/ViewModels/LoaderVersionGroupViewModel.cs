using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NextCraftLauncher.ViewModels;

/// <summary>
/// 版本详情页里的一个加载器分组（Forge / Fabric / NeoForge）。
/// 没有可用版本时保持不可展开。
/// </summary>
public partial class LoaderVersionGroupViewModel : ObservableObject
{
    public LoaderVersionGroupViewModel(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public ObservableCollection<LoaderVersionItemViewModel> Versions { get; } = new();

    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "加载中…";
    [ObservableProperty] private string? _errorText;
}
