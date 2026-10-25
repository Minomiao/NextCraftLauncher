using System.Security.Cryptography;
using System.Text;

namespace NextCraftLauncher.Core.Launch;

public static class OfflineAuth
{
    /// <summary>
    /// 与 Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:" + name) 保持一致。
    /// </summary>
    public static string CreateOfflineUuid(string playerName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));

        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
