// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Media;

namespace Ecl.App.Theming;

public enum EclTheme
{
    Dark,
    Light,
}

public sealed record ThemePalette(
    Color Background,
    Color Sidebar,
    Color Surface,
    Color SurfaceAlt,
    Color SurfaceHover,
    Color Border,
    Color TextPrimary,
    Color TextSecondary,
    Color TextMuted,
    Color Accent,
    Color AccentHover,
    Color AccentSoft,
    Color Success,
    Color Warning,
    Color Danger,
    Color DangerSoft,
    Color DangerBorder,
    Color DangerHover);

/// <summary>
/// 主题切换不替换 ResourceDictionary，而是直接修改共享 SolidColorBrush 的 Color。
/// 这样所有使用 StaticResource 的既有控件都能立即刷新，避免重建可视树或重启。
/// </summary>
public static class ThemeManager
{
    public static EclTheme Current { get; private set; } = EclTheme.Dark;

    public static event EventHandler? ThemeChanged;

    private static readonly ThemePalette Dark = new(
        Background: ColorFrom("#0E1015"),
        Sidebar: ColorFrom("#13161D"),
        Surface: ColorFrom("#181C24"),
        SurfaceAlt: ColorFrom("#202633"),
        SurfaceHover: ColorFrom("#262D3B"),
        Border: ColorFrom("#2A3140"),
        TextPrimary: ColorFrom("#F3F5F9"),
        TextSecondary: ColorFrom("#98A2B3"),
        TextMuted: ColorFrom("#6B7485"),
        Accent: ColorFrom("#7C6CF0"),
        AccentHover: ColorFrom("#9082F5"),
        AccentSoft: ColorFrom("#2A2542"),
        Success: ColorFrom("#3DDC97"),
        Warning: ColorFrom("#F2C14E"),
        Danger: ColorFrom("#F06A6A"),
        DangerSoft: ColorFrom("#2E1D22"),
        DangerBorder: ColorFrom("#4A2A31"),
        DangerHover: ColorFrom("#3A232A"));

    private static readonly ThemePalette Light = new(
        Background: ColorFrom("#F4F6FA"),
        Sidebar: ColorFrom("#FFFFFF"),
        Surface: ColorFrom("#FFFFFF"),
        SurfaceAlt: ColorFrom("#EDF1F7"),
        SurfaceHover: ColorFrom("#E2E8F1"),
        Border: ColorFrom("#D3DBE7"),
        TextPrimary: ColorFrom("#1A2230"),
        TextSecondary: ColorFrom("#4D5A6B"),
        TextMuted: ColorFrom("#7B8798"),
        Accent: ColorFrom("#5A4BD8"),
        AccentHover: ColorFrom("#6E5EE8"),
        AccentSoft: ColorFrom("#E9E5FF"),
        Success: ColorFrom("#16865A"),
        Warning: ColorFrom("#A66A12"),
        Danger: ColorFrom("#B42318"),
        DangerSoft: ColorFrom("#FDECEA"),
        DangerBorder: ColorFrom("#F5C2BC"),
        DangerHover: ColorFrom("#FAD9D5"));

    public static void Apply(string? theme)
    {
        var selected = string.Equals(theme?.Trim(), "light", StringComparison.OrdinalIgnoreCase)
            ? EclTheme.Light
            : EclTheme.Dark;

        if (Application.Current is null)
        {
            Current = selected;
            return;
        }

        var palette = selected == EclTheme.Light ? Light : Dark;
        var resources = Application.Current.Resources;

        ApplyColor(resources, "BackgroundColor", palette.Background);
        ApplyColor(resources, "SidebarColor", palette.Sidebar);
        ApplyColor(resources, "SurfaceColor", palette.Surface);
        ApplyColor(resources, "SurfaceAltColor", palette.SurfaceAlt);
        ApplyColor(resources, "SurfaceHoverColor", palette.SurfaceHover);
        ApplyColor(resources, "BorderColor", palette.Border);
        ApplyColor(resources, "TextPrimaryColor", palette.TextPrimary);
        ApplyColor(resources, "TextSecondaryColor", palette.TextSecondary);
        ApplyColor(resources, "TextMutedColor", palette.TextMuted);
        ApplyColor(resources, "AccentColor", palette.Accent);
        ApplyColor(resources, "AccentHoverColor", palette.AccentHover);
        ApplyColor(resources, "AccentSoftColor", palette.AccentSoft);
        ApplyColor(resources, "SuccessColor", palette.Success);
        ApplyColor(resources, "WarningColor", palette.Warning);
        ApplyColor(resources, "DangerColor", palette.Danger);

        ApplyBrush(resources, "BackgroundBrush", palette.Background);
        ApplyBrush(resources, "SidebarBrush", palette.Sidebar);
        ApplyBrush(resources, "SurfaceBrush", palette.Surface);
        ApplyBrush(resources, "SurfaceAltBrush", palette.SurfaceAlt);
        ApplyBrush(resources, "SurfaceHoverBrush", palette.SurfaceHover);
        ApplyBrush(resources, "BorderBrush", palette.Border);
        ApplyBrush(resources, "TextPrimaryBrush", palette.TextPrimary);
        ApplyBrush(resources, "TextSecondaryBrush", palette.TextSecondary);
        ApplyBrush(resources, "TextMutedBrush", palette.TextMuted);
        ApplyBrush(resources, "AccentBrush", palette.Accent);
        ApplyBrush(resources, "AccentHoverBrush", palette.AccentHover);
        ApplyBrush(resources, "AccentSoftBrush", palette.AccentSoft);
        ApplyBrush(resources, "SuccessBrush", palette.Success);
        ApplyBrush(resources, "WarningBrush", palette.Warning);
        ApplyBrush(resources, "DangerBrush", palette.Danger);
        ApplyBrush(resources, "DangerSoftBrush", palette.DangerSoft);
        ApplyBrush(resources, "DangerBorderBrush", palette.DangerBorder);
        ApplyBrush(resources, "DangerHoverBrush", palette.DangerHover);

        Current = selected;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplyColor(ResourceDictionary resources, string key, Color color)
    {
        var current = Application.Current?.TryFindResource(key);
        if (current is Color existing && existing != color)
        {
            resources[key] = color;
        }
    }

    private static void ApplyBrush(ResourceDictionary resources, string key, Color color)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }

    private static Color ColorFrom(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
