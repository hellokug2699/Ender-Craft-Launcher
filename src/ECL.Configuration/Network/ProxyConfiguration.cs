// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Microsoft.Extensions.Logging;

namespace Ecl.Configuration.Network;

/// <summary>
/// 当前实现实测支持的代理协议（settings-network.md §4、D-04）。
///
/// 支持集合由本地代理服务对 .NET <see cref="System.Net.WebProxy"/> 的完整请求链路实测确定：
/// - <c>http</c>：HTTP 代理（含 Basic 认证）可用；
/// - <c>socks5</c>：SOCKS5（含用户名/密码认证）可用；
/// - <c>socks4a</c>：SOCKS4a 可用，但协议本身不支持认证。
///
/// 不提供 <c>https</c> 代理：本轮没有用受信任证书完成 TLS-to-proxy 的端到端验证，
/// 因此不能声称支持。未知协议不得回退到其它协议或系统代理。
/// </summary>
public static class ProxyProtocols
{
    public const string Http = "http";

    public const string Socks5 = "socks5";

    public const string Socks4a = "socks4a";

    public static IReadOnlyList<string> All { get; } = [Http, Socks5, Socks4a];

    public static string Normalize(string? protocol) =>
        (protocol ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsSupported(string? protocol) =>
        All.Contains(Normalize(protocol), StringComparer.Ordinal);

    /// <summary>协议是否支持用户名/密码认证。</summary>
    public static bool SupportsCredentials(string? protocol) =>
        Normalize(protocol) is Http or Socks5;
}

public enum ProxyEndpointError
{
    None,
    UnsupportedProtocol,
    HostRequired,
    HostInvalid,
    PortRequired,
    PortInvalid,
    PortOutOfRange,
}

/// <summary>
/// 手动代理的规范化端点。只保存协议、主机与端口；凭据永远不进入这个类型，
/// 因此它可以安全地出现在日志、错误信息与可序列化状态中。
/// </summary>
public sealed record ProxyEndpoint
{
    private ProxyEndpoint(string protocol, string host, int port)
    {
        Protocol = protocol;
        Host = host;
        Port = port;
    }

    public string Protocol { get; }

    public string Host { get; }

    public int Port { get; }

    public string Authority => Host.Contains(':', StringComparison.Ordinal)
        ? $"[{Host}]:{Port}"
        : $"{Host}:{Port}";

    public Uri ToUri() => new($"{Protocol}://{Authority}", UriKind.Absolute);

    public override string ToString() => $"{Protocol}://{Authority}";

    public static bool TryCreate(
        string? protocol,
        string? host,
        int port,
        out ProxyEndpoint? endpoint,
        out ProxyEndpointError error)
    {
        endpoint = null;
        error = ProxyEndpointError.None;

        var normalizedProtocol = ProxyProtocols.Normalize(protocol);
        if (!ProxyProtocols.IsSupported(normalizedProtocol))
        {
            error = ProxyEndpointError.UnsupportedProtocol;
            return false;
        }

        var normalizedHost = NormalizeHost(host);
        if (normalizedHost.Length == 0)
        {
            error = ProxyEndpointError.HostRequired;
            return false;
        }

        var hostKind = Uri.CheckHostName(normalizedHost);
        if (hostKind == UriHostNameType.Unknown
            || (normalizedHost.Contains(':', StringComparison.Ordinal) && hostKind != UriHostNameType.IPv6))
        {
            error = ProxyEndpointError.HostInvalid;
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = port <= 0 ? ProxyEndpointError.PortRequired : ProxyEndpointError.PortOutOfRange;
            return false;
        }

        endpoint = new ProxyEndpoint(normalizedProtocol, normalizedHost, port);
        return true;
    }

    public static bool TryCreate(
        string? protocol,
        string? host,
        string? portText,
        out ProxyEndpoint? endpoint,
        out ProxyEndpointError error)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(portText))
        {
            error = ProxyEndpointError.PortRequired;
            return false;
        }

        if (!int.TryParse(portText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            error = ProxyEndpointError.PortInvalid;
            return false;
        }

        return TryCreate(protocol, host, port, out endpoint, out error);
    }

    private static string NormalizeHost(string? host)
    {
        var value = (host ?? string.Empty).Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            value = value[1..^1];
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value.Any(char.IsWhiteSpace)
            || value.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
        {
            return "\0invalid";
        }

        return value.ToLowerInvariant();
    }
}

public enum ProxyMode
{
    None,
    System,
    Manual,
}

