// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Wardrobe.Appearance;

namespace Ecl.Wardrobe.Authlib;

/// <summary>
/// Authlib-Injector 的 <c>uploadableTextures</c> 能力声明。
/// <para>
/// 规范：该属性是角色资料 properties 中的一项，值为逗号分隔的 <c>skin</c>/<c>cape</c>。
/// 属性缺失、为空或不包含对应值时，绝不能向用户宣称对应材质可上传。
/// </para>
/// </summary>
public sealed record AuthlibTextureCapabilities
{
    public required AppearanceCapabilityStatus Skin { get; init; }

    public required AppearanceCapabilityStatus Cape { get; init; }

    /// <summary>解析/判定依据，便于向用户解释为什么某项能力不可用。</summary>
    public string Reason { get; init; } = string.Empty;

    public bool CanUploadSkin => Skin == AppearanceCapabilityStatus.Declared;

    public bool CanUploadCape => Cape == AppearanceCapabilityStatus.Declared;

    public bool CanUpload(WardrobeTextureType textureType) => textureType switch
    {
        WardrobeTextureType.Skin => CanUploadSkin,
        WardrobeTextureType.Cape => CanUploadCape,
        _ => false,
    };

    public static AuthlibTextureCapabilities Unknown(string reason) => new()
    {
        Skin = AppearanceCapabilityStatus.Unknown,
        Cape = AppearanceCapabilityStatus.Unknown,
        Reason = reason,
    };

    /// <summary>
    /// 从角色资料 JSON 解析能力。只读取 properties 中名为 <c>uploadableTextures</c> 的属性，
    /// 不根据服务器域名、名称或“是否使用 Authlib-Injector”做推断。
    /// </summary>
    public static Result<AuthlibTextureCapabilities> Parse(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return Result<AuthlibTextureCapabilities>.Fail(
                "authlib.profile_empty",
                "角色资料为空，无法判断材质上传能力。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(profileJson);
        }
        catch (JsonException)
        {
            return Result<AuthlibTextureCapabilities>.Fail(
                "authlib.profile_invalid",
                "角色资料不是有效的 JSON，无法判断材质上传能力。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Result<AuthlibTextureCapabilities>.Fail(
                    "authlib.profile_invalid",
                    "角色资料根节点不是 JSON 对象。");
            }

            if (!root.TryGetProperty("properties", out var properties) ||
                properties.ValueKind == JsonValueKind.Null)
            {
                return Result<AuthlibTextureCapabilities>.Ok(new AuthlibTextureCapabilities
                {
                    Skin = AppearanceCapabilityStatus.Unavailable,
                    Cape = AppearanceCapabilityStatus.Unavailable,
                    Reason = "角色资料没有 uploadableTextures 属性，服务端未声明任何可上传的材质。",
                });
            }

            if (properties.ValueKind != JsonValueKind.Array)
            {
                return Result<AuthlibTextureCapabilities>.Ok(Unknown(
                    "角色资料的 properties 不是数组，无法判断材质上传能力。"));
            }

            JsonElement? uploadable = null;
            foreach (var property in properties.EnumerateArray())
            {
                if (property.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (string.Equals(ReadString(property, "name"), "uploadableTextures", StringComparison.Ordinal))
                {
                    uploadable = property;
                    break;
                }
            }

            if (uploadable is null)
            {
                return Result<AuthlibTextureCapabilities>.Ok(new AuthlibTextureCapabilities
                {
                    Skin = AppearanceCapabilityStatus.Unavailable,
                    Cape = AppearanceCapabilityStatus.Unavailable,
                    Reason = "角色资料没有 uploadableTextures 属性，服务端未声明任何可上传的材质。",
                });
            }

            var value = ReadString(uploadable.Value, "value");
            if (value is null)
            {
                return Result<AuthlibTextureCapabilities>.Ok(Unknown(
                    "uploadableTextures 属性没有字符串值，无法判断材质上传能力。"));
            }

            var declared = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);

            var skin = declared.Contains("skin")
                ? AppearanceCapabilityStatus.Declared
                : AppearanceCapabilityStatus.Unavailable;
            var cape = declared.Contains("cape")
                ? AppearanceCapabilityStatus.Declared
                : AppearanceCapabilityStatus.Unavailable;

            var reason = declared.Count == 0
                ? "uploadableTextures 为空，服务端未声明任何可上传的材质。"
                : $"uploadableTextures = \"{string.Join(",", declared)}\"，按声明分别开放对应能力。";

            return Result<AuthlibTextureCapabilities>.Ok(new AuthlibTextureCapabilities
            {
                Skin = skin,
                Cape = cape,
                Reason = reason,
            });
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
