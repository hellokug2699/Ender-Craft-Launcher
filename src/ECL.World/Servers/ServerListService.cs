// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.World.Nbt;

namespace Ecl.World.Servers;

public sealed record ServerEntry(
    string Name,
    string Address,
    string Host,
    int Port,
    byte[]? IconBytes,
    string? RawIcon)
{
    public string DisplayAddress => Port == 25565 ? Host : $"{Host}:{Port}";
}

public sealed record ServerListResult(IReadOnlyList<ServerEntry> Servers, string? Error)
{
    public bool IsReadable => Error is null;
}

/// <summary>
/// 只读读取实例 <c>servers.dat</c>。未知字段保留在原始 NBT 中，当前不写回，
/// 避免破坏 Minecraft 自己维护的列表。
/// </summary>
public sealed class ServerListService(string gameDirectory)
{
    private const int MaxIconBytes = 256 * 1024;

    public string ServersFile { get; } = Path.Combine(gameDirectory, "servers.dat");

    public Task<ServerListResult> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                if (!File.Exists(ServersFile))
                {
                    return new ServerListResult([], null);
                }

                try
                {
                    var root = NbtFile.ReadFile(ServersFile);
                    var list = root.Child("servers");
                    if (list is not { Type: NbtTagType.List })
                    {
                        return new ServerListResult([], "servers.dat 缺少 servers 列表。");
                    }

                    var servers = new List<ServerEntry>();
                    foreach (var entry in list.Children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var address = entry.Child("ip")?.GetString();
                        if (string.IsNullOrWhiteSpace(address) || !ServerAddress.TryParse(address, out var parsed))
                        {
                            continue;
                        }

                        var rawIcon = entry.Child("icon")?.GetString();
                        servers.Add(new ServerEntry(
                            entry.Child("name")?.GetString() ?? parsed.Display,
                            address,
                            parsed.Host,
                            parsed.Port,
                            DecodeIcon(rawIcon),
                            rawIcon));
                    }

                    return new ServerListResult(servers, null);
                }
                catch (Exception exception) when (exception is NbtFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    return new ServerListResult([], $"servers.dat 读取失败：{exception.Message}");
                }
            },
            cancellationToken);

    private static byte[]? DecodeIcon(string? rawIcon)
    {
        if (string.IsNullOrWhiteSpace(rawIcon))
        {
            return null;
        }

        var payload = rawIcon;
        var comma = payload.IndexOf(',', StringComparison.Ordinal);
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            payload = payload[(comma + 1)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length is 0 or > MaxIconBytes || !PngInspector.IsPng(bytes))
            {
                return null;
            }

            return PngInspector.TryReadSize(bytes, out var width, out var height) &&
                   width is > 0 and <= 256 && height is > 0 and <= 256
                ? bytes
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public readonly record struct ServerAddress(string Host, int Port)
{
    public string Display => Port == 25565 ? Host : FormattableString.Invariant($"{Host}:{Port}");

    public static bool TryParse(string value, out ServerAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var end = value.IndexOf(']');
            if (end <= 1)
            {
                return false;
            }

            var host = value[1..end];
            var port = 25565;
            if (end + 1 < value.Length)
            {
                if (value[end + 1] != ':' ||
                    !int.TryParse(value[(end + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
                {
                    return false;
                }
            }

            if (!IsValidPort(port) || !IsValidHost(host))
            {
                return false;
            }

            address = new ServerAddress(host, port);
            return true;
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon)
        {
            var host = value[..colon];
            if (!int.TryParse(value[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
                !IsValidPort(port))
            {
                return false;
            }

            if (!IsValidHost(host))
            {
                return false;
            }

            address = new ServerAddress(host, port);
            return true;
        }

        if (!IsValidHost(value))
        {
            return false;
        }

        address = new ServerAddress(value, 25565);
        return true;
    }

    private static bool IsValidPort(int port) => port is > 0 and <= 65535;

    private static bool IsValidHost(string host) =>
        host.Length is > 0 and <= 253 &&
        host.All(character => !char.IsWhiteSpace(character) && character is not '/' and not '\\');
}

internal static class PngInspector
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Signature.Length && bytes[..Signature.Length].SequenceEqual(Signature);

    public static bool TryReadSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 24 || !IsPng(bytes) ||
            bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
        {
            return false;
        }

        width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width > 0 && height > 0;
    }
}
