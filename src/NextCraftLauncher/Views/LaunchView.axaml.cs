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

    /// <summary>按下时高亮先缩小，松手再放大回原尺寸。</summary>
    private static readonly Transitions SquashIn = new()
    {
        new DoubleTransition
        {
            Property = ScaleTransform.ScaleXProperty,
            Duration = TimeSpan.FromMilliseconds(90),
            Easing = new CubicEaseOut(),
        },
        new DoubleTransition
        {
            Property = ScaleTransform.ScaleYProperty,
            Duration = TimeSpan.FromMilliseconds(90),
            Easing = new CubicEaseOut(),
        },
    };

    private static readonly Transitions SquashOut = new()
    {
        new DoubleTransition
        {
            Property = ScaleTransform.ScaleXProperty,
            Duration = TimeSpan.FromMilliseconds(170),
            Easing = new CubicEaseOut(),
        },
        new DoubleTransition
        {
            Property = ScaleTransform.ScaleYProperty,
            Duration = TimeSpan.FromMilliseconds(170),
            Easing = new CubicEaseOut(),
        },
    };

    private const double SquashScale = 0.9;

    private readonly TranslateTransform _pillOffset = new();
    private readonly ScaleTransform _pillScale = new();
    private MainViewModel? _viewModel;
    private Rect? _pillTarget;
    private bool _pillAnimated;
    private bool _dragging;

    /// <summary>按下时缓存下来的按钮位置；拖动过程中不变，避免每次移动都重新换算。</summary>
    private Rect[]? _dragRects;

    /// <summary>待处理的最新指针横坐标，每帧只应用一次。</summary>
    private double? _pendingX;
    private bool _updateQueued;

    public LaunchView()
    {
        InitializeComponent();

        var transform = new TransformGroup();
        transform.Children.Add(_pillOffset);
        transform.Children.Add(_pillScale);

        AccountPill.RenderTransform = transform;
        AccountPill.RenderTransformOrigin = RelativePoint.Center;

        // 账号切换的拖动与顶部导航一致：按在任意一侧即可拖过去。
        AccountSwitch.AddHandler(PointerPressedEvent, OnAccountPointerPressed, RoutingStrategies.Tunnel);
        AccountSwitch.AddHandler(PointerMovedEvent, OnAccountPointerMoved, RoutingStrategies.Tunnel);
        AccountSwitch.AddHandler(PointerReleasedEvent, OnAccountPointerReleased, RoutingStrategies.Tunnel);
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
            if (!_dragging) SnapAccountPill(animate: true);
        }
    }

    private void OnAccountPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!TryGetAccountRects(out var rects)) return;

        _dragging = true;
        _dragRects = rects;
        SetAnimated(false);
        SetSquash(SquashScale, SquashIn);

        FollowAccountPointer(e.GetPosition(AccountSwitch).X);
    }

    private void OnAccountPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        FollowAccountPointer(e.GetPosition(AccountSwitch).X);
    }

    private void OnAccountPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;

        var rects = _dragRects;

        _dragging = false;
        _dragRects = null;
        _pendingX = null;
        SetSquash(1, SquashOut);

        // 与导航栏一样，松手时按指针落点决定切到哪一侧。
        if (rects is not null)
        {
            if (AccountIndexAt(e.GetPosition(AccountSwitch).X, rects) == 0)
                _viewModel?.UseMicrosoftAccountCommand.Execute(null);
            else
                _viewModel?.UseOfflineAccountCommand.Execute(null);
        }

        // 点击命令在松手之后才执行，等它把账号类型改完再贴高亮。
        _pillTarget = null;
        Dispatcher.UIThread.Post(() => SnapAccountPill(animate: true), DispatcherPriority.Background);
    }

    /// <summary>指针事件可能远高于刷新率，每帧只处理最后一次（同导航栏）。</summary>
    private void FollowAccountPointer(double x)
    {
        if (_dragRects is null) return;

        _pendingX = x;
        if (_updateQueued) return;

        _updateQueued = true;
        Dispatcher.UIThread.Post(FlushAccountPill, DispatcherPriority.Render);
    }

    private void FlushAccountPill()
    {
        _updateQueued = false;

        var x = _pendingX;
        _pendingX = null;

        if (x is not { } value || !_dragging || _dragRects is not { } rects) return;

        ApplyPill(PillRect(value, rects));
    }

    /// <summary>开关高亮的过渡；状态未变时不重复赋值。</summary>
    private void SetAnimated(bool animate)
    {
        if (_pillAnimated == animate) return;

        _pillAnimated = animate;
        AccountPill.Transitions = animate ? PillResize : null;
        _pillOffset.Transitions = animate ? PillSlide : null;
    }

    private void SetSquash(double value, Transitions transitions)
    {
        _pillScale.Transitions = transitions;
        _pillScale.ScaleX = value;
        _pillScale.ScaleY = value;
    }

    /// <summary>把高亮贴到当前账号类型对应的按钮上。</summary>
    private void SnapAccountPill(bool animate)
    {
        if (_viewModel is null || _dragging) return;
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

    /// <summary>纯赋值，不碰过渡设置，拖动时每帧只走这里。</summary>
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

    /// <summary>指针落在哪个按钮上；按两个按钮的中点划分。</summary>
    private static int AccountIndexAt(double x, Rect[] rects)
        => x < (rects[0].Right + rects[1].X) / 2 ? 0 : 1;

    /// <summary>高亮以指针为中心、夹在两端之间，与导航栏的高亮算法一致。</summary>
    private static Rect PillRect(double x, Rect[] rects)
    {
        var left = rects[0].X;
        var right = rects[^1].Right;
        var pointer = Math.Clamp(x, left, right);

        var width = rects[pointer > rects[0].Center.X ? 1 : 0].Width;
        var start = Math.Clamp(pointer - width / 2, left, right - width);

        return new Rect(start, rects[0].Y, width, rects[0].Height);
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
