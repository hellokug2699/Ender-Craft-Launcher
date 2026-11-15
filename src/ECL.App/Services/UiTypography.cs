// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.App.Services;

/// <summary>
/// 界面偏好的字号档位与缩放范围（<c>settings-interface-preferences.md</c> §3/§4）。
///
/// 这里只定义 Design System 的换算规则，不定义页面字号：
/// - 字号档位是“整体文字尺寸”的倍率，标题/正文/辅助信息按同一倍率变化，层级关系不变；
/// - 不提供低于标准的档位：ADR 0050 §5 要求常规信息不得使用极小字号，
///   继续缩小会跌破设计系统的可读性底线；
/// - 界面缩放是 ECL 自身布局的缩放（80%–150%，步进 5%），不修改 Windows 显示缩放。
/// </summary>
public static class UiTypography
{
    public const string FontFamilyKey = "launcher.interfaceFont";
    public const string FontSizeKey = "launcher.fontSize";
    public const string UiScaleKey = "launcher.uiScale";
    public const string MotionKey = "launcher.animationsEnabled";

    public const string FontSizeTierStandard = "standard";
    public const string FontSizeTierLarge = "large";
    public const string FontSizeTierExtraLarge = "xlarge";

    public const int DefaultUiScalePercent = 100;
    public const int MinimumUiScalePercent = 80;
    public const int MaximumUiScalePercent = 150;
    public const int UiScaleStepPercent = 5;

    /// <summary>档位顺序即页面呈现顺序；倍率由设计系统集中定义。</summary>
    private static readonly IReadOnlyDictionary<string, double> FontSizeFactors =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [FontSizeTierStandard] = 1.0,
            [FontSizeTierLarge] = 1.1,
            [FontSizeTierExtraLarge] = 1.25,
        };

    public static IReadOnlyList<string> FontSizeTiers { get; } =
    [
        FontSizeTierStandard,
        FontSizeTierLarge,
        FontSizeTierExtraLarge,
    ];

    /// <summary>未知或空档位回退到标准倍率，不猜测用户意图。</summary>
    public static string NormalizeFontSizeTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
        {
            return FontSizeTierStandard;
        }

        var trimmed = tier.Trim();
        return FontSizeFactors.ContainsKey(trimmed) ? trimmed.ToLowerInvariant() : FontSizeTierStandard;
    }

    public static double GetFontSizeFactor(string? tier) =>
        FontSizeFactors[NormalizeFontSizeTier(tier)];

    /// <summary>
    /// 把持久化/用户输入折算成合法缩放值：先取整到步进，再夹到范围内。
    /// 非法输入（NaN、0、负数）回到默认 100%。
    /// </summary>
    public static int ClampUiScale(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent <= 0)
        {
            return DefaultUiScalePercent;
        }

        var stepped = (int)Math.Round(percent / UiScaleStepPercent, MidpointRounding.AwayFromZero) *
                      UiScaleStepPercent;
        return Math.Clamp(stepped, MinimumUiScalePercent, MaximumUiScalePercent);
    }

    /// <summary>
    /// 按倍率换算单个字号。保留两位小数，避免 13 × 1.25 = 16.250000000000004 之类的
    /// 浮点尾巴让布局取整在每次重算时抖动。
    /// </summary>
    public static double ScaleFontSize(double baseSize, double factor) =>
        Math.Round(baseSize * factor, 2, MidpointRounding.AwayFromZero);
}
