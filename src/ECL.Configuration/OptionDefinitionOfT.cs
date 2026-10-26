// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Ecl.Configuration;

public sealed class OptionDefinition<T> : OptionDefinition
{
    public required T Default { get; init; }

    /// <summary>额外的业务校验；返回 false 时使用通用错误信息。</summary>
    public Func<T, bool>? Validate { get; init; }

    public override Type ValueType => Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

    public override object? DefaultValue => Default;

    public override bool TryConvert(string? raw, out object? value, out string? error)
    {
        value = null;
        error = null;

        if (EmptyIsNull && string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var underlyingType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (underlyingType == typeof(bool))
        {
            if (!TryParseBoolean(raw, out var boolean))
            {
                error = $"“{raw}”不是有效的布尔值（true/false）。";
                return false;
            }

            value = boolean;
        }
        else if (underlyingType == typeof(int))
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                error = $"“{raw}”不是有效的整数。";
                return false;
            }

            value = number;
        }
        else if (underlyingType == typeof(long))
        {
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                error = $"“{raw}”不是有效的整数。";
                return false;
            }

            value = number;
        }
        else if (underlyingType == typeof(double))
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                error = $"“{raw}”不是有效的数字。";
                return false;
            }

            value = number;
        }
        else if (underlyingType == typeof(string))
        {
            value = raw ?? string.Empty;
        }
        else
        {
            error = $"暂不支持的配置值类型：{typeof(T).Name}。";
            return false;
        }

        if (value is not null && (Min.HasValue || Max.HasValue))
        {
            var numeric = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Min.HasValue && numeric < Min.Value)
            {
                error = $"不能小于 {Min.Value.ToString(CultureInfo.InvariantCulture)}。";
                return false;
            }

            if (Max.HasValue && numeric > Max.Value)
            {
                error = $"不能大于 {Max.Value.ToString(CultureInfo.InvariantCulture)}。";
                return false;
            }
        }

        if (Kind == OptionKind.Select && value is string selected)
        {
            if (Choices.Count == 0 || !Choices.Any(choice => string.Equals(choice.Value, selected, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"“{selected}”不是可选项。";
                return false;
            }
        }

        if (value is not null && Validate is not null && !Validate((T)value))
        {
            error = "值不符合该项的要求。";
            return false;
        }

        return true;
    }

    private static bool TryParseBoolean(string? raw, out bool value)
    {
        if (bool.TryParse(raw, out value))
        {
            return true;
        }

        switch (raw?.Trim().ToLowerInvariant())
        {
            case "1":
            case "on":
            case "yes":
                value = true;
                return true;
            case "0":
            case "off":
            case "no":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }
}
