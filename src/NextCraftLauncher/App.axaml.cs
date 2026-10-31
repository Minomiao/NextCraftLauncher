using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

        window.Show();
        window.Activate();
        desktop.MainWindow = window;
        splash.Close();

        LauncherLog.Info($"进入主界面（窗口构建 {buildMilliseconds} ms，显示 {buildWatch.ElapsedMilliseconds} ms）");
    }
}
