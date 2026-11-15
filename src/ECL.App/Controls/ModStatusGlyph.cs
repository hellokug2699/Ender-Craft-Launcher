// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Ecl.App.ViewModels;

namespace Ecl.App.Controls;

/// <summary>
/// 模组状态图标（B2 步骤 3b-3）：按 `hints/ui/pages/mods.md` §6 的七种状态绘制矢量图标。
/// 页面规格用 🚫/⛔ 表达“冲突/缺少依赖/禁用”，这里绘制同义的禁止与禁行符号，
/// 不使用 emoji 字符代替正式图标。
/// </summary>
public sealed class ModStatusGlyph : FrameworkElement
{
    private static readonly Color SuccessFallback = Color.FromRgb(0x4B, 0xDA, 0x9F);
    private static readonly Color WarningFallback = Color.FromRgb(0xE9, 0xB9, 0x49);
    private static readonly Color DangerFallback = Color.FromRgb(0xF4, 0x79, 0x7F);
    private static readonly Color UnknownFallback = Color.FromRgb(0xE0, 0x8A, 0x3C);
    private static readonly Color MutedFallback = Color.FromRgb(0x71, 0x6C, 0x82);
    private static readonly Color CutoutFallback = Color.FromRgb(0x0F, 0x0E, 0x14);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(ModStatusKind),
        typeof(ModStatusGlyph),
        new FrameworkPropertyMetadata(ModStatusKind.Available, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize),
        typeof(double),
        typeof(ModStatusGlyph),
        new FrameworkPropertyMetadata(
            14d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public ModStatusKind Kind
    {
        get => (ModStatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = IconSize;
        return new Size(
            double.IsInfinity(availableSize.Width) ? size : Math.Min(size, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? size : Math.Min(size, availableSize.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 1)
        {
            return;
        }

        switch (Kind)
        {
            case ModStatusKind.Available:
                DrawCircleCheck(drawingContext, size, Brush("SuccessBrush", SuccessFallback));
                break;
            case ModStatusKind.PossiblyAbnormal:
                DrawWarningTriangle(drawingContext, size, Brush("WarningBrush", WarningFallback), "!");
                break;
            case ModStatusKind.Dangerous:
                DrawWarningTriangle(drawingContext, size, Brush("DangerBrush", DangerFallback), "!");
                break;
            case ModStatusKind.Unknown:
                DrawWarningTriangle(drawingContext, size, Brush("ModUnknownBrush", UnknownFallback), "?");
                break;
            case ModStatusKind.Conflict:
                DrawProhibition(drawingContext, size, Brush("WarningBrush", WarningFallback));
                break;
            case ModStatusKind.MissingDependency:
                DrawNoEntry(drawingContext, size, Brush("WarningBrush", WarningFallback));
                break;
            case ModStatusKind.Disabled:
                DrawNoEntry(drawingContext, size, Brush("TextMutedBrush", MutedFallback));
                break;
        }
    }

    private Brush Brush(string resourceKey, Color fallback) =>
        TryFindResource(resourceKey) as Brush ?? new SolidColorBrush(fallback);

    private static Pen StrokePen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    /// <summary>可用：圆形内 ✓（mods.md §6）。</summary>
    private static void DrawCircleCheck(DrawingContext context, double size, Brush brush)
    {
        var center = new Point(size / 2, size / 2);
        context.DrawEllipse(null, StrokePen(brush, 1.3), center, size / 2 - 0.8, size / 2 - 0.8);

        var check = new StreamGeometry();
        using (var geometry = check.Open())
        {
            geometry.BeginFigure(new Point(size * 0.28, size * 0.52), isFilled: false, isClosed: false);
            geometry.LineTo(new Point(size * 0.44, size * 0.68), isStroked: true, isSmoothJoin: false);
            geometry.LineTo(new Point(size * 0.72, size * 0.36), isStroked: true, isSmoothJoin: false);
        }

        check.Freeze();
        context.DrawGeometry(null, StrokePen(brush, 1.4), check);
    }

    /// <summary>实心三角 + 感叹号/问号（可能异常、危险、未知）。</summary>
    private void DrawWarningTriangle(DrawingContext context, double size, Brush brush, string mark)
    {
        var triangle = new StreamGeometry();
        using (var geometry = triangle.Open())
        {
            geometry.BeginFigure(new Point(size / 2, 1.1), isFilled: true, isClosed: true);
            geometry.LineTo(new Point(size - 1.1, size - 1.1), isStroked: true, isSmoothJoin: false);
            geometry.LineTo(new Point(1.1, size - 1.1), isStroked: true, isSmoothJoin: false);
        }

        triangle.Freeze();
        context.DrawGeometry(brush, null, triangle);

        var cutout = Brush("BackgroundBrush", CutoutFallback);
        var glyphSize = size * 0.60;
        var typeface = new Typeface(
            new FontFamily("Segoe UI"),
            FontStyles.Normal,
            FontWeights.Bold,
            FontStretches.Normal);
        var text = new FormattedText(
            mark,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            glyphSize,
            cutout,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // 三角内部可用区域从约 28% 高度开始；把标记整体放在三角形内部，避免压到尖角。
        context.DrawText(
            text,
            new Point((size - text.Width) / 2, (size * 0.27) + ((size * 0.68 - text.Height) / 2)));
    }

    /// <summary>禁止符号（冲突）：圆环 + 斜杠。</summary>
    private static void DrawProhibition(DrawingContext context, double size, Brush brush)
    {
        var center = new Point(size / 2, size / 2);
        context.DrawEllipse(null, StrokePen(brush, 1.4), center, size / 2 - 0.8, size / 2 - 0.8);
        context.DrawLine(
            StrokePen(brush, 1.4),
            new Point(size * 0.26, size * 0.26),
            new Point(size * 0.74, size * 0.74));
    }

    /// <summary>禁行符号（缺少依赖、禁用）：圆环 + 横杠。</summary>
    private static void DrawNoEntry(DrawingContext context, double size, Brush brush)
    {
        var center = new Point(size / 2, size / 2);
        context.DrawEllipse(null, StrokePen(brush, 1.4), center, size / 2 - 0.8, size / 2 - 0.8);
        context.DrawLine(
            StrokePen(brush, 1.6),
            new Point(size * 0.28, size / 2),
            new Point(size * 0.72, size / 2));
    }
}
