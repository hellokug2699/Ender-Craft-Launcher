// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace Ecl.Core;

/// <summary>
/// 本地绝对路径脱敏：把 Windows 盘符路径与 UNC 路径替换为 <c>&lt;local-path&gt;</c>。
///
/// 诊断导出（<c>SessionExportService</c>）与用户可见通知（<c>NotificationService</c>）都需要
/// 同一条规则：绝对路径会暴露用户名与目录结构，而这两处都不需要定位到具体文件。
/// 规则只作用于派生副本，原始证据与真实文件路径保持不变。
/// </summary>
public static partial class LocalPathRedaction
{
    public const string Placeholder = "<local-path>";

    /// <summary>按路径规则脱敏；空输入原样返回。</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = WindowsAbsolutePath().Replace(text, Placeholder);
        return UncAbsolutePath().Replace(redacted, Placeholder);
    }

    /// <summary>
    /// 盘符路径：前面不能紧邻字母数字，避免把 <c>abc:D:\x</c> 之类的标识符尾部当成新路径；
    /// 路径终止于引号、换行与 Windows 文件名非法字符。
    /// </summary>
    [GeneratedRegex(
        "(?<![A-Za-z0-9])[A-Za-z]:\\\\[^\\r\\n\"'<>|*?]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAbsolutePath();

    /// <summary>UNC 路径（<c>\\\\server\\share\\...</c>）；同样不跨越空白与引号。</summary>
    [GeneratedRegex(
        "\\\\\\\\[^\\s\"']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex UncAbsolutePath();
}
