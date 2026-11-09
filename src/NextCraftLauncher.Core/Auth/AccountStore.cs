using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NextCraftLauncher.Core.Settings;

namespace NextCraftLauncher.Core.Auth;

/// <summary>已保存的账号信息。正版账号只保存刷新令牌，用它换取新的访问令牌。</summary>
public sealed class StoredAccount
{
    /// <summary><c>offline</c> 或 <c>microsoft</c>。</summary>
    public string Type { get; set; } = "offline";

    public string? Name { get; set; }

    public string? Uuid { get; set; }

    /// <summary>长期有效的刷新令牌；访问令牌过期时用它续期。</summary>
    public string? RefreshToken { get; set; }

    public string? AccessToken { get; set; }

    public DateTimeOffset AccessTokenExpiresAt { get; set; }

    public string? Xuid { get; set; }

    [JsonIgnore]
    public bool IsMicrosoft => string.Equals(Type, "microsoft", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 账号的本地存储：<c>NCL/account.json</c>。
/// 注意：刷新令牌以明文保存，和多数启动器一样依赖本机账户权限保护；
/// 请勿把这个文件分享给他人。
/// </summary>
public static class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string FilePath => Path.Combine(AppSettings.DataDirectory, "account.json");

    public static StoredAccount? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var account = JsonSerializer.Deserialize<StoredAccount>(File.ReadAllBytes(FilePath), JsonOptions);
            return account is null || string.IsNullOrWhiteSpace(account.Type) ? null : account;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(StoredAccount account)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(account, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // 保存失败不影响本次启动，下次仍需重新登录。
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception)
        {
        }
    }
}
