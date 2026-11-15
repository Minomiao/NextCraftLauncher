using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NextCraftLauncher.ViewModels;

/// <summary>外观页的一个主题色选项。</summary>
public sealed partial class AccentOptionViewModel : ObservableObject
{
    public AccentOptionViewModel(
        string name,
        Color color,
        Action<AccentOptionViewModel> select,
        bool isSystem = false)
    {
        Name = name;
        Color = color;
        IsSystem = isSystem;
        Brush = new SolidColorBrush(color);
        SelectCommand = new RelayCommand(() => select(this));
    }

    /// <summary>用于悬停提示。</summary>
    public string Name { get; }

    public Color Color { get; }

    /// <summary>跟随系统强调色；选中时不会写入固定的颜色值。</summary>
    public bool IsSystem { get; }

    public IBrush Brush { get; }

    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;
}
