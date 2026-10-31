// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Auth;

internal sealed class JsonAuthServerStore(IAppPaths paths, ILogger<JsonAuthServerStore> logger) : IAuthServerStore
{
    public AuthServersDocument Load()
    {
        var path = paths.AuthServersFile;
        if (!File.Exists(path))
        {
            return new AuthServersDocument();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<AuthServersDocument>(stream, EclJson.Default) ?? new AuthServersDocument();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "读取认证服务器文件失败：{Path}", path);
            return new AuthServersDocument();
        }
    }

    public void Save(AuthServersDocument document)
    {
        var path = paths.AuthServersFile;
        Directory.CreateDirectory(paths.AccountsDirectory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, EclJson.Indented));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