/// <summary>
/// 一次代理配置解析结果。手动代理无效时 <see cref="Endpoint"/> 为空且 <see cref="ErrorCode"/> 非空；
/// 调用方必须显式失败，不得回退到系统代理（settings-network.md §5）。
/// </summary>
public sealed record ProxySettingsSnapshot(
    ProxyMode Mode,
    ProxyEndpoint? Endpoint,
    string? ErrorCode = null,
    string? ErrorDetail = null)
{
    public bool IsValid => ErrorCode is null;
}

/// <summary>
/// 代理配置的读取、旧地址解析与迁移（D-04）。
/// 新配置由 <c>network.proxyProtocol</c> / <c>network.proxyHost</c> / <c>network.proxyPort</c> 组成；
/// 旧 <c>network.proxyAddress</c> 只作为迁移输入，不再参与新配置写入。
/// </summary>
public static class ProxySettings
{
    public const string ModeKey = "network.proxyMode";

    public const string ProtocolKey = "network.proxyProtocol";

    public const string HostKey = "network.proxyHost";

    public const string PortKey = "network.proxyPort";

    public const string LegacyAddressKey = "network.proxyAddress";

    public const string NoneMode = "none";

    public const string SystemMode = "system";

    public const string ManualMode = "manual";

    public const string InvalidModeCode = "network.proxy.invalid_mode";

    public const string ManualMissingCode = "network.proxy.manual_missing";

    public const string LegacyInvalidCode = "network.proxy.legacy_invalid";

    public static ProxySettingsSnapshot Resolve(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var rawMode = settings.Get<string>(ModeKey);
        var mode = ProxyProtocols.Normalize(rawMode);
        switch (mode)
        {
            case NoneMode:
                return new ProxySettingsSnapshot(ProxyMode.None, null);
            case SystemMode:
                return new ProxySettingsSnapshot(ProxyMode.System, null);
            case ManualMode:
                return ResolveManual(settings);
            default:
                // 未知模式不得当成系统代理：这是显式配置错误。
                return new ProxySettingsSnapshot(
                    ProxyMode.Manual,
                    null,
                    InvalidModeCode,
                    $"“{rawMode}”不是有效的代理模式。");
        }
    }

    private static ProxySettingsSnapshot ResolveManual(ISettingsService settings)
    {
        var hasStructured = settings.GetSource(ProtocolKey) != SettingsValueSource.Default
            || settings.GetSource(HostKey) != SettingsValueSource.Default
            || settings.GetSource(PortKey) != SettingsValueSource.Default;

        if (hasStructured)
        {
            if (!ProxyEndpoint.TryCreate(
                    settings.Get<string>(ProtocolKey),
                    settings.Get<string>(HostKey),
                    settings.GetRawString(PortKey),
                    out var endpoint,
                    out var error))
            {
                return new ProxySettingsSnapshot(
                    ProxyMode.Manual,
                    null,
                    ErrorCode(error),
                    DescribeEndpointError(error));
            }

            return new ProxySettingsSnapshot(ProxyMode.Manual, endpoint);
        }

        var legacy = settings.GetRawString(LegacyAddressKey);
        if (string.IsNullOrWhiteSpace(legacy))
        {
            return new ProxySettingsSnapshot(
                ProxyMode.Manual,
                null,
                ManualMissingCode,
                "手动代理缺少协议、主机与端口。");
        }

        if (!TryParseLegacy(
                legacy,
                out var legacyEndpoint,
                out _,
                out _,
                out var legacyError))
        {
            return new ProxySettingsSnapshot(
                ProxyMode.Manual,
                null,
                LegacyInvalidCode,
                DescribeEndpointError(legacyError));
        }

        return new ProxySettingsSnapshot(ProxyMode.Manual, legacyEndpoint);
    }

    /// <summary>
    /// 解析旧版单一代理地址。只接受带受支持协议前缀的绝对 URI；缺少协议、未知协议、
    /// 带路径/查询/片段或无效端口都视为无法无歧义解析，保留原值并提示修复。
    /// </summary>
    public static bool TryParseLegacy(
        string? raw,
        out ProxyEndpoint? endpoint,
        out string? userName,
        out string? password,
        out ProxyEndpointError error)
    {
        endpoint = null;
        userName = null;
        password = null;
        error = ProxyEndpointError.None;

        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            error = ProxyEndpointError.HostRequired;
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = ProxyEndpointError.HostInvalid;
            return false;
        }

