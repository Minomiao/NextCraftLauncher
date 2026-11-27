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
    // ------------------------------------------------------------ 账号（正版登录）

    private void ApplyAccount()
    {
        HasMicrosoftAccount = _account is { IsMicrosoft: true };

        if (HasMicrosoftAccount)
        {
            AccountDisplayName = _account!.Name ?? "正版账号";
            AccountSubtitle = "Microsoft 账户";
        }
        else
        {
            AccountDisplayName = "未登录";
            AccountSubtitle = "点击「登录」使用正版账号";
        }

        OnPropertyChanged(nameof(IsOfflineAccount));
        UpdateLaunchWarning();
    }

    public bool IsOfflineAccount => !IsMicrosoftAccount;

    partial void OnIsMicrosoftAccountChanged(bool value)
    {
        _settings.AccountType = value ? "microsoft" : "offline";
        _settings.Save();

        OnPropertyChanged(nameof(IsOfflineAccount));
        UpdateLaunchWarning();
    }

    [RelayCommand]
    private void UseOfflineAccount() => IsMicrosoftAccount = false;

    [RelayCommand]
    private void UseMicrosoftAccount() => IsMicrosoftAccount = true;

    /// <summary>由视图提供的应用内登录窗口；为 <c>null</c> 时退回设备码登录。</summary>
    public Func<CancellationToken, Task<MicrosoftSignInAttempt>>? MicrosoftSignInHandler { get; set; }

    [RelayCommand]
    private async Task LoginMicrosoftAsync()
    {
        if (IsLoggingIn) return;

        _loginCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loginCts = cts;

        IsLoggingIn = true;
        IsLoginDialogOpen = false;
        LoginUserCode = string.Empty;
        LoginVerificationUri = string.Empty;

        try
        {
            var tokens = await WaitForLiveTokensAsync(cts.Token);
            if (tokens is null) return;

            LoginStatusText = "正在验证 Minecraft 账户…";
            var result = await MicrosoftAuth.FinishLoginAsync(
                tokens.AccessToken, tokens.RefreshToken, cts.Token);

            _account = new StoredAccount
            {
                Type = "microsoft",
                Name = result.Profile.Name,
                Uuid = result.Profile.Uuid,
                RefreshToken = string.IsNullOrEmpty(tokens.RefreshToken) ? null : tokens.RefreshToken,
                AccessToken = result.MinecraftAccessToken,
                AccessTokenExpiresAt = result.AccessTokenExpiresAt,
                Xuid = result.Xuid,
            };

            AccountStore.Save(_account);
            ApplyAccount();
            IsMicrosoftAccount = true;

            IsLoginDialogOpen = false;
            Status = $"已登录正版账号：{result.Profile.Name}";
            LauncherLog.Info($"正版登录成功：{result.Profile.Name}（{result.Profile.Uuid}）");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (IsLoginDialogOpen) LoginStatusText = "登录失败：" + exception.Message;
            Status = "正版登录失败：" + exception.Message;
            LauncherLog.Warn($"正版登录失败：{exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_loginCts, cts)) IsLoggingIn = false;
        }
    }

    /// <summary>优先用应用内登录窗口拿到 Live 令牌；窗口不可用时退回设备码流程。</summary>
    private async Task<LiveTokens?> WaitForLiveTokensAsync(CancellationToken cancellationToken)
    {
        if (MicrosoftSignInHandler is { } signIn)
        {
            Status = "正在打开 Microsoft 登录窗口…";
            var attempt = await signIn(cancellationToken);
            if (attempt.Handled)
            {
                if (attempt.Tokens is null) Status = "已取消正版登录";
                return attempt.Tokens;
            }

            LauncherLog.Warn("无法打开 Microsoft 登录窗口，改用设备码登录。");
        }

        // 没有 WebView2 运行时（或窗口创建失败）时，用手机/浏览器输码登录。
        IsLoginDialogOpen = true;
        LoginStatusText = "正在获取登录代码…";

        var device = await MicrosoftAuth.RequestDeviceCodeAsync(cancellationToken);
        LoginUserCode = device.UserCode;
        LoginVerificationUri = device.VerificationUri;
        LoginStatusText = $"请打开 {device.VerificationUri} 并输入下方代码";

        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresInSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(3, device.IntervalSeconds));

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken);

            var tokens = await MicrosoftAuth.PollDeviceCodeAsync(device.DeviceCode, cancellationToken);
            if (tokens is not null) return tokens;
        }

        LoginStatusText = "登录代码已过期，请重新获取。";
        return null;
    }

    [RelayCommand]
    private void CancelMicrosoftLogin()
    {
        _loginCts?.Cancel();
        IsLoginDialogOpen = false;
        IsLoggingIn = false;
    }

    [RelayCommand]
    private void LogoutMicrosoft()
    {
        _loginCts?.Cancel();
        IsLoginDialogOpen = false;
        IsLoggingIn = false;

        _account = null;
        AccountStore.Clear();
        ApplyAccount();
        IsMicrosoftAccount = false;
        Status = "已退出正版账号，使用离线账号启动";
    }

    /// <summary>启动前取可用的正版令牌；过期时用刷新令牌续期。</summary>
    private async Task<(string Name, string Uuid, string AccessToken, string? Xuid)> EnsureMicrosoftSessionAsync(
        CancellationToken cancellationToken)
    {
        if (_account is not { IsMicrosoft: true })
            throw new InvalidOperationException("尚未登录正版账号。");

        if (!string.IsNullOrEmpty(_account.AccessToken) &&
            !string.IsNullOrEmpty(_account.Uuid) &&
            _account.AccessTokenExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5))
        {
            return (_account.Name ?? "Player", _account.Uuid, _account.AccessToken, _account.Xuid);
        }

        if (string.IsNullOrWhiteSpace(_account.RefreshToken))
            throw new InvalidOperationException("正版登录已过期，请重新登录。");

        Status = "正在刷新正版账号…";
        var tokens = await MicrosoftAuth.RefreshAsync(_account.RefreshToken, cancellationToken);
        var result = await MicrosoftAuth.FinishLoginAsync(tokens.AccessToken, tokens.RefreshToken, cancellationToken);

        _account.RefreshToken = string.IsNullOrEmpty(tokens.RefreshToken) ? _account.RefreshToken : tokens.RefreshToken;
        _account.AccessToken = result.MinecraftAccessToken;
        _account.AccessTokenExpiresAt = result.AccessTokenExpiresAt;
        _account.Name = result.Profile.Name;
        _account.Uuid = result.Profile.Uuid;
        _account.Xuid = result.Xuid;
        _account.Type = "microsoft";
        AccountStore.Save(_account);
        ApplyAccount();

        return (result.Profile.Name, result.Profile.Uuid, result.MinecraftAccessToken, result.Xuid);
    }
}
