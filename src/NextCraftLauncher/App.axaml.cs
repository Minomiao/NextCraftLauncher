using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.ViewModels;
using NextCraftLauncher.Views;

namespace NextCraftLauncher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        LauncherLog.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LauncherLog.Error("未处理异常", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LauncherLog.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();

            _ = StartMainWindowAsync(desktop, splash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartMainWindowAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        var viewModel = new MainViewModel();
        desktop.Exit += (_, _) => viewModel.Dispose();

        LauncherLog.Info("启动界面：开始初始化");

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            // 初始化失败也让主界面打开，界面里会显示具体错误。
            LauncherLog.Error("启动初始化失败", exception);
        }

        // 主窗口的 XAML 解析与首次布局需要几百毫秒；放在启动画面的最短停留期内完成，
        // 这样等待时间被利用起来，而不是先等满再解析。
        var buildWatch = System.Diagnostics.Stopwatch.StartNew();
        var window = new MainWindow
        {
            DataContext = viewModel,
        };
        var buildMilliseconds = buildWatch.ElapsedMilliseconds;

        // 保持启动画面至少一小段时间，避免一闪而过。
        var minimum = TimeSpan.FromMilliseconds(600);
        var remaining = minimum - (DateTimeOffset.UtcNow - startedAt);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);

        if (!splash.IsVisible)
        {
            viewModel.Dispose();
            return;
        }

        // 下载页、设置页的首次布局与模板解析需要几百毫秒。趁启动画面还在、窗口还完全透明时先跑一遍，
        // 用户第一次点开这两个页面时就只剩几毫秒。启动画面是静态的，这一小段不会被察觉。
        var warmWatch = System.Diagnostics.Stopwatch.StartNew();
        window.Opacity = 0;
        window.Show();
        window.Activate();
        desktop.MainWindow = window;
        var shownMilliseconds = warmWatch.ElapsedMilliseconds;

        try
        {
            await WarmUpPagesAsync(viewModel);
        }
        catch (Exception exception)
        {
            // 预热失败不影响启动，只是第一次打开这些页面会稍慢。
            LauncherLog.Warn($"预热页面失败：{exception.Message}");
        }
        finally
        {
            window.Opacity = 1;
        }

        splash.Close();

        LauncherLog.Info(
            $"进入主界面（窗口构建 {buildMilliseconds} ms，显示 {buildWatch.ElapsedMilliseconds} ms，" +
            $"窗口 {shownMilliseconds} ms，预热 {warmWatch.ElapsedMilliseconds - shownMilliseconds} ms）");
    }

    /// <summary>
    /// 预热各页面：切过去让它完成一次完整布局，再切回启动页。
    /// 全程通过 Dispatcher 让出，不在调用线程上同步等待。
    /// </summary>
    private static async Task WarmUpPagesAsync(MainViewModel viewModel)
    {
        viewModel.ShowDownloadPageCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        viewModel.ShowSettingsPageCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        viewModel.ShowLaunchPageCommand.Execute(null);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }
}
