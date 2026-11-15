// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows.Media;

namespace Ecl.App.Services;

/// <summary>
/// 一个随包提供、家族名与字面均已验证的界面字体族。
///
/// 页面规格（<c>settings-interface-preferences.md</c> §3/§8）要求字体列表只能包含
/// 实际随包提供且经验证的资源，不允许任意字体导入，也不允许在字体缺失时静默回退。
/// Regular / Medium / Bold 必须映射到真实字面：HarmonyOS Sans SC 的 Medium 是独立家族名
/// （HarmonyOS Sans SC Medium），不能靠 FontWeight 合成。
/// </summary>
public sealed record BundledFontFamily(
    string Id,
    string DisplayNameKey,
    string RegularFamilyName,
    string MediumFamilyName)
{
    public FontFamily RegularFontFamily => new($"pack://application:,,,/Fonts/#{RegularFamilyName}");

    public FontFamily MediumFontFamily => new($"pack://application:,,,/Fonts/#{MediumFamilyName}");
}

/// <summary>
/// 随包字体登记表。新增字体必须先提交字体资源、许可证与字面验证，
/// 再在这里登记；不能只在设置选项里加一个字符串。
/// </summary>
public static class BundledFontFamilies
{
    /// <summary>主题里字体资源的键；用户选择字体族时由 <see cref="UserInterfacePreferences"/> 覆盖。</summary>
    public const string RegularResourceKey = "UiFontFamily";

    public const string MediumResourceKey = "UiMediumFontFamily";

    public static IReadOnlyList<BundledFontFamily> All { get; } =
    [
        new(
            "harmonyos-sans-sc",
            "option.launcher.interfaceFont.harmonyosSc",
            "HarmonyOS Sans SC",
            "HarmonyOS Sans SC Medium"),
    ];

    public static BundledFontFamily Default => All[0];

    /// <summary>未知 Id 回退到 ECL 默认字体，但页面仍只显示已登记项，不静默换成系统字体。</summary>
    public static BundledFontFamily Resolve(string? id) =>
        All.FirstOrDefault(family => string.Equals(family.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Default;
}
