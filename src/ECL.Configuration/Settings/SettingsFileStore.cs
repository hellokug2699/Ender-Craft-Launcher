// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Configuration.Settings;

public sealed class SettingsFileStore(ILogger<SettingsFileStore> logger)
{
    public SettingsDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SettingsDocument();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<SettingsDocument>(stream, EclJson.Default) ?? new SettingsDocument();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "读取配置文件失败，将使用默认值：{Path}", path);
            return new SettingsDocument();
        }
    }

    public void Save(string path, SettingsDocument document)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, EclJson.Indented));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