        if (!ProxyProtocols.IsSupported(uri.Scheme))
        {
            error = ProxyEndpointError.UnsupportedProtocol;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/"))
        {
            error = ProxyEndpointError.HostInvalid;
            return false;
        }

        if (uri.Port <= 0)
        {
            error = ProxyEndpointError.PortRequired;
            return false;
        }

        if (!ProxyEndpoint.TryCreate(
                uri.Scheme,
                uri.Host,
                uri.Port,
                out endpoint,
                out error))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var separator = uri.UserInfo.IndexOf(':', StringComparison.Ordinal);
            var rawUser = separator >= 0 ? uri.UserInfo[..separator] : uri.UserInfo;
            var rawPassword = separator >= 0 ? uri.UserInfo[(separator + 1)..] : string.Empty;
            try
            {
                userName = Uri.UnescapeDataString(rawUser);
                password = Uri.UnescapeDataString(rawPassword);
            }
            catch (UriFormatException)
            {
                endpoint = null;
                userName = null;
                password = null;
                error = ProxyEndpointError.HostInvalid;
                return false;
            }

            if (userName.Length == 0)
            {
                endpoint = null;
                error = ProxyEndpointError.HostInvalid;
                return false;
            }
        }

        return true;
    }

    public static string ErrorCode(ProxyEndpointError error) => error switch
    {
        ProxyEndpointError.UnsupportedProtocol => "network.proxy.unsupported_protocol",
        ProxyEndpointError.HostRequired => "network.proxy.host_required",
        ProxyEndpointError.HostInvalid => "network.proxy.host_invalid",
        ProxyEndpointError.PortRequired => "network.proxy.port_required",
        ProxyEndpointError.PortInvalid => "network.proxy.port_invalid",
        ProxyEndpointError.PortOutOfRange => "network.proxy.port_range",
        _ => "network.proxy.invalid",
    };

    public static string DescribeEndpointError(ProxyEndpointError error) => error switch
    {
        ProxyEndpointError.UnsupportedProtocol => "代理协议不在当前实现实测支持的范围内。",
        ProxyEndpointError.HostRequired => "代理主机不能为空。",
        ProxyEndpointError.HostInvalid => "代理主机格式无效。",
        ProxyEndpointError.PortRequired => "代理端口不能为空。",
        ProxyEndpointError.PortInvalid => "代理端口必须是整数。",
        ProxyEndpointError.PortOutOfRange => "代理端口必须在 1 到 65535 之间。",
        _ => "代理端点无效。",
    };
}

/// <summary>
/// 一条代理认证信息。实现为普通类而不是 record：
/// record 自动生成的 <c>ToString()</c> 会把密码写进日志/诊断字符串，这里只暴露用户名。
/// </summary>
public sealed class ProxyCredential
{
    public ProxyCredential(string userName, string password)
    {
        UserName = userName ?? string.Empty;
        Password = password ?? string.Empty;
    }

    public string UserName { get; }

    public string Password { get; }

    public override string ToString() => UserName;
}

public interface IProxyCredentialStore
{
    /// <summary>当前平台/构建是否支持受保护凭据存储；不支持时页面必须如实说明。</summary>
    bool IsSupported { get; }

    Result<ProxyCredential?> Read(ProxyEndpoint endpoint);

    Result Save(ProxyEndpoint endpoint, ProxyCredential credential);

    Result Delete(ProxyEndpoint endpoint);
}

/// <summary>测试与不支持平台的空实现：不保存任何凭据，也不假装保存成功。</summary>
public sealed class NullProxyCredentialStore : IProxyCredentialStore
{
    public bool IsSupported => false;

    public Result<ProxyCredential?> Read(ProxyEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Result<ProxyCredential?>.Ok(null);
    }

    public Result Save(ProxyEndpoint endpoint, ProxyCredential credential) =>
        Result.Fail("network.credentials_unsupported", "当前平台不支持受保护的代理凭据存储。");

    public Result Delete(ProxyEndpoint endpoint) =>
        Result.Fail("network.credentials_unsupported", "当前平台不支持受保护的代理凭据存储。");
}

public enum ProxyMigrationStatus
{
    NothingToDo,
    Migrated,
    Unresolved,
    Failed,
}

public sealed record ProxyMigrationResult(
    ProxyMigrationStatus Status,
    string? ErrorCode = null,
    string? Detail = null,
    bool LegacyRetained = false)
{
    public bool IsMigrated => Status == ProxyMigrationStatus.Migrated;
}

/// <summary>
/// D-04 的旧配置迁移：只读取旧 <c>network.proxyAddress</c>，解析成功时写入结构化键并清除旧值；
/// 解析失败时保留旧值并返回原因，绝不猜测、丢弃或写半套端点。
///
/// 写入不是跨键事务：结构化键任一项失败时清除本次已写入的键，旧地址保持不动；
/// 运行时解析器在结构化键不完整时会使用旧地址，因此失败不会造成“静默直连”。
/// </summary>
public static class ProxySettingsMigration
{
    public static ProxyMigrationResult TryMigrate(
        ISettingsService settings,
        IProxyCredentialStore credentials,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);

