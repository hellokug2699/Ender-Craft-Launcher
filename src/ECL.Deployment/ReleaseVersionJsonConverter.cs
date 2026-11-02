// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecl.Deployment;

/// <summary>把 <see cref="ReleaseVersion"/> 以字符串形式持久化，避免状态文件依赖内部构造函数。</summary>
public sealed class ReleaseVersionJsonConverter : JsonConverter<ReleaseVersion>
{
    public override ReleaseVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return ReleaseVersion.TryParse(text, out var version)
            ? version
            : throw new JsonException($"无效的版本号：{text}");
    }

    public override void Write(Utf8JsonWriter writer, ReleaseVersion value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
