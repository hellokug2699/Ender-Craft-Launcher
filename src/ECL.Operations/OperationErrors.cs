// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Ecl.Operations;

/// <summary>
/// 把异常映射为稳定的失败归因。UI 只读取 <see cref="OperationErrorKind"/>，
/// 不需要解析异常文本或 HTTP 细节。
/// </summary>
public static class OperationErrors
{
    public static OperationError FromException(string code, Exception exception) =>
        new(Classify(exception), code, exception.Message, exception.InnerException?.Message);

    /// <summary>
    /// 把业务错误码（形如 <c>install.download_failed</c>）映射为失败归因。
    /// 这是启发式：服务掌握更具体的异常时（例如 <c>DownloadException</c>）
    /// 应优先使用 <see cref="FromException"/>。
    /// </summary>
    public static OperationError FromCode(string code, string message, string? detail = null) =>
        new(ClassifyCode(code), code, message, detail);

    /// <summary>按错误码关键词推断归因，未匹配时视为启动器内部错误。</summary>
    public static OperationErrorKind ClassifyCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return OperationErrorKind.EclInternal;
        }

        var normalized = code.ToLowerInvariant();
        if (normalized.EndsWith(".cancelled", StringComparison.Ordinal) ||
            normalized.EndsWith(".canceled", StringComparison.Ordinal))
        {
            return OperationErrorKind.Cancelled;
        }

        if (HasAny(normalized, "authentication", "unauthorized", "forbidden", "token"))
        {
            return OperationErrorKind.Authentication;
        }

        if (HasAny(normalized, "hash", "integrity", "checksum", "size"))
        {
            return OperationErrorKind.FileIntegrity;
        }

        if (HasAny(normalized, "download", "network", "timeout", "connect", "dns"))
        {
            return OperationErrorKind.Network;
        }

        if (HasAny(normalized, "write", "disk", "space", "localio", "io_"))
        {
            return OperationErrorKind.LocalIO;
        }

        if (HasAny(normalized, "upstream", "curseforge", "modrinth", "http"))
        {
            return OperationErrorKind.Upstream;
        }

        if (HasAny(normalized, "not_found", "missing", "invalid", "unsupported", "mismatch", "duplicate"))
        {
            return OperationErrorKind.Configuration;
        }

        return OperationErrorKind.EclInternal;
    }

    private static bool HasAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));

    public static OperationErrorKind Classify(Exception exception)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return OperationErrorKind.Cancelled;
            case AggregateException aggregate:
                return Worst(aggregate.Flatten().InnerExceptions.Select(Classify));
            case HttpRequestException http:
                if (http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return OperationErrorKind.Authentication;
                }

                return http.StatusCode is null ? OperationErrorKind.Network : OperationErrorKind.Upstream;
            case SocketException:
            case TimeoutException:
                return OperationErrorKind.Network;
            case InvalidDataException:
            case CryptographicException:
                return OperationErrorKind.FileIntegrity;
            case UnauthorizedAccessException:
            case IOException:
                return OperationErrorKind.LocalIO;
            default:
                return exception.InnerException is SocketException
                    ? OperationErrorKind.Network
                    : OperationErrorKind.EclInternal;
        }
    }

    /// <summary>多个失败同时存在时选择最需要用户处理的原因。</summary>
    public static OperationErrorKind Worst(IEnumerable<OperationErrorKind> kinds)
    {
        var worst = OperationErrorKind.None;
        foreach (var kind in kinds)
        {
            if (Severity(kind) > Severity(worst))
            {
                worst = kind;
            }
        }

        return worst;
    }

    private static int Severity(OperationErrorKind kind) => kind switch
    {
        OperationErrorKind.Authentication => 100,
        OperationErrorKind.Configuration => 90,
        OperationErrorKind.FileIntegrity => 80,
        OperationErrorKind.LocalIO => 70,
        OperationErrorKind.Network => 60,
        OperationErrorKind.Upstream => 50,
        OperationErrorKind.EclInternal => 10,
        _ => 0,
    };
}
