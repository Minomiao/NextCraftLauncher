using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCraftLauncher.ViewModels;

namespace NextCraftLauncher.Views;

public partial class LaunchView : UserControl
{
    /// <summary>高亮从一侧滑到另一侧的过渡。</summary>
    private static readonly Transitions PillSlide = new()
    {
        new DoubleTransition
        {
            Property = TranslateTransform.XProperty,
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new CubicEaseOut(),
        },
    };

    private static readonly Transitions PillResize = new()
    {
        new DoubleTransition
        {
            Property = Layoutable.WidthProperty,
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new CubicEaseOut(),
        },
    };

    private readonly TranslateTransform _pillOffset = new();
    private MainViewModel? _viewModel;
    private Rect? _pillTarget;
    private bool _pillAnimated;

    public LaunchView()
    {
        InitializeComponent();

        AccountPill.RenderTransform = _pillOffset;

        // 高亮只跟随选中项，不响应拖动；位置在布局完成与账号类型变化时同步。
        AccountSwitch.LayoutUpdated += (_, _) => SnapAccountPill(animate: false);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as MainViewModel;
        _pillTarget = null;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SnapAccountPill(animate: false);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsMicrosoftAccount) or nameof(MainViewModel.IsOfflineAccount))
        {
            SnapAccountPill(animate: true);
            return;
        }

        // 进入版本设置时，左侧分栏逐条从左边出现。
        if (e.PropertyName == nameof(MainViewModel.IsVersionDetailOpen) &&
            _viewModel?.IsVersionDetailOpen == true)
        {
            Dispatcher.UIThread.Post(
                () => EnterAnimation.Play(VersionSettingsSideBar.Children, -18, 60, 180, 240),
                DispatcherPriority.Background);
        }
    }

    /// <summary>开关高亮的过渡；状态未变时不重复赋值。</summary>
    private void SetAnimated(bool animate)
    {
        if (_pillAnimated == animate) return;

        _pillAnimated = animate;
        AccountPill.Transitions = animate ? PillResize : null;
        _pillOffset.Transitions = animate ? PillSlide : null;
    }

    /// <summary>把高亮贴到当前账号类型对应的按钮上。</summary>
    private void SnapAccountPill(bool animate)
    {
        if (_viewModel is null) return;
        if (!TryGetAccountRects(out var rects)) return;

        var rect = rects[_viewModel.IsMicrosoftAccount ? 0 : 1];
        if (_pillTarget == rect) return;

        SetPill(rect, animate);
    }

    private void SetPill(Rect rect, bool animate)
    {
        _pillTarget = rect;

        SetAnimated(animate);
        ApplyPill(rect);
    }

    /// <summary>纯赋值，不碰过渡设置。</summary>
    private void ApplyPill(Rect rect)
    {
        _pillOffset.X = rect.X;

        // 初始宽度是 NaN（自动），不能用差值比较判断：NaN 与任何数相减都不大于阈值。
        if (!AccountPill.Width.Equals(rect.Width)) AccountPill.Width = rect.Width;
        if (!AccountPill.Height.Equals(rect.Height)) AccountPill.Height = rect.Height;
    }

    private bool TryGetAccountRects(out Rect[] rects)
    {
        rects = new Rect[2];

        foreach (var (button, index) in new[] { (MicrosoftTab, 0), (OfflineTab, 1) })
        {
            if (button.Bounds.Width <= 0) return false;

            var origin = button.TranslatePoint(default, AccountSwitch);
            if (origin is null) return false;

            rects[index] = new Rect(origin.Value, button.Bounds.Size);
        }

        return true;
    }

    /// <summary>在版本列表里点选一个版本后直接回到启动页，轮播会从上一个版本滚过去。</summary>
    private void OnVersionSelectedByPointer(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (e.Source is not Visual source) return;

        // 行内的「设置 / 删除」自己处理点击，不跳回启动页。
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;

        // 点在列表空白处也不跳。
        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null) return;

        if (DataContext is MainViewModel viewModel)
            viewModel.ShowStartSectionCommand.Execute(null);
    }

    private async void OnCopyLoginCodeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || string.IsNullOrEmpty(viewModel.LoginUserCode)) return;

        await clipboard.SetTextAsync(viewModel.LoginUserCode);
    }

    private async void OnOpenLoginLinkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null) return;
        if (!Uri.TryCreate(viewModel.LoginVerificationUri, UriKind.Absolute, out var uri)) return;

        await launcher.LaunchUriAsync(uri);
    }

    private async void OnAddGameFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择游戏文件夹（.minecraft）",
            AllowMultiple = false,
        });

        if (folders.Count == 0) return;

        var path = folders[0].Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            viewModel.AddGameFolder(path);
    }

    private async void OnChoosePclExecutableClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 PCL2 启动器",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PCL2 启动器")
                {
                    Patterns = new[] { "*.exe" },
                },
            },
        });

        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            await viewModel.ImportPcl2FromLocationAsync(path);
    }

    private async void OnChoosePclFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 PCL 文件夹、启动器所在目录或游戏目录",
            AllowMultiple = false,
        });

        if (folders.Count == 0) return;

        var path = folders[0].Path.LocalPath;
        if (!string.IsNullOrEmpty(path))
            await viewModel.ImportPcl2FromLocationAsync(path);
    }
}
