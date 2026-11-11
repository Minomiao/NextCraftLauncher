using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NextCraftLauncher.Core.Auth;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;
using NextCraftLauncher.ViewModels;

namespace NextCraftLauncher.Views;

public partial class MainWindow : Window
{
    /// <summary>错误卡片打开时，背景模糊从无到有地出现。</summary>
    private static readonly Animation BackdropBlurAnimation = new()
    {
        Duration = TimeSpan.FromMilliseconds(450),
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0d),
                Setters = { new Setter(BlurEffect.RadiusProperty, 0d) },
            },
            new KeyFrame
            {
                Cue = new Cue(1d),
                Setters = { new Setter(BlurEffect.RadiusProperty, 12d) },
            },
        },
    };

    private MainViewModel? _viewModel;

    /// <summary>指示器的横向位移，与 <see cref="NavIndicator"/> 的宽度一起构成完整的滑动动画。</summary>
    private readonly TranslateTransform _navIndicatorOffset = new();

    /// <summary>指示器的缩放，用于按下时的“抬起”手感。</summary>
    private readonly ScaleTransform _navIndicatorScale = new();

    /// <summary>按下指示器抬起到的倍数。</summary>
    private const double NavIndicatorLiftScale = 1.12;

    /// <summary>指示器从当前页滑到目标页时的过渡。</summary>
    private static readonly Transitions NavIndicatorSizeTransition = new()
    {
        new DoubleTransition
        {
            Property = Layoutable.WidthProperty,
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new CubicEaseOut(),
        },
    };

    private static readonly Transitions NavIndicatorSlideTransition = new()
    {
        new DoubleTransition
        {
            Property = TranslateTransform.XProperty,
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new CubicEaseOut(),
        },
    };

    private static readonly Transitions NavIndicatorLiftTransition = new()
    {
        new DoubleTransition
        {
            Property = ScaleTransform.ScaleXProperty,
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut(),
        },
        new DoubleTransition
        {
            Property = ScaleTransform.ScaleYProperty,
            Duration = TimeSpan.FromMilliseconds(160),
            Easing = new CubicEaseOut(),
        },
    };

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ReportMetrics();

        // 导航按钮所在区域支持左右拖拽切页（按下后再横向拖）。
        var transform = new TransformGroup();
        transform.Children.Add(_navIndicatorOffset);
        transform.Children.Add(_navIndicatorScale);
        NavIndicator.RenderTransform = transform;
        _navIndicatorScale.Transitions = NavIndicatorLiftTransition;

        NavBar.AddHandler(PointerPressedEvent, OnNavPointerPressed, RoutingStrategies.Tunnel);
        NavBar.AddHandler(PointerMovedEvent, OnNavPointerMoved, RoutingStrategies.Tunnel);
        NavBar.AddHandler(PointerReleasedEvent, OnNavPointerReleased, RoutingStrategies.Tunnel);
        NavBar.LayoutUpdated += OnNavBarLayoutUpdated;
    }

    private bool _navDragging;
    private bool _navPageChanged;

    /// <summary>导航按钮顺序，与向左到右的页面顺序一致。</summary>
    private static readonly AppPage[] NavPages = { AppPage.Launch, AppPage.Download, AppPage.Settings };

    /// <summary>指示器上一次被设置到的位置；布局变化时用来判断是否需要重新贴齐。</summary>
    private Rect? _navIndicatorTarget;

    private void OnNavPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(NavBar);
        _navDragging = true;
        _navPageChanged = false;

        // 按下先把高亮“抬起”并对齐到指针所在的按钮，之后的移动才连续跟随。
        LiftNavIndicator();
        PressNavPointer(position);
    }

    private void OnNavPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_navDragging) return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndNavDrag();
            return;
        }

        FollowNavPointer(e.GetPosition(NavBar));
    }

    private void OnNavPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasPageChanged = _navPageChanged;
        EndNavDrag();

        if (!wasPageChanged) return;

        // 拖拽已经切过页了，吞掉这次释放，避免按钮再触发一次点击把页切回去。
        e.Handled = true;
    }

    private void EndNavDrag()
    {
        if (!_navDragging) return;

        _navDragging = false;
        _navPageChanged = false;

        SettleNavIndicator();
        SnapNavIndicator(animate: true);
    }

    /// <summary>按下时把高亮对齐到指针所在的按钮。</summary>
    private void PressNavPointer(Point position)
    {
        if (_viewModel is null || !TryGetNavRects(out var rects)) return;

        var index = NavPageIndex(position.X, rects);
        if (NavPages[index] != _viewModel.CurrentPage) SwitchPage(NavPages[index]);

        SetNavIndicator(rects[index], animate: false);
    }

    /// <summary>拖动中：高亮以指针为中心连续移动，页面跟着切到指针所在的按钮。</summary>
    private void FollowNavPointer(Point position)
    {
        if (_viewModel is null || !TryGetNavRects(out var rects)) return;

        var index = NavPageIndex(position.X, rects);
        if (NavPages[index] != _viewModel.CurrentPage) SwitchPage(NavPages[index]);

        SetNavIndicator(NavIndicatorRect(position.X, rects), animate: false);
    }

    /// <summary>指针落在哪个按钮的区间内；按按钮之间空隙的中点划分，越界时取两端。</summary>
    private static int NavPageIndex(double x, Rect[] rects)
    {
        for (var i = 0; i < rects.Length - 1; i++)
        {
            if (x < (rects[i].Right + rects[i + 1].X) / 2) return i;
        }

        return rects.Length - 1;
    }

    /// <summary>
    /// 高亮以指针为中心，宽度在相邻按钮之间平滑过渡；指针移出按钮区域后停在边界上不再跟随。
    /// </summary>
    private static Rect NavIndicatorRect(double x, Rect[] rects)
    {
        var left = rects[0].X;
        var right = rects[^1].Right;
        var pointer = Math.Clamp(x, left, right);

        var index = 0;
        while (index < rects.Length - 2 && pointer > rects[index + 1].Center.X) index++;

        var width = rects[index].Width;
        if (index < rects.Length - 1 && pointer > rects[index].Center.X)
        {
            var span = rects[index + 1].Center.X - rects[index].Center.X;
            var t = Math.Clamp((pointer - rects[index].Center.X) / span, 0d, 1d);
            width += (rects[index + 1].Width - rects[index].Width) * t;
        }

        var start = Math.Clamp(pointer - width / 2, left, right - width);
        return new Rect(start, rects[0].Y, width, rects[0].Height);
    }

    /// <summary>按下时高亮轻轻抬起，提示可以拖拽。</summary>
    private void LiftNavIndicator()
    {
        _navIndicatorScale.ScaleX = NavIndicatorLiftScale;
        _navIndicatorScale.ScaleY = NavIndicatorLiftScale;
    }

    private void SettleNavIndicator()
    {
        _navIndicatorScale.ScaleX = 1;
        _navIndicatorScale.ScaleY = 1;
    }

    private void SetNavIndicator(Rect rect, bool animate)
    {
        _navIndicatorTarget = rect;

        NavIndicator.Transitions = animate ? NavIndicatorSizeTransition : null;
        _navIndicatorOffset.Transitions = animate ? NavIndicatorSlideTransition : null;

        _navIndicatorOffset.X = rect.X;
        NavIndicator.Width = rect.Width;

        if (animate) return;

        // 直接落位后把过渡恢复，之后的页面切换仍然带动画。
        NavIndicator.Transitions = NavIndicatorSizeTransition;
        _navIndicatorOffset.Transitions = NavIndicatorSlideTransition;
    }

    private void SnapNavIndicator(bool animate)
    {
        if (_viewModel is null) return;
        if (!TryGetNavRect(_viewModel.CurrentPage, out var rect)) return;

        SetNavIndicator(rect, animate);
    }

    /// <summary>取某个页面按钮相对导航区域的位置；布局还没完成时返回 false。</summary>
    private bool TryGetNavRect(AppPage page, out Rect rect)
    {
        var button = page switch
        {
            AppPage.Download => NavDownloadButton,
            AppPage.Settings => NavSettingsButton,
            _ => NavLaunchButton,
        };

        rect = default;
        if (button.Bounds.Width <= 0) return false;

        var origin = button.TranslatePoint(default, NavBar);
        if (origin is null) return false;

        rect = new Rect(origin.Value, button.Bounds.Size);
        return true;
    }

    private bool TryGetNavRects(out Rect[] rects)
    {
        rects = new Rect[3];
        var pages = new[] { AppPage.Launch, AppPage.Download, AppPage.Settings };

        for (var i = 0; i < pages.Length; i++)
        {
            if (!TryGetNavRect(pages[i], out rects[i])) return false;
        }

        return true;
    }

    /// <summary>窗口首次布局完成、或按钮位置发生变化时，把指示器直接贴到当前页。</summary>
    private void OnNavBarLayoutUpdated(object? sender, EventArgs e)
    {
        if (_viewModel is null || _navDragging) return;
        if (!TryGetNavRect(_viewModel.CurrentPage, out var rect)) return;
        if (_navIndicatorTarget == rect) return;

        SetNavIndicator(rect, animate: false);
    }

    private void SwitchPage(AppPage page)
    {
        if (_viewModel is null) return;

        switch (page)
        {
            case AppPage.Launch:
                _viewModel.ShowLaunchPageCommand.Execute(null);
                break;

            case AppPage.Download:
                _viewModel.ShowDownloadPageCommand.Execute(null);
                break;

            case AppPage.Settings:
                _viewModel.ShowSettingsPageCommand.Execute(null);
                break;
        }

        _navPageChanged = true;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.GameStarted -= OnGameStarted;
            _viewModel.GameExited -= OnGameExited;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.MicrosoftSignInHandler = null;
        }

        _viewModel = DataContext as MainViewModel;
        _navIndicatorTarget = null;

        if (_viewModel is not null)
        {
            _viewModel.GameStarted += OnGameStarted;
            _viewModel.GameExited += OnGameExited;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.MicrosoftSignInHandler = ShowMicrosoftSignInAsync;
            ReportMetrics();
        }
    }

    /// <summary>任意提示卡片打开时让背景模糊逐渐出现，关闭时直接移除。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            // 拖拽中的指示器由指针驱动，不要在此处抢回。
            if (!_navDragging) SnapNavIndicator(animate: true);
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.IsMessageCardOpen)) return;

        if (_viewModel?.IsMessageCardOpen == true)
        {
            var effect = new BlurEffect { Radius = 0 };
            ContentRoot.Effect = effect;
            _ = BackdropBlurAnimation.RunAsync(effect);
        }
        else
        {
            ContentRoot.Effect = null;
        }
    }

    /// <summary>打开应用内的 Microsoft 登录窗口；用户关闭窗口时返回被取消。</summary>
    private async Task<MicrosoftSignInAttempt> ShowMicrosoftSignInAsync(CancellationToken cancellationToken)
    {
        var window = new MicrosoftSignInWindow();

        try
        {
            using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
            {
                if (window.IsVisible) window.Close(null);
            }));

            var tokens = await window.ShowDialog<LiveTokens?>(this);
            return new MicrosoftSignInAttempt(!window.RuntimeMissing, tokens);
        }
        catch (Exception exception)
        {
            LauncherLog.Error("显示 Microsoft 登录窗口失败", exception);
            return new MicrosoftSignInAttempt(false, null);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ReportMetrics();
    }

    /// <summary>把窗口尺寸与主屏工作区同步给启动设置（游戏窗口尺寸用物理像素）。</summary>
    private void ReportMetrics()
    {
        if (_viewModel is null) return;

        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        var screen = Screens.Primary?.WorkingArea ?? default;

        _viewModel.UpdateLauncherWindowMetrics(
            Bounds.Width * scaling,
            Bounds.Height * scaling,
            screen.Width,
            screen.Height);
    }

    private void OnGameStarted(object? sender, LauncherVisibilityAfterLaunch visibility)
    {
        switch (visibility)
        {
            case LauncherVisibilityAfterLaunch.Minimize:
                WindowState = WindowState.Minimized;
                break;

            case LauncherVisibilityAfterLaunch.Hide:
                Hide();
                break;
        }
    }

    private void OnGameExited(object? sender, EventArgs e)
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>打开当天的日志文件；文件不存在时打开日志目录。</summary>
    private void OpenLogFile()
    {
        try
        {
            var path = LauncherLog.CurrentFilePath;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (Directory.Exists(LauncherLog.DirectoryPath))
                Process.Start(new ProcessStartInfo(LauncherLog.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            LauncherLog.Error("打开日志文件失败", exception);
        }
    }

    /// <summary>导出包含异常全文与最近日志的错误报告，方便发给他人排查。</summary>
    private async Task ExportErrorReportAsync()
    {
        if (DataContext is not MainViewModel viewModel) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出错误报告",
                SuggestedFileName = $"NCL-error-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("文本文件") { Patterns = new[] { "*.txt" } },
                },
            });

            if (file is null) return;

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(viewModel.BuildErrorReport());

            LauncherLog.Info($"错误报告已导出：{file.Path.LocalPath}");
        }
        catch (Exception exception)
        {
            LauncherLog.Error("导出错误报告失败", exception);
        }
    }

    private void OnErrorDialogClose(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.CloseDownloadErrorDialogCommand.Execute(null);
    }

    private void OnLaunchNoticeClose(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.CloseLaunchNoticeCommand.Execute(null);
    }

    private void OnErrorDialogOpenLog(object? sender, EventArgs e) => OpenLogFile();

    private void OnErrorDialogExport(object? sender, EventArgs e) => _ = ExportErrorReportAsync();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 导航按钮等交互控件自行处理点击，不触发窗口拖动。
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        BeginMoveDrag(e);
    }
}
