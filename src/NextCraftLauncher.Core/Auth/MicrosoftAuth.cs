using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NextCraftLauncher.Core.Auth;

/// <summary>设备码登录第一步返回的信息。</summary>
public sealed record DeviceCodeInfo(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int IntervalSeconds,
    int ExpiresInSeconds);

/// <summary>Live Connect 的令牌。</summary>
public sealed record LiveTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Minecraft 账户档案。</summary>
public sealed record MinecraftProfile(string Uuid, string Name);

/// <summary>完成一次正版登录后需要保存的内容。</summary>
public sealed record MinecraftLoginResult(
    string RefreshToken,
    string MinecraftAccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    MinecraftProfile Profile,
    string? Xuid,
    string UserHash);

/// <summary>
/// Microsoft 账户（正版）登录。
/// <para/>
/// 使用 Xbox Live 公共客户端的设备码流程：用户在 <c>microsoft.com/link</c>
/// 输入代码授权后，依次换取 Xbox Live、XSTS 与 Minecraft 的访问令牌。
/// 只在用户主动登录或令牌过期时联网。
/// </summary>
public static class MicrosoftAuth
{
    /// <summary>Xbox Live 公共客户端 ID（沿用官方的公开应用标识）。</summary>
    public const string ClientId = "00000000402b5328";

    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";
    private const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    private static readonly HttpClient Http = CreateClient();

    // ------------------------------------------------------------ 设备码

