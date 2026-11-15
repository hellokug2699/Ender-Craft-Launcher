// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Deployment;

namespace Ecl.Additions;

/// <summary>
/// 宿主当前暴露的 Additions Core API 版本（ADR 0032 §6）。目录条目的兼容范围与已安装记录的
/// 适用范围都用它判断；这里只保留一份权威值，避免安装计划、运行时与设置页各自硬编码。
/// </summary>
public static class AdditionCoreApi
{
    public static ReleaseVersion Version { get; } = ReleaseVersion.Parse("1.0.0");
}
