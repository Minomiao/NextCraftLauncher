namespace NextCraftLauncher.Core.Auth;

/// <summary>一次内嵌登录的结果。</summary>
/// <param name="Handled">
/// 是否由登录窗口处理完（无论成功、取消还是被拒绝）。
/// 为 <c>false</c> 表示登录窗口打不开，调用方可以退回设备码流程。
/// </param>
/// <param name="Tokens">登录成功时的 Live 令牌；取消或失败时为 <c>null</c>。</param>
public sealed record MicrosoftSignInAttempt(bool Handled, LiveTokens? Tokens);
