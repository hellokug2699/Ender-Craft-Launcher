// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ecl.App.Services;

/// <summary>
/// 应用级“字号档位”实现（<c>settings-interface-preferences.md</c> §3）。
///
/// 现有页面的字号来自设计令牌与局部样式，数量很大；为避免在每个 XAML 里再引入第二套
/// 资源键，这里在界面树上按同一倍率换算“元素自己的”字号：
/// - 只处理元素自身设置了字号（局部值 / 样式）或是遍历起点的元素；
/// - 纯继承的字号跟随父元素，不会被重复乘算；
/// - 记录每个元素第一次被换算前的字号，倍率变化时从原始值重算，恢复 1.0 时还原；
/// - 后加载的元素（虚拟化列表、弹窗模板、页面切换）通过 Loaded 路由事件补做一次。
///
/// 只改表现层字号：控件模板、图标尺寸、业务状态与命令执行都不受影响。
/// </summary>
internal sealed class UiTextScaler : IDisposable
{
    private readonly DependencyObject _root;
    private readonly ConditionalWeakTable<DependencyObject, BaseFontSize> _baseSizes = new();
    private readonly RoutedEventHandler _onLoaded;
    private double _factor = 1.0;
    private bool _disposed;

    public UiTextScaler(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        _root = root;
        _onLoaded = OnLoaded;
        if (root is FrameworkElement element)
        {
            element.AddHandler(FrameworkElement.LoadedEvent, _onLoaded, handledEventsToo: true);
        }
    }

    public void Apply(double factor)
    {
        if (_disposed)
        {
            return;
        }

        _factor = factor > 0 && double.IsFinite(factor) ? factor : 1.0;
        ApplySubtree(_root);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_root is FrameworkElement element)
        {
            element.RemoveHandler(FrameworkElement.LoadedEvent, _onLoaded);
        }

        _baseSizes.Clear();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || _factor == 1.0)
        {
            return;
        }

        // Loaded 会为每个新加入已加载树的元素触发：元素本身换算一次即可，
        // 其子元素会各自触发；纯继承字号的子元素跟随已换算的父元素。
        if (e.OriginalSource is DependencyObject element)
        {
            ScaleElement(element);
        }
    }

    private void ApplySubtree(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            ScaleElement(current);

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++)
            {
                stack.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private void ScaleElement(DependencyObject element)
    {
        // 字号是 TextElement 的附着属性，任何 FrameworkElement 都可以持有并向下继承，
        // 因此不做控件白名单：只看它是否“自己持有”字号（局部值或样式）。
        if (element is not FrameworkElement)
        {
            return;
        }

        // 数据绑定或动画驱动的字号不属于设计令牌，换算会破坏绑定表达式。
        if (BindingOperations.IsDataBound(element, TextElement.FontSizeProperty))
        {
            return;
        }

        var source = DependencyPropertyHelper.GetValueSource(element, TextElement.FontSizeProperty);
        var isRoot = ReferenceEquals(element, _root);
        switch (source.BaseValueSource)
        {
            case BaseValueSource.Inherited:
                // 跟随父元素（父元素已被换算）；重复乘算会让层级失真。
                return;
            case BaseValueSource.Default when !isRoot:
                // 没有自己的字号：同样继承父元素。
                return;
            case BaseValueSource.Unknown:
                return;
        }

        var current = (double)element.GetValue(TextElement.FontSizeProperty);
        if (double.IsNaN(current) || current <= 0)
        {
            return;
        }

        if (!_baseSizes.TryGetValue(element, out var baseSize))
        {
            baseSize = new BaseFontSize(current);
            _baseSizes.Add(element, baseSize);
        }

        var target = UiTypography.ScaleFontSize(baseSize.Value, _factor);
        if (Math.Abs(current - target) > 0.001)
        {
            element.SetValue(TextElement.FontSizeProperty, target);
        }
    }

    private sealed class BaseFontSize(double value)
    {
        public double Value { get; } = value;
    }
}