    public static async Task<DeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var document = await PostFormAsync(
            DeviceCodeUrl,
            new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope,
                ["response_type"] = "device_code",
            },
            cancellationToken);

        var root = document.RootElement;
        ThrowIfError(root);

        var deviceCode = Text(root, "device_code") ?? throw new InvalidOperationException("登录服务没有返回设备码。");
        var userCode = Text(root, "user_code") ?? throw new InvalidOperationException("登录服务没有返回验证码。");
        var verificationUri = Text(root, "verification_uri") ?? "https://www.microsoft.com/link";

        return new DeviceCodeInfo(
            deviceCode,
            userCode,
            verificationUri,
            (int)Number(root, "interval", 5),
            (int)Number(root, "expires_in", 900));
    }

    /// <summary>轮询一次；仍在等待用户授权时返回 <c>null</c>。</summary>
    public static async Task<LiveTokens?> PollDeviceCodeAsync(string deviceCode, CancellationToken cancellationToken)
    {
        using var document = await PostFormAsync(
            TokenUrl,
            new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode,
            },
            cancellationToken);

        var root = document.RootElement;
        var error = Text(root, "error");

        if (error is "authorization_pending" or "slow_down") return null;
        ThrowIfError(root);

        return ReadTokens(root);
    }

    public static async Task<LiveTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var document = await PostFormAsync(
            TokenUrl,
            new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope,
            },
            cancellationToken);

        var root = document.RootElement;
        ThrowIfError(root);
        return ReadTokens(root);
    }

    // ------------------------------------------------------------ Xbox / Minecraft

    public static async Task<(string Token, string UserHash, string? Xuid)> AuthenticateXboxAsync(
        string liveAccessToken,
        CancellationToken cancellationToken)
    {
        using var document = await PostJsonAsync(
            "https://user.auth.xboxlive.com/user/authenticate",
            new
            {
                Properties = new
                {
                    AuthMethod = "RPS",
                    SiteName = "user.auth.xboxlive.com",
                    RpsTicket = "d=" + liveAccessToken,
                },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT",
            },
            cancellationToken);

        var root = document.RootElement;
        var token = Text(root, "Token") ?? throw new InvalidOperationException("Xbox Live 授权失败。");
        var (userHash, xuid) = ReadXui(root);
        return (token, userHash, xuid);
    }

    public static async Task<(string Token, string UserHash, string? Xuid)> AuthorizeXstsAsync(
        string xboxToken,
        CancellationToken cancellationToken)
    {
        using var document = await PostJsonAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize",
            new
            {
                Properties = new
                {
                    SandboxId = "RETAIL",
                    UserTokens = new[] { xboxToken },
                },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT",
            },
            cancellationToken);

        var root = document.RootElement;
        if (root.TryGetProperty("XErr", out var xerrElement))
        {
            var xerr = xerrElement.ValueKind == JsonValueKind.Number ? xerrElement.GetInt64() : 0;
            throw new InvalidOperationException(xerr switch
            {
                2148916233 => "该 Microsoft 账户还没有 Xbox 档案，请先在 xbox.com 创建。",
                2148916235 => "所在地区不支持 Xbox Live。",
                2148916238 => "未成年账户需要家长同意后才能使用。",
                _ => $"Xbox 授权失败（XErr {xerr}）。",
            });
        }

        var token = Text(root, "Token") ?? throw new InvalidOperationException("XSTS 授权失败。");
        var (userHash, xuid) = ReadXui(root);
        return (token, userHash, xuid);
    }

    public static async Task<(string AccessToken, DateTimeOffset ExpiresAt)> LoginMinecraftAsync(
        string userHash,
        string xstsToken,
        CancellationToken cancellationToken)
    {
        using var document = await PostJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" },
            cancellationToken);

        var root = document.RootElement;
        var token = Text(root, "access_token") ?? throw new InvalidOperationException("Minecraft 登录失败。");
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Number(root, "expires_in", 86400));
        return (token, expiresAt);
    }

    /// <summary>读取游戏档案；返回 <c>null</c> 表示该账户没有购买 Minecraft。</summary>
    public static async Task<MinecraftProfile?> GetProfileAsync(
        string minecraftAccessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftAccessToken);

        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var id = Text(root, "id");
        var name = Text(root, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Minecraft 档案格式不正确。");

        return new MinecraftProfile(id, name);
    }

    /// <summary>用 Live 令牌走完 Xbox → XSTS → Minecraft，返回可直接保存的登录结果。</summary>
    public static async Task<MinecraftLoginResult> FinishLoginAsync(
        string liveAccessToken,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        var (xboxToken, _, _) = await AuthenticateXboxAsync(liveAccessToken, cancellationToken);
        var (xstsToken, userHash, xuid) = await AuthorizeXstsAsync(xboxToken, cancellationToken);
        var (minecraftToken, expiresAt) = await LoginMinecraftAsync(userHash, xstsToken, cancellationToken);

        var profile = await GetProfileAsync(minecraftToken, cancellationToken)
                      ?? throw new InvalidOperationException("该账户没有 Minecraft: Java Edition 的购买记录。");

        return new MinecraftLoginResult(refreshToken, minecraftToken, expiresAt, profile, xuid, userHash);
    }

    // ------------------------------------------------------------ 工具

    private static LiveTokens ReadTokens(JsonElement root)
    {
        var access = Text(root, "access_token") ?? throw new InvalidOperationException("登录服务没有返回访问令牌。");
        var refresh = Text(root, "refresh_token") ?? string.Empty;
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Number(root, "expires_in", 3600));
        return new LiveTokens(access, refresh, expiresAt);
    }

    private static (string UserHash, string? Xuid) ReadXui(JsonElement root)
    {
        if (root.TryGetProperty("DisplayClaims", out var claims) &&
            claims.TryGetProperty("xui", out var xui) &&
            xui.ValueKind == JsonValueKind.Array &&
            xui.GetArrayLength() > 0)
        {
            var first = xui[0];
            var userHash = Text(first, "uhs") ?? string.Empty;
            var xuid = Text(first, "xuid");
            return (userHash, xuid);
        }

        return (string.Empty, null);
    }

    private static void ThrowIfError(JsonElement root)
    {
        var error = Text(root, "error");
        if (string.IsNullOrEmpty(error)) return;

        var description = Text(root, "error_description");
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(description)
            ? $"登录失败（{error}）。"
            : description);
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double Number(JsonElement element, string name, double fallback)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;

    private static async Task<JsonDocument> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await Http.PostAsync(url, content, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonDocument> PostJsonAsync(
        string url,
        object body,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, cancellationToken);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        // 错误响应也带 JSON 正文，交给调用方根据 error / XErr 给出具体提示。
        if (!response.IsSuccessStatusCode &&
            !document.RootElement.TryGetProperty("error", out _) &&
            !document.RootElement.TryGetProperty("XErr", out _))
        {
            document.Dispose();
            response.EnsureSuccessStatusCode();
        }

        return document;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "NextCraftLauncher/0.1.0 (https://github.com/Minomiao/NextCraftLauncher)");
        return client;
    }
}