        try
        {
            var legacy = settings.GetRawString(ProxySettings.LegacyAddressKey);
            var hasStructured = settings.GetSource(ProxySettings.ProtocolKey) != SettingsValueSource.Default
                || settings.GetSource(ProxySettings.HostKey) != SettingsValueSource.Default
                || settings.GetSource(ProxySettings.PortKey) != SettingsValueSource.Default;

            if (hasStructured)
            {
                // 新配置已经存在：旧地址只剩清理价值；清理失败不影响新配置。
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    return TryClearLegacy(settings, logger)
                        ? new ProxyMigrationResult(ProxyMigrationStatus.NothingToDo)
                        : new ProxyMigrationResult(
                            ProxyMigrationStatus.Failed,
                            "network.proxy.legacy_clear_failed",
                            "旧代理地址无法清除。",
                            LegacyRetained: true);
                }

                return new ProxyMigrationResult(ProxyMigrationStatus.NothingToDo);
            }

            if (string.IsNullOrWhiteSpace(legacy))
            {
                return new ProxyMigrationResult(ProxyMigrationStatus.NothingToDo);
            }

            if (!ProxySettings.TryParseLegacy(
                    legacy,
                    out var endpoint,
                    out var userName,
                    out var password,
                    out var error)
                || endpoint is null)
            {
                logger?.LogWarning(
                    "旧版代理地址无法无歧义拆分（{Code}），保留原值并等待用户修复。",
                    ProxySettings.ErrorCode(error));
                return new ProxyMigrationResult(
                    ProxyMigrationStatus.Unresolved,
                    ProxySettings.ErrorCode(error),
                    ProxySettings.DescribeEndpointError(error),
                    LegacyRetained: true);
            }

            if (userName is not null)
            {
                var saved = credentials.Save(endpoint, new ProxyCredential(userName, password ?? string.Empty));
                if (!saved.IsSuccess)
                {
                    logger?.LogWarning(
                        "旧版代理地址的凭据无法写入受保护存储：{Error}",
                        saved.Error?.Code);
                    return new ProxyMigrationResult(
                        ProxyMigrationStatus.Unresolved,
                        "network.proxy.credential_migration_failed",
                        saved.Error?.Message ?? "无法保存代理凭据。",
                        LegacyRetained: true);
                }
            }

            if (!TryWrite(settings, ProxySettings.ProtocolKey, endpoint.Protocol)
                || !TryWrite(settings, ProxySettings.HostKey, endpoint.Host)
                || !TryWrite(settings, ProxySettings.PortKey, endpoint.Port.ToString(CultureInfo.InvariantCulture)))
            {
                // 还原本次可能写入的部分结构化键，避免留下半套配置。
                TryClear(settings, ProxySettings.ProtocolKey);
                TryClear(settings, ProxySettings.HostKey);
                TryClear(settings, ProxySettings.PortKey);
                logger?.LogWarning("旧版代理地址迁移未能写入结构化端点，保留原值。");
                return new ProxyMigrationResult(
                    ProxyMigrationStatus.Failed,
                    "network.proxy.migration_failed",
                    "无法写入结构化代理配置。",
                    LegacyRetained: true);
            }

            if (!TryClearLegacy(settings, logger))
            {
                return new ProxyMigrationResult(
                    ProxyMigrationStatus.Migrated,
                    "network.proxy.legacy_clear_failed",
                    "结构化代理已写入，但旧地址无法清除。",
                    LegacyRetained: true);
            }

            logger?.LogInformation(
                "代理配置迁移完成：{Endpoint}（旧地址已清除）。",
                endpoint);
            return new ProxyMigrationResult(ProxyMigrationStatus.Migrated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "代理配置迁移失败；保留原有配置。");
            return new ProxyMigrationResult(
                ProxyMigrationStatus.Failed,
                "network.proxy.migration_failed",
                exception.Message,
                LegacyRetained: true);
        }
    }

    private static bool TryWrite(ISettingsService settings, string key, string value)
    {
        try
        {
            return settings.TrySetRaw(key, value, null, out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryClear(ISettingsService settings, string key)
    {
        try
        {
            settings.TryClear(key, null, out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 还原是尽力而为；失败时运行时仍会因端点不完整而显式报错。
        }
    }

    private static bool TryClearLegacy(ISettingsService settings, ILogger? logger)
    {
        try
        {
            return settings.TryClear(ProxySettings.LegacyAddressKey, null, out var error)
                || string.IsNullOrWhiteSpace(settings.GetRawString(ProxySettings.LegacyAddressKey));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "清除旧代理地址失败。");
            return false;
        }
    }
}
