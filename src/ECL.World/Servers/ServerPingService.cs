// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ecl.World.Servers;

public enum ServerPingStatus
{
    Online,
    Offline,
    Refused,
    Timeout,
    DnsError,
    ProtocolError,
    InvalidResponse,
}

public sealed record ServerPingResult(
    string Host,
    int Port,
    ServerPingStatus Status,
    string? Motd,
    string? VersionName,
    int? ProtocolVersion,
    int? PlayersOnline,
    int? PlayersMax,
    long? LatencyMilliseconds,
    byte[]? IconBytes,
    string? Error,
    DateTimeOffset QueriedAt)
{
    public bool Online => Status == ServerPingStatus.Online;

    public string StatusText => Status switch
    {
        ServerPingStatus.Online => "在线",
        ServerPingStatus.Offline => "离线",
        ServerPingStatus.Refused => "连接被拒绝",
        ServerPingStatus.Timeout => "连接超时",
        ServerPingStatus.DnsError => "域名解析失败",
        ServerPingStatus.ProtocolError => "协议错误",
        ServerPingStatus.InvalidResponse => "响应无效",
        _ => "未知",
    };
}

/// <summary>
/// Minecraft Java Edition Server List Ping。网络 I/O 全部异步、单服务器超时、
/// 全局并发上限、响应/图标大小限制与短期缓存；服务器返回的文本只作为普通文本展示。
/// </summary>
public sealed partial class ServerPingService : IDisposable
{
    private const int MaxFrameBytes = 2 * 1024 * 1024;
    private const int MaxIconBytes = 256 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, (ServerPingResult Result, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrency;

    public ServerPingService(TimeSpan? cacheTtl = null, TimeSpan? timeout = null, int maxConcurrency = 8)
    {
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5);
        _timeout = timeout ?? DefaultTimeout;
        _concurrency = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 32));
    }

    public async Task<ServerPingResult> QueryAsync(
        ServerEntry entry,
        bool refresh = false,
        CancellationToken cancellationToken = default) =>
        await PingAsync(entry.Host, entry.Port, refresh, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ServerPingResult>> QueryManyAsync(
        IEnumerable<ServerEntry> entries,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var tasks = entries.Select(entry => QueryAsync(entry, refresh, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<ServerPingResult> PingAsync(
        string host,
        int port,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var key = $"{host}:{port}";
        if (!refresh &&
            _cache.TryGetValue(key, out var cached) &&
            cached.Expires > DateTimeOffset.UtcNow)
        {
            return cached.Result;
        }

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await PingCoreAsync(host, port, cancellationToken).ConfigureAwait(false);
            _cache[key] = (result, DateTimeOffset.UtcNow.Add(_cacheTtl));
            if (_cache.Count > 256)
            {
                foreach (var expired in _cache.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Take(64))
                {
                    _cache.TryRemove(expired.Key, out _);
                }
            }

            return result;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void ClearCache() => _cache.Clear();

    public void Dispose() => _concurrency.Dispose();

    private async Task<ServerPingResult> PingCoreAsync(string host, int port, CancellationToken cancellationToken)
    {
        var queriedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var effective = new ServerAddress(host, port);
        if (port == 25565)
        {
            var srv = await SrvResolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            if (srv is { } resolved)
            {
                effective = resolved;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(effective.Host, effective.Port, timeout.Token).ConfigureAwait(false);
            client.NoDelay = true;
            await using var stream = client.GetStream();
            await SendHandshakeAsync(stream, host, port, timeout.Token).ConfigureAwait(false);
            await WritePacketAsync(stream, [0x00], timeout.Token).ConfigureAwait(false);
            var json = await ReadStatusJsonAsync(stream, timeout.Token).ConfigureAwait(false);
            clock.Stop();
            return ParseStatus(json, host, port, clock.ElapsedMilliseconds, queriedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(host, port, ServerPingStatus.Timeout, "连接或读取超时。", queriedAt);
        }
        catch (SocketException exception)
        {
            var status = exception.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => ServerPingStatus.Refused,
                SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => ServerPingStatus.DnsError,
                SocketError.TimedOut => ServerPingStatus.Timeout,
                _ => ServerPingStatus.Offline,
            };
            return Failure(host, port, status, exception.Message, queriedAt);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or FormatException)
        {
            return Failure(host, port, ServerPingStatus.InvalidResponse, exception.Message, queriedAt);
        }
    }

    private ServerPingResult ParseStatus(string json, string host, int port, long latency, DateTimeOffset queriedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var version = root.TryGetProperty("version", out var versionElement) ? versionElement : default;
        var players = root.TryGetProperty("players", out var playersElement) ? playersElement : default;
        var motd = root.TryGetProperty("description", out var description)
            ? FlattenDescription(description)
            : null;
        var icon = root.TryGetProperty("favicon", out var favicon) ? DecodeFavicon(favicon.GetString()) : null;
        return new ServerPingResult(
            host,
            port,
            ServerPingStatus.Online,
            Sanitize(motd),
            version.ValueKind == JsonValueKind.Object && version.TryGetProperty("name", out var name)
                ? Sanitize(name.GetString())
                : null,
            version.ValueKind == JsonValueKind.Object &&
            version.TryGetProperty("protocol", out var protocol) &&
            protocol.TryGetInt32(out var protocolValue)
                ? protocolValue
                : null,
            players.ValueKind == JsonValueKind.Object &&
            players.TryGetProperty("online", out var online) &&
            online.TryGetInt32(out var onlineValue)
                ? onlineValue
                : null,
            players.ValueKind == JsonValueKind.Object &&
            players.TryGetProperty("max", out var max) &&
            max.TryGetInt32(out var maxValue)
                ? maxValue
                : null,
            latency,
            icon,
            null,
            queriedAt);
    }

    private static ServerPingResult Failure(string host, int port, ServerPingStatus status, string error, DateTimeOffset queriedAt) =>
        new(host, port, status, null, null, null, null, null, null, null, error, queriedAt);

    private static async Task SendHandshakeAsync(Stream stream, string host, int port, CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        WriteVarInt(payload, 0);
        WriteVarInt(payload, -1);
        var hostBytes = Encoding.UTF8.GetBytes(host);
        WriteVarInt(payload, hostBytes.Length);
        payload.Write(hostBytes);
        payload.WriteByte((byte)(port >> 8));
        payload.WriteByte((byte)port);
        WriteVarInt(payload, 1);
        await WritePacketAsync(stream, payload.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        using var framed = new MemoryStream();
        WriteVarInt(framed, payload.Length);
        framed.Write(payload);
        await stream.WriteAsync(framed.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadStatusJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = await ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length <= 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"状态响应长度无效：{length}。");
        }

        var frame = new byte[length];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        var offset = 0;
        var packetId = ReadVarInt(frame, ref offset);
        if (packetId != 0)
        {
            throw new InvalidDataException($"状态响应包 ID 不是 0：{packetId}。");
        }

        var jsonLength = ReadVarInt(frame, ref offset);
        if (jsonLength <= 0 || jsonLength > MaxFrameBytes || offset + jsonLength > frame.Length)
        {
            throw new InvalidDataException("状态 JSON 长度无效。");
        }

        return Encoding.UTF8.GetString(frame, offset, jsonLength);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = 0;
        var buffer = new byte[1];
        for (var index = 0; index < 5; index++)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("连接在读取 VarInt 时关闭。");
            }

            value |= (buffer[0] & 0x7F) << (index * 7);
            if ((buffer[0] & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("VarInt 超过 5 字节。");
    }

    private static int ReadVarInt(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = 0;
        for (var index = 0; index < 5; index++)
        {
            if (offset >= data.Length)
            {
                throw new InvalidDataException("VarInt 越界。");
            }

            var current = data[offset++];
            value |= (current & 0x7F) << (index * 7);
            if ((current & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("VarInt 超过 5 字节。");
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            stream.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        stream.WriteByte((byte)remaining);
    }

    private static string? FlattenDescription(JsonElement description)
    {
        if (description.ValueKind == JsonValueKind.String)
        {
            return description.GetString();
        }

        if (description.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var builder = new StringBuilder();
        AppendText(description, builder, 0);
        return builder.ToString();
    }

    private static void AppendText(JsonElement element, StringBuilder builder, int depth)
    {
        if (depth > 8 || builder.Length > 2048)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            builder.Append(element.GetString());
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            builder.Append(text.GetString());
        }
        else if (element.TryGetProperty("translate", out var translate) && translate.ValueKind == JsonValueKind.String)
        {
            builder.Append(translate.GetString());
        }

        if (element.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in extra.EnumerateArray())
            {
                AppendText(child, builder, depth + 1);
            }
        }
    }

    private static byte[]? DecodeFavicon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var comma = value.IndexOf(',', StringComparison.Ordinal);
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            value = value[(comma + 1)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length is > 0 and <= MaxIconBytes && PngInspector.IsPng(bytes) ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var clean = ControlChars().Replace(value, string.Empty);
        return clean.Length > 512 ? clean[..512] : clean;
    }

    [GeneratedRegex("[\u0000-\u0008\u000B\u000C\u000E-\u001F]")]
    private static partial Regex ControlChars();
}
