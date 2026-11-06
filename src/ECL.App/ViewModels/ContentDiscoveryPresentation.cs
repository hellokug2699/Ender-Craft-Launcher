// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Services;
using Ecl.Core.Localization;
using Ecl.Minecraft.Content;
using Ecl.Minecraft.Mods;

namespace Ecl.App.ViewModels;

/// <summary>
/// 下载中心内容发现页的展示映射（`download-mods.md` / `download-modpacks.md` /
/// `download-resource-template.md`）：版本范围、加载器、文件大小与筛选标题。
/// 只格式化领域已经给出的真实字段，缺失时显示「未知 / 大小未知」，不做推断。
/// </summary>
public static class ContentDiscoveryPresentation
{
    /// <summary>非 ASCII 输入（例如中文）不是有效的英文搜索条件。</summary>
    public static bool HasNonAsciiInput(string? value) =>
        !string.IsNullOrEmpty(value) && value.Any(character => character > 0x7F);

    public static string FormatVersionRange(IReadOnlyList<string> versions, ILocalizer localizer)
    {
        var distinct = versions
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinct.Length == 0)
        {
            return localizer.Get("downloads.discovery.unknown");
        }

        if (distinct.Length == 1)
        {
            return distinct[0];
        }

        var ordered = distinct.OrderBy(version => version, VersionTextComparer.Instance).ToArray();
        return $"{ordered[0]} – {ordered[^1]}";
    }

    public static string FormatLoaders(ContentHit hit, ILocalizer localizer)
    {
        if (hit.Loaders.Count == 0)
        {
            return localizer.Get("downloads.discovery.unknown");
        }

        var names = hit.Loaders
            .Select(ContentLoaders.DisplayName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 0 ? localizer.Get("downloads.discovery.unknown") : string.Join(" / ", names);
    }

    /// <summary>光影包只展示来源提供的光影组件标签（iris / optifine）；没有时明确未知。</summary>
    public static string FormatShaderApplicability(ContentHit hit, ILocalizer localizer)
    {
        var tags = hit.Tags
            .Where(tag => string.Equals(tag, "iris", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(tag, "optifine", StringComparison.OrdinalIgnoreCase))
            .Select(tag => tag.ToLowerInvariant() switch
            {
                "iris" => "Iris",
                "optifine" => "OptiFine",
                _ => tag,
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return tags.Length == 0 ? localizer.Get("downloads.discovery.unknown") : string.Join(" / ", tags);
    }

    public static string FormatSize(long? bytes, ILocalizer localizer) =>
        bytes is > 0 ? StorageSizeFormatter.FormatBytes(bytes.Value) : localizer.Get("downloads.deploy.sizeUnknown");

    public static string FormatDate(DateTimeOffset? published, ILocalizer localizer) =>
        published is { } value
            ? value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : localizer.Get("downloads.discovery.unknown");

    /// <summary>版本弹窗上表的标题：只按真实筛选条件拼接，加载器为“无”时不出现加载器类。</summary>
    public static string FormatFilterTitle(string gameVersion, string? loaderId, ILocalizer localizer)
    {
        if (string.IsNullOrWhiteSpace(loaderId))
        {
            return localizer.Get("downloads.discovery.versionsFiltered", gameVersion);
        }

        var loaderName = loaderId.Trim().ToLowerInvariant() switch
        {
            "forge" => localizer.Get("downloads.discovery.loader.forge"),
            "fabric" => localizer.Get("downloads.discovery.loader.fabric"),
            "neoforge" => localizer.Get("downloads.discovery.loader.neoforge"),
            _ => ContentLoaders.DisplayName(loaderId),
        };
        return localizer.Get("downloads.discovery.versionsFilteredLoader", gameVersion, loaderName);
    }

    public static string FormatInstanceMeta(string gameVersion, string? loader, ILocalizer localizer)
    {
        var loaderText = string.IsNullOrWhiteSpace(loader) || string.Equals(loader, "vanilla", StringComparison.OrdinalIgnoreCase)
            ? localizer.Get("instances.loader.vanilla")
            : loader.Trim().ToLowerInvariant() switch
            {
                "fabric" => localizer.Get("instances.loader.fabric"),
                "forge" => localizer.Get("instances.loader.forge"),
                "neoforge" => localizer.Get("instances.loader.neoforge"),
                "cleanroom" => localizer.Get("instances.loader.cleanroom"),
                "quilt" => "Quilt",
                _ => localizer.Get("downloads.discovery.unknown"),
            };
        return $"{gameVersion} · {loaderText}";
    }

    public static string FormatShaderSupportState(ShaderSupportState state, ILocalizer localizer) =>
        state switch
        {
            ShaderSupportState.Supported => localizer.Get("downloads.discovery.shaderState.supported"),
            ShaderSupportState.Missing => localizer.Get("downloads.discovery.shaderState.missing"),
            ShaderSupportState.Incomplete => localizer.Get("downloads.discovery.shaderState.incomplete"),
            ShaderSupportState.Incompatible => localizer.Get("downloads.discovery.shaderState.incompatible"),
            ShaderSupportState.Conflict => localizer.Get("downloads.discovery.shaderState.conflict"),
            _ => localizer.Get("downloads.discovery.shaderState.unknown"),
        };
}

/// <summary>
/// Minecraft 版本文本比较：逐段按数字比较（1.9 &lt; 1.10），非数字段按序号比较；
/// 只用于“版本范围”的上下界展示，不参与兼容性判断。
/// </summary>
public sealed class VersionTextComparer : IComparer<string>
{
    public static VersionTextComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var left = x.Split('.');
        var right = y.Split('.');
        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var leftPart = index < left.Length ? left[index] : string.Empty;
            var rightPart = index < right.Length ? right[index] : string.Empty;
            int result;
            if (long.TryParse(leftPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leftNumber) &&
                long.TryParse(rightPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightNumber))
            {
                result = leftNumber.CompareTo(rightNumber);
            }
            else
            {
                result = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
