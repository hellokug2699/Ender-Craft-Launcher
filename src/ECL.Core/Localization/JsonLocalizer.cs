// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;

namespace Ecl.Core.Localization;

/// <summary>
/// 基于嵌入式 JSON 的轻量本地化。新增语言只需增加 locales/&lt;culture&gt;.json。
/// 不支持的语言会回退到 en-US；缺失的键会回退为键本身，便于发现遗漏。
/// </summary>
public sealed class JsonLocalizer : ILocalizer
{
    private static readonly CultureInfo[] SupportedCultures =
    [
        CultureInfo.GetCultureInfo("zh-CN"),
        CultureInfo.GetCultureInfo("en-US"),
    ];

    private readonly Dictionary<string, string> _messages;
    private readonly Dictionary<string, string> _fallbackMessages;

    public CultureInfo Culture { get; }

    public IReadOnlyList<CultureInfo> AvailableCultures => SupportedCultures;

    public JsonLocalizer(CultureInfo? culture = null)
    {
        Culture = Normalize(culture ?? CultureInfo.CurrentUICulture);
        _messages = LoadMessages(Culture.Name);
        _fallbackMessages = Culture.Name == "en-US" ? _messages : LoadMessages("en-US");
    }

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        if (_messages.TryGetValue(key, out var value))
        {
            return value;
        }

        return _fallbackMessages.TryGetValue(key, out var fallback) ? fallback : key;
    }

    public string Get(string key, params object?[] arguments)
    {
        var template = Get(key);
        if (arguments.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(Culture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public static CultureInfo Normalize(CultureInfo culture)
    {
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedCultures[0];
        }

        return SupportedCultures[1];
    }

    private static Dictionary<string, string> LoadMessages(string cultureName)
    {
        var assembly = typeof(JsonLocalizer).Assembly;
        var suffix = $".Locales.{cultureName}.json";
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var messages = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            return messages is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(messages, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
