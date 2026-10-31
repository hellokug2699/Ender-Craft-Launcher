// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;
using Ecl.Java;

namespace Ecl.Minecraft.Java;

/// <summary>
/// 把全局设置里的 Java 目录/路径提供给 Java 探测器。
/// 用户既可以填单个 java.exe，也可以填一个包含多个 JDK/JRE 的父目录。
/// </summary>
internal sealed class SettingsJavaSearchRootProvider(ISettingsService settings) : IJavaSearchRootProvider
{
    public IReadOnlyList<string> GetSearchRoots() =>
        JavaSearchPath.Split(settings.Get<string>("java.defaultPath"));
}
