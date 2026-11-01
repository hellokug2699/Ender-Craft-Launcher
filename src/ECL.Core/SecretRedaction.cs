// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace Ecl.Core;

/// <summary>
/// 敏感值（token）在日志、界面与命令预览中只保留两端，中间统一用 <c>*</c> 覆盖。
/// 这样既能确认“有凭据”，又不会泄漏可用凭据本身。
/// </summary>
public static partial class SecretRedaction
{
    private const int VisiblePrefix = 8;
    private const int VisibleSuffix = 4;
    private const int MaskWidth = 8;

    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        if (secret.Length <= VisiblePrefix + VisibleSuffix + MaskWidth)
        {
            return new string('*', Math.Min(secret.Length, MaskWidth));
        }

        return $"{secret[..VisiblePrefix]}{new string('*', MaskWidth)}{secret[^VisibleSuffix..]}";
    }

    /// <summary>把命令行里的 <c>--accessToken</c> 取值替换为脱敏形式，其余部分保持不变。</summary>
    public static string CommandLine(string commandLine) =>
        string.IsNullOrEmpty(commandLine)
            ? commandLine
            : AccessTokenArgument().Replace(
                commandLine,
                match => $"{match.Groups[1].Value}{Mask(match.Groups[2].Value)}");

    /// <summary>
    /// 通用文本脱敏：用于日志、崩溃报告与诊断导出。只处理明确的凭据形态，
    /// 不修改原始本地证据；调用方应在派生副本上使用。
    /// </summary>
    public static string Text(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = BearerPattern().Replace(text, "Bearer ***");
        result = TokenKeyPattern().Replace(result, "$1***");
        result = CommandLine(result);
        result = SignedJwtPattern().Replace(result, "$1***");
        return UrlCredentialsPattern().Replace(result, "$1***@");
    }

    [GeneratedRegex(@"(--accessToken\s+)(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenArgument();

    [GeneratedRegex(
        "(?i)(\"?(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization|token)\"?\\s*[:=]\\s*\"?)[^\"\\s,}]+")]
    private static partial Regex TokenKeyPattern();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)(SignedJWT:\s*)[A-Za-z0-9._~+/=-]{16,}")]
    private static partial Regex SignedJwtPattern();

    [GeneratedRegex("(://)[^/@\\s]+@")]
    private static partial Regex UrlCredentialsPattern();
}
