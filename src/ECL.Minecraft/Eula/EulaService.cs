// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Eula;

public sealed record EulaConsent(DateTimeOffset AcceptedAt, DateTimeOffset MultiplayerNoticeShownAt);

public interface IEulaService
{
    bool HasAccepted { get; }

    EulaConsent? GetConsent();

    Result Accept();
}

public sealed class FileEulaService(IAppPaths paths, ILogger<FileEulaService> logger) : IEulaService
{
    private readonly string _file = Path.Combine(paths.ConfigDirectory, "eula.json");

    public bool HasAccepted => GetConsent() is not null;

    public EulaConsent? GetConsent()
    {
        if (!File.Exists(_file))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_file);
            return JsonSerializer.Deserialize<EulaConsent>(json, EclJson.Default);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "读取 EULA 同意记录失败：{Path}", _file);
            return null;
        }
    }

    public Result Accept()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var consent = new EulaConsent(now, now);
            var directory = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = $"{_file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(consent, EclJson.Indented));
            File.Move(temporary, _file, overwrite: true);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "保存 EULA 同意记录失败：{Path}", _file);
            return Result.Fail("eula.write_failed", "无法保存 EULA 同意记录。", exception.Message);
        }
    }
}
