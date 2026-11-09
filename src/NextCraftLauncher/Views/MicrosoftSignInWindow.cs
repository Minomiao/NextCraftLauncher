using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using NextCraftLauncher.Core.Auth;
using NextCraftLauncher.Core.Logging;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Views;

/// <summary>
/// 应用内的 Microsoft 登录窗口：用 WebView2 载入官方登录页，拦截跳转地址取回令牌，
/// 不跳转系统浏览器。仅在用户点击「登录 / 切换账号」时创建。
/// </summary>
public sealed class MicrosoftSignInWindow : Window
{
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private bool _finished;

    public MicrosoftSignInWindow()
    {
        Title = "登录 Microsoft 账户";
        Width = 520;
        Height = 720;
        MinWidth = 420;
        MinHeight = 560;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        Opened += async (_, _) => await InitializeAsync();
        SizeChanged += (_, _) => ApplyBounds();
        ScalingChanged += (_, _) => ApplyBounds();
        Closed += (_, _) => Release();
    }

    /// <summary>缺少 WebView2 运行时时为 <c>true</c>，调用方可以退回设备码登录。</summary>
    public bool RuntimeMissing { get; private set; }

    private async Task InitializeAsync()
    {
        try
        {
            var handle = TryGetPlatformHandle();
            if (handle is null) throw new InvalidOperationException("无法取得登录窗口的句柄。");

            var userDataFolder = Path.Combine(AppSettings.DataDirectory, "cache", "microsoft-login");
            Directory.CreateDirectory(userDataFolder);

            _environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            _controller = await _environment.CreateCoreWebView2ControllerAsync(handle.Handle);
            ApplyBounds();

            var core = _controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            core.NavigationStarting += (_, args) => Inspect(args.Uri);
            core.SourceChanged += (_, _) => Inspect(core.Source);
            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess) LauncherLog.Info("Microsoft 登录页已加载。");
                else LauncherLog.Warn($"Microsoft 登录页加载失败：{args.WebErrorStatus}");
            };

            _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            core.Navigate(MicrosoftAuth.CreateAuthorizeUrl());
            LauncherLog.Info($"已打开 Microsoft 登录窗口（WebView2 {_environment.BrowserVersionString}）。");
        }
        catch (WebView2RuntimeNotFoundException exception)
        {
            RuntimeMissing = true;
            LauncherLog.Warn("未安装 WebView2 运行时，改用设备码登录：" + exception.Message);
            Finish(null);
        }
        catch (Exception exception)
        {
            LauncherLog.Error("打开 Microsoft 登录窗口失败", exception);
            Finish(null);
        }
    }

    /// <summary>跳转地址是登录回调时取出令牌；带错误信息时直接结束。</summary>
    private void Inspect(string? url)
    {
        if (_finished || string.IsNullOrEmpty(url)) return;
        if (!url.StartsWith(MicrosoftAuth.RedirectUri, StringComparison.OrdinalIgnoreCase)) return;

        LiveTokens? tokens;
        try
        {
            tokens = MicrosoftAuth.ParseRedirect(new Uri(url));
        }
        catch (Exception exception)
        {
            LauncherLog.Warn($"Microsoft 登录被拒绝：{exception.Message}");
            Finish(null);
            return;
        }

        if (tokens is not null) Finish(tokens);
    }

    /// <summary>
    /// 同步 WebView 的尺寸。WebView2 的 <c>Bounds</c> 与子窗口都按物理像素计算，
    /// 而 Avalonia 的尺寸是 DIP，所以乘上缩放比例，并把光栅化比例一并告诉 WebView2。
    /// </summary>
    private void ApplyBounds()
    {
        if (_controller is null) return;

        var scaling = RenderScaling is > 0 ? RenderScaling : 1;
        var size = ClientSize;

        _controller.Bounds = new Rectangle(
            0,
            0,
            Math.Max(1, (int)Math.Round(size.Width * scaling)),
            Math.Max(1, (int)Math.Round(size.Height * scaling)));
        _controller.RasterizationScale = scaling;
    }

    private void Finish(LiveTokens? tokens)
    {
        if (_finished) return;
        _finished = true;

        Dispatcher.UIThread.Post(() => Close(tokens));
    }

    private void Release()
    {
        if (!_finished) _finished = true;

        try { _controller?.Close(); }
        catch (Exception) { /* 已经释放时忽略 */ }

        _controller = null;
        _environment = null;
    }
}
