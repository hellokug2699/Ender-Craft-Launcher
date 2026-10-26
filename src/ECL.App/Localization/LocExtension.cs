// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Markup;
using Ecl.Core.Localization;

namespace Ecl.App.Localization;

/// <summary>
/// XAML 标记扩展需要静态入口；App 在创建窗口前设置当前本地化器。
/// 语言切换标记为“需要重启”，因此不需要运行时通知。
/// </summary>
public static class LocalizationProvider
{
    public static ILocalizer? Current { get; set; }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        LocalizationProvider.Current?.Get(Key) ?? Key;
}
