// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;

namespace Ecl.Auth;

/// <summary>
/// 与 Java 的 UUID.nameUUIDFromBytes(("OfflinePlayer:" + name)) 保持一致：
/// MD5、版本号 3、RFC 4122 variant。注意 Guid(byte[]) 使用小端序，因此这里直接拼十六进制。
/// </summary>
public static class OfflineUuid
{
    public static string Create(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexStringLower(hash);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
