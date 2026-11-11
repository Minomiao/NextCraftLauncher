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

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ReportMetrics();
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
