// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;

namespace Ecl.Configuration.Settings;

/// <summary>启动早期读取少量全局配置（例如语言），此时 DI 容器尚未建立。</summary>
public static class SettingsBootstrap
{
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
