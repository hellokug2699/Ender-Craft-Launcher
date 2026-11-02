// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace Ecl.World.Nbt;

/// <summary>NBT 标签类型。<see cref="End"/> 只用于列表终止与空复合标签。</summary>
public enum NbtTagType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,
}

/// <summary>
/// 与文件格式一一对应的 NBT 标签。列表元素同样使用 <see cref="NbtTag"/> 表示，
/// 复合标签按文件中的顺序保存子标签，重复键不会被静默覆盖。
/// </summary>
public sealed class NbtTag
{
    private readonly List<NbtTag> _children = [];

    public NbtTag(NbtTagType type, string name = "", object? value = null)
    {
        Type = type;
        Name = name ?? string.Empty;
        Value = value;
    }

    public NbtTagType Type { get; }

    public string Name { get; set; }

    public object? Value { get; set; }

    /// <summary><see cref="NbtTagType.List"/> 的元素类型；其他类型为 <see cref="NbtTagType.End"/>。</summary>
    public NbtTagType ListType { get; set; }

    public IReadOnlyList<NbtTag> Children => _children;

    public bool IsContainer => Type is NbtTagType.Compound or NbtTagType.List;

    public static NbtTag Compound(string name = "") => new(NbtTagType.Compound, name);

    public static NbtTag List(string name, NbtTagType elementType) =>
        new(NbtTagType.List, name) { ListType = elementType };

    public static NbtTag Of(NbtTagType type, string name, object value) => new(type, name, value);

    public NbtTag Add(NbtTag child)
    {
        if (Type != NbtTagType.Compound)
        {
            throw new InvalidOperationException("只有复合标签可以按名称添加子标签。");
        }

        _children.Add(child);
        return this;
    }

    public NbtTag Add(string name, NbtTagType type, object value) =>
        Add(new NbtTag(type, name, value));

    public void Remove(NbtTag child) => _children.Remove(child);

    public void RemoveAt(int index) => _children.RemoveAt(index);

    public void ReplaceChildren(IEnumerable<NbtTag> children)
    {
        _children.Clear();
        _children.AddRange(children);
    }

    public NbtTag AddTo(NbtTag child)
    {
        if (Type != NbtTagType.List)
        {
            throw new InvalidOperationException("只有列表标签可以添加元素。");
        }

        if (child.Type != ListType && ListType != NbtTagType.End)
        {
            throw new InvalidOperationException($"列表元素类型为 {ListType}，不能加入 {child.Type}。");
        }

        _children.Add(child);
        return this;
    }

    public NbtTag? Child(string name)
    {
        foreach (var child in _children)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>按 <c>a/b[2]/c</c> 形式访问路径；列表索引使用方括号。</summary>
    public NbtTag? Find(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return this;
        }

        var current = this;
        foreach (var rawSegment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment;
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0)
            {
                var name = segment[..bracket];
                if (name.Length > 0)
                {
                    current = current?.Child(name);
                }

                var close = segment.IndexOf(']', bracket + 1);
                if (close < 0 || current is null ||
                    !int.TryParse(segment[(bracket + 1)..close], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= current.Children.Count)
                {
                    return null;
                }

                current = current.Children[index];
                continue;
            }

            current = current?.Child(segment);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public string? GetString(string? fallback = null) => Value as string ?? fallback;

    public byte GetByte(byte fallback = 0) => Value switch
    {
        byte value => value,
        sbyte value => unchecked((byte)value),
        _ => fallback,
    };

    public short GetShort(short fallback = 0) => Value switch
    {
        short value => value,
        byte value => value,
        _ => fallback,
    };

    public int GetInt(int fallback = 0) => Value switch
    {
        int value => value,
        short value => value,
        // NBT TAG_Byte 是有符号字节；Section Y 等字段允许负数。
        byte value => unchecked((sbyte)value),
        _ => fallback,
    };

    public long GetLong(long fallback = 0) => Value switch
    {
        long value => value,
        int value => value,
        short value => value,
        byte value => unchecked((sbyte)value),
        _ => fallback,
    };

    public float GetFloat(float fallback = 0) => Value switch
    {
        float value => value,
        double value => (float)value,
        _ => fallback,
    };

    public double GetDouble(double fallback = 0) => Value switch
    {
        double value => value,
        float value => value,
        _ => fallback,
    };

    public int[] GetIntArray() => Value as int[] ?? [];

    public long[] GetLongArray() => Value as long[] ?? [];

    public byte[] GetByteArray() => Value as byte[] ?? [];

    public NbtTag DeepClone()
    {
        var clone = new NbtTag(Type, Name, CloneValue(Value))
        {
            ListType = ListType,
        };
        foreach (var child in _children)
        {
            clone._children.Add(child.DeepClone());
        }

        return clone;
    }

    /// <summary>返回可读的、稳定的路径表示，供变更跟踪与语义目录匹配使用。</summary>
    public IEnumerable<(string Path, NbtTag Tag)> Walk(string prefix = "")
    {
        var path = string.IsNullOrEmpty(prefix) ? Name : $"{prefix}/{Name}";
        yield return (path, this);
        for (var index = 0; index < _children.Count; index++)
        {
            var child = _children[index];
            var childPrefix = Type == NbtTagType.List ? $"{path}[{index}]" : path;
            foreach (var item in child.Walk(childPrefix))
            {
                yield return item;
            }
        }
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(Type).Append(" \"").Append(Name).Append('"');
        if (!IsContainer)
        {
            builder.Append(" = ").Append(FormatValue(Value));
        }

        return builder.ToString();
    }

    private static object? CloneValue(object? value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        int[] ints => ints.ToArray(),
        long[] longs => longs.ToArray(),
        _ => value,
    };

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        byte[] bytes => $"byte[{bytes.Length}]",
        int[] ints => $"int[{ints.Length}]",
        long[] longs => $"long[{longs.Length}]",
        float single => single.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "null",
    };
}

public sealed class NbtFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
