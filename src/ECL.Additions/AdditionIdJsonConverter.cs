// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecl.Additions;

/// <summary>以字符串形式持久化 Addition ID，避免只读结构在反序列化时丢失值。</summary>
public sealed class AdditionIdJsonConverter : JsonConverter<AdditionId>
{
    public override AdditionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return AdditionId.TryParse(text, out var id)
            ? id
            : throw new JsonException($"无效的 Addition ID：{text}");
    }

    public override void Write(Utf8JsonWriter writer, AdditionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
