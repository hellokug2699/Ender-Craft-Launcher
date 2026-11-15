// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Ecl.App.Workspace.ViewModels;

namespace Ecl.App.Controls;

/// <summary>
/// 资源包/光影包状态图标（B2b）：沿用模组页的矢量图标风格
/// （圆环 ✓、实心三角、圆环问号），不使用 emoji 字形。
/// 颜色语义来自 resource-library-template.md §3.1：绿色正常、黄色异常、红色损坏、灰色未识别。
/// </summary>
public sealed class ContentPackStatusGlyph : FrameworkElement
{
    private static readonly Color SuccessFallback = Color.FromRgb(0x4B, 0xDA, 0x9F);
    private static readonly Color WarningFallback = Color.FromRgb(0xE9, 0xB9, 0x49);
    private static readonly Color DangerFallback = Color.FromRgb(0xF4, 0x79, 0x7F);
    private static readonly Color MutedFallback = Color.FromRgb(0x71, 0x6C, 0x82);
    private static readonly Color CutoutFallback = Color.FromRgb(0x0F, 0x0E, 0x14);

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(ContentPackStatusKind),
        typeof(ContentPackStatusGlyph),
        new FrameworkPropertyMetadata(
            ContentPackStatusKind.Active,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize),
        typeof(double),
        typeof(ContentPackStatusGlyph),
        new FrameworkPropertyMetadata(
            14d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public ContentPackStatusKind Kind
    {
        get => (ContentPackStatusKind)GetValue(KindProperty);
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
            case ContentPackStatusKind.Active:
                DrawCircleCheck(drawingContext, size, Brush("SuccessBrush", SuccessFallback));
                break;
            case ContentPackStatusKind.Warning:
                DrawWarningTriangle(drawingContext, size, Brush("WarningBrush", WarningFallback), "!");
                break;
            case ContentPackStatusKind.Corrupt:
                DrawWarningTriangle(drawingContext, size, Brush("DangerBrush", DangerFallback), "!");
                break;
            case ContentPackStatusKind.Unrecognized:
                DrawCircleQuestion(drawingContext, size, Brush("TextMutedBrush", MutedFallback));
                break;
            case ContentPackStatusKind.Note:
                DrawCircleMark(drawingContext, size, Brush("WarningBrush", WarningFallback), "!");
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

    /// <summary>圆环 + 问号（未识别）：灰色，不使用红色危险样式。</summary>
    private void DrawCircleQuestion(DrawingContext context, double size, Brush brush) =>
        DrawCircleMark(context, size, brush, "?");

    /// <summary>空心圆环 + 标记（页面提示）：不填充，避免与“危险”实心三角混淆。</summary>
    private void DrawCircleMark(DrawingContext context, double size, Brush brush, string mark)
    {
        var center = new Point(size / 2, size / 2);
        context.DrawEllipse(null, StrokePen(brush, 1.3), center, size / 2 - 0.8, size / 2 - 0.8);

        var typeface = new Typeface(
            new FontFamily("Segoe UI"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);
        var text = new FormattedText(
            mark,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            size * 0.62,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
    }

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
            size * 0.60,
            cutout,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        context.DrawText(
            text,
            new Point((size - text.Width) / 2, (size * 0.27) + ((size * 0.68 - text.Height) / 2)));
    }
}
