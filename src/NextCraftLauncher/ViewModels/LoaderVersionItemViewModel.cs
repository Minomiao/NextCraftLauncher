using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NextCraftLauncher.ViewModels;

/// <summary>加载器版本行，可选择后安装。</summary>
public sealed partial class LoaderVersionItemViewModel : ObservableObject
{
    public LoaderVersionItemViewModel(
        string version,
        string? tag,
        string loaderKey,
        Action<LoaderVersionItemViewModel> select,
        bool isSelected = false)
    {
        Version = version;
        Tag = tag;
        LoaderKey = loaderKey;
        _isSelected = isSelected;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Version { get; }

    public string? Tag { get; }

    public bool HasTag => !string.IsNullOrEmpty(Tag);

    /// <summary>所属加载器（forge / fabric / neoforge）。</summary>
    public string LoaderKey { get; }

    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
}
