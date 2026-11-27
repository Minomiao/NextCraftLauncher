using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Threading;
using NextCraftLauncher.Core.Auth;
using NextCraftLauncher.Core.Java;
using NextCraftLauncher.Core.Launch;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Minecraft;
using NextCraftLauncher.Core.Settings;
using NextCraftLauncher.Download;
using NextCraftLauncher.Download.Java;
using NextCraftLauncher.Download.Loaders;
using NextCraftLauncher.Download.Minecraft;
using NextCraftLauncher.Download.Mods;
using NextCraftLauncher.Icons;
using NextCraftLauncher.Theme;

namespace NextCraftLauncher.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>游戏启动后启动器窗口需要做何处理（由主窗口订阅执行）。</summary>
    public event EventHandler<LauncherVisibilityAfterLaunch>? GameStarted;

    /// <summary>游戏退出后通知主窗口恢复显示。</summary>
    public event EventHandler? GameExited;

    private int _launcherWindowWidth;
    private int _launcherWindowHeight;
    private int _primaryScreenWidth;
    private int _primaryScreenHeight;

    /// <summary>主窗口把自身尺寸与主屏幕工作区同步给启动设置使用。</summary>
    public void UpdateLauncherWindowMetrics(
        double windowWidth,
        double windowHeight,
        double screenWidth,
        double screenHeight)
    {
        _launcherWindowWidth = (int)Math.Round(windowWidth);
        _launcherWindowHeight = (int)Math.Round(windowHeight);
        _primaryScreenWidth = (int)Math.Round(screenWidth);
        _primaryScreenHeight = (int)Math.Round(screenHeight);
    }

    /// <summary>按「窗口大小」设置计算游戏窗口尺寸，并返回是否全屏。</summary>
    private (int Width, int Height, bool Fullscreen) ResolveGameWindow()
    {
        switch (_settings.GameWindowSize)
        {
            case GameWindowSizeMode.Fullscreen:
                return (854, 480, true);

            case GameWindowSizeMode.SameAsLauncher:
                return (
                    _launcherWindowWidth > 0 ? _launcherWindowWidth : 854,
                    _launcherWindowHeight > 0 ? _launcherWindowHeight : 480,
                    false);

            case GameWindowSizeMode.Custom:
                return (_settings.GameWindowWidth, _settings.GameWindowHeight, false);

            case GameWindowSizeMode.Maximized:
                return (
                    _primaryScreenWidth > 0 ? _primaryScreenWidth : 1280,
                    _primaryScreenHeight > 0 ? _primaryScreenHeight : 720,
                    false);

            default:
                return (854, 480, false);
        }
    }
    [RelayCommand]
    private async Task LaunchAsync()
    {
        // 正在扫描版本或已经在启动时，点按钮不应该弹出“无法启动”。
        if (IsBusy || IsLaunching) return;

        if (IsMicrosoftAccount && !HasMicrosoftAccount)
        {
            ShowLaunchNotice("已选择正版账号，但尚未登录：请点击上方的「登录」，或切换到离线账号。");
            return;
        }

        // 刚切换版本时解析可能还没结束，等它完成再用 _resolved，避免启动到上一个版本。
        await _detailsTask;

        if (!CanLaunch || _resolved is null || SelectedJava is null)
        {
            var reason = LaunchWarning;
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = SelectedVersion is null
                    ? "还没有选择版本：请先在「版本」里选择要启动的版本。"
                    : "当前版本暂时无法启动。";
            }

            ShowLaunchNotice(reason);
            return;
        }

        IsLaunching = true;
        Status = "正在启动...";

        var started = false;

        try
        {
            var (width, height, fullscreen) = ResolveGameWindow();

            var playerName = PlayerName;
            string? accessToken = null;
            string? uuid = null;
            string? xuid = null;

            if (IsMicrosoftAccount)
            {
                var session = await EnsureMicrosoftSessionAsync(_lifetime.Token);
                playerName = session.Name;
                accessToken = session.AccessToken;
                uuid = session.Uuid;
                xuid = session.Xuid;
            }

            var options = new LaunchOptions(
                playerName, MemoryMb, IsVersionIsolated, width, height, fullscreen, accessToken, uuid, xuid);
            var plan = LaunchPlanBuilder.Build(_resolved, SelectedJava, options);

            LauncherLog.Info($"启动游戏：{_resolved.Id}（Java {SelectedJava.MajorVersion}）");

            // 游戏输出直接在当前线程写入日志，不占用 UI 线程。
            var gameLog = new InlineProgress<string>(LauncherLog.Game);
            var visibility = _settings.LauncherVisibility;

            var exitCode = await GameLauncher.RunAsync(
                plan,
                gameLog,
                _settings.GameProcessPriority,
                () =>
                {
                    started = true;
                    GameStarted?.Invoke(this, visibility);
                },
                _lifetime.Token);

            Status = exitCode == 0 ? "游戏已退出" : $"游戏异常退出（代码 {exitCode}）";
            LauncherLog.Info($"游戏退出，代码 {exitCode}");
        }
        catch (OperationCanceledException)
        {
            Status = "启动已取消";
            LauncherLog.Warn("启动已取消");
        }
        catch (Exception exception)
        {
            Status = "启动失败";
            LauncherLog.Error("启动失败", exception);
        }
        finally
        {
            IsLaunching = false;

            if (started)
                GameExited?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SetPreferredJavaAsync(string javaPath)
    {
        _settings.JavaPath = javaPath;
        _settings.Save();

        await RefreshJavaAsync();

        var selected = JavaRuntimes.FirstOrDefault(runtime =>
            string.Equals(runtime.Path, javaPath, StringComparison.OrdinalIgnoreCase));

        if (selected is not null) SelectedJava = selected;
    }

    private async Task RefreshJavaAsync()
    {
        var runtimes = await JavaLocator.DiscoverAsync(MinecraftDirectory, _settings.JavaPath, _lifetime.Token);

        JavaRuntimes.Clear();
        foreach (var runtime in runtimes)
            JavaRuntimes.Add(runtime);

        LauncherLog.Info($"Java 发现完成：{JavaRuntimes.Count} 个运行时");

        SelectedJava = JavaLocator.SelectBest(JavaRuntimes, _resolved?.RequiredJavaMajor ?? 8);
        UpdateLaunchWarning();
    }

    private async Task LoadDetailsAsync(VersionItemViewModel? item)
    {
        var revision = ++_detailsRevision;

        if (item is null)
        {
            _resolved = null;
            VersionDetails = "请选择左侧版本。";
            OnPropertyChanged(nameof(IsolationDescription));
            UpdateLaunchWarning();
            return;
        }

        VersionDetails = "正在解析版本...";

        // 切换版本时不清空上一份解析结果：否则启动按钮会先变灰、警告文字会先消失，
        // 解析完再变回来，看起来就像在闪。启动命令会等这次解析完成后再校验。

        try
        {
            var resolved = await Task.Run(
                () => VersionResolver.Resolve(MinecraftDirectory, item.Id, _settings.DefaultIsolation),
                _lifetime.Token);
            if (revision != _detailsRevision) return;

            _resolved = resolved;
            IsVersionIsolated = resolved.Isolation.IsIsolated;
            OnPropertyChanged(nameof(EffectiveGameDirectory));
            OnPropertyChanged(nameof(IsolationDescription));
            VersionDetails = BuildDetailsText(resolved);

            var bestJava = JavaLocator.SelectBest(JavaRuntimes, resolved.RequiredJavaMajor);
            if (bestJava is not null) SelectedJava = bestJava;

            UpdateLaunchWarning();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (revision != _detailsRevision) return;

            _resolved = null;
            VersionDetails = "解析失败: " + exception.Message;
            OnPropertyChanged(nameof(IsolationDescription));
            UpdateLaunchWarning();
        }
    }

    private static string BuildDetailsText(ResolvedVersion version)
    {
        var lines = new List<string>
        {
            $"版本: {version.Id}",
            $"类型: {version.Merged.Type ?? "未知"}",
            $"需要 Java: {version.RequiredJavaMajor}",
            $"依赖库: {version.Libraries.Count} 个",
            $"版本隔离: {version.Isolation.Description}",
            $"缺失文件: {version.MissingFiles.Count} 个",
        };

        if (version.MissingFiles.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("缺失示例:");
            foreach (var file in version.MissingFiles.Take(5))
                lines.Add("  " + file);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void UpdateLaunchWarning()
    {
        if (_resolved is null)
        {
            LaunchWarning = null;
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(CanDownloadJava));
            OnPropertyChanged(nameof(RequiredJavaDescription));
            return;
        }

        if (!_resolved.IsComplete)
        {
            LaunchWarning = $"缺少 {_resolved.MissingFiles.Count} 个必要文件（客户端 jar / 依赖库 / 资源索引），当前无法启动。";
        }
        else if (SelectedJava is null)
        {
            LaunchWarning = $"未找到可用 Java，需要 Java {_resolved.RequiredJavaMajor}+。";
        }
        else if (SelectedJava.MajorVersion < _resolved.RequiredJavaMajor)
        {
            LaunchWarning = $"当前 Java {SelectedJava.MajorVersion} 低于所需的 Java {_resolved.RequiredJavaMajor}。";
        }
        else if (IsMicrosoftAccount && !HasMicrosoftAccount)
        {
            LaunchWarning = "已选择正版账号，但尚未登录：请点击上方的「登录」，或切换到离线账号。";
        }
        else
        {
            LaunchWarning = null;
        }

        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanDownloadJava));
        OnPropertyChanged(nameof(RequiredJavaDescription));
    }
}
