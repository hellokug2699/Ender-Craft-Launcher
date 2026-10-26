// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Auth;

internal sealed class JsonAccountStore(IAppPaths paths, ILogger<JsonAccountStore> logger) : IAccountStore
{
    public AccountsDocument Load()
    {
        var path = paths.AccountsFile;
        if (!File.Exists(path))
        {
            return new AccountsDocument();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<AccountsDocument>(stream, EclJson.Default) ?? new AccountsDocument();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "读取账户文件失败：{Path}", path);
            return new AccountsDocument();
        }
    }

    public void Save(AccountsDocument document)
    {
        var path = paths.AccountsFile;
        Directory.CreateDirectory(paths.AccountsDirectory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, EclJson.Indented));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
