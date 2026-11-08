// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using Ecl.Core;

namespace Ecl.Configuration.Settings;

/// <summary>启动早期读取少量全局配置（例如语言），此时 DI 容器尚未建立。</summary>
public static class SettingsBootstrap
{
    /// <summary>
    /// 界面语言设置的语义解析：未设置、空值或 <c>system</c> 表示“跟随系统首选语言”
    /// （settings-interface-preferences.md §5）。已保存的具体区域性名称原样返回，
    /// 不受支持的名称仍由本地化器回退到 ECL 默认语言。
    /// </summary>
    public static string ResolveLanguage(string? configured, CultureInfo systemCulture)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);

        return string.IsNullOrWhiteSpace(configured) ||
               string.Equals(configured, "system", StringComparison.OrdinalIgnoreCase)
            ? systemCulture.Name
            : configured;
    }

    public static string? ReadGlobalString(AppPaths paths, string key)
    {
        if (!File.Exists(paths.GlobalSettingsFile))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(paths.GlobalSettingsFile));
            if (!document.RootElement.TryGetProperty("values", out var values) ||
                !values.TryGetProperty(key, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
