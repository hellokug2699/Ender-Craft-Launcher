// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Ecl.Configuration;

/// <summary>
/// 一个配置项的完整定义。值本身不在这里，由 <c>ISettingsService</c> 按作用域存储。
/// 定义是元数据：UI、CLI、校验、序列化都从它派生，避免“加一个选项要改五处”。
/// </summary>
public abstract class OptionDefinition
{
    public required string Key { get; init; }

    public required OptionKind Kind { get; init; }

    public required string CategoryKey { get; init; }

    public required string DisplayNameKey { get; init; }

    public string? DescriptionKey { get; init; }

    public string? UnitKey { get; init; }

    public OptionScope Scope { get; init; } = OptionScope.Global;

    public IReadOnlyList<OptionChoice> Choices { get; init; } = [];

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Step { get; init; }

    public bool RequiresRestart { get; init; }

    /// <summary>敏感值不应出现在日志或诊断包中。</summary>
    public bool IsSecret { get; init; }

    /// <summary>空字符串是否表示“未设置 / 继承上层”。对可空数字与实例覆盖项有用。</summary>
    public bool EmptyIsNull { get; init; }

    public int Order { get; init; }

    /// <summary>依赖的另一个配置键；不满足时 UI 隐藏该项。</summary>
    public string? VisibleWhenKey { get; init; }

    public string? VisibleWhenValue { get; init; }

    public abstract Type ValueType { get; }

    public abstract object? DefaultValue { get; }

    public abstract bool TryConvert(string? raw, out object? value, out string? error);

    public string Format(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value switch
        {
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }
}
