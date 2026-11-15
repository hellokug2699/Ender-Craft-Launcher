// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;

namespace Ecl.App.Controls;

/// <summary>
/// 多状态图标的排列（mods.md §6.2）：两个并排、三个呈三角形、四个呈正方形。
/// 一个状态按单个图标正常展示；最多四个，与页面规格一致。
/// </summary>
public sealed class ModStatusBadgePanel : Panel
{
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize),
        typeof(double),
        typeof(ModStatusBadgePanel),
        new FrameworkPropertyMetadata(
            14d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap),
        typeof(double),
        typeof(ModStatusBadgePanel),
        new FrameworkPropertyMetadata(
            2d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = IconSize;
        var cell = size + Gap;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(size, size));
        }

        return InternalChildren.Count switch
        {
            0 => new Size(0, 0),
            1 => new Size(size, size),
            2 => new Size(cell * 2 - Gap, size),
            _ => new Size(cell * 2 - Gap, cell * 2 - Gap),
        };
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = IconSize;
        var cell = size + Gap;
        var desired = DesiredSize;
        var offsetX = Math.Max(0, (finalSize.Width - desired.Width) / 2);
        var offsetY = Math.Max(0, (finalSize.Height - desired.Height) / 2);
        var count = InternalChildren.Count;

        Point Position(int index) => count switch
        {
            1 => new Point(offsetX, offsetY),
            2 => new Point(offsetX + (index * cell), offsetY),
            3 => index switch
            {
                0 => new Point(offsetX + (cell / 2), offsetY),
                1 => new Point(offsetX, offsetY + cell),
                _ => new Point(offsetX + cell, offsetY + cell),
            },
            _ => new Point(offsetX + ((index % 2) * cell), offsetY + ((index / 2) * cell)),
        };

        for (var index = 0; index < count; index++)
        {
            InternalChildren[index].Arrange(new Rect(Position(index), new Size(size, size)));
        }

        return finalSize;
    }
}
