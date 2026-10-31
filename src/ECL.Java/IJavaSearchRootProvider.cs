// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

/// <summary>
/// 为 Java 探测器提供额外的搜索根。实现方可以读取启动器设置，
/// 让用户在全局设置里指定一个包含多个 JDK/JRE 的目录。
/// </summary>
public interface IJavaSearchRootProvider
{
    IReadOnlyList<string> GetSearchRoots();
}

/// <summary>
/// 解析用户填写的 Java 路径列表：支持可执行文件、Java 主目录，
/// 也支持包含多个 Java 的上级目录；多个条目用分号或换行分隔。
/// </summary>
public static class JavaSearchPath
{
    private static readonly char[] Separators = [';', '\r', '\n'];

    public static IReadOnlyList<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim().Trim('"'))
            .Where(entry => entry.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
