// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using Ecl.World.Nbt;

namespace Ecl.App.ViewModels;

/// <summary>NBT 只读树节点。保留原始标签名与类型，不把本地化名称写回数据。</summary>
public sealed class NbtNodeViewModel
{
    private NbtNodeViewModel(string name, string type, string value, IReadOnlyList<NbtNodeViewModel> children)
    {
        Name = name;
        Type = type;
        Value = value;
        Children = new ObservableCollection<NbtNodeViewModel>(children);
    }

    public string Name { get; }

    public string Type { get; }

    public string Value { get; }

    public ObservableCollection<NbtNodeViewModel> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public static NbtNodeViewModel FromTag(NbtTag tag, string? nameOverride = null)
    {
        var name = nameOverride ?? tag.Name;
        var children = tag.Type switch
        {
            NbtTagType.Compound => tag.Children.Select(child => FromTag(child)).ToArray(),
            NbtTagType.List => tag.Children.Select((child, index) => FromTag(child, $"[{index}]")).ToArray(),
            _ => [],
        };
        return new NbtNodeViewModel(name, tag.Type.ToString(), FormatValue(tag), children);
    }

    private static string FormatValue(NbtTag tag) => tag.Value switch
    {
        null => string.Empty,
        byte[] bytes => $"byte[{bytes.Length}]",
        int[] ints => $"int[{ints.Length}]",
        long[] longs => $"long[{longs.Length}]",
        float single => single.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => tag.Value.ToString() ?? string.Empty,
    };
}
