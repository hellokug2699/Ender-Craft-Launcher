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

    [GeneratedRegex(@"(--accessToken\s+)(\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenArgument();
}
