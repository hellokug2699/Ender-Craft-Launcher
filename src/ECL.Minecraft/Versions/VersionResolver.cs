// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Versions;

internal sealed class VersionResolver(IAppPaths paths, ILogger<VersionResolver> logger) : IVersionResolver
{
    public Task<Result<MinecraftVersion>> ResolveAsync(string versionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!MinecraftVersion.IsValidVersionId(versionId))
        {
            return Task.FromResult(Result<MinecraftVersion>.Fail("version.invalid_id", $"版本 ID 不合法：{versionId}"));
        }

        var path = Path.Combine(paths.VersionsDirectory, versionId, $"{versionId}.json");
        if (!File.Exists(path))
        {
            return Task.FromResult(Result<MinecraftVersion>.Fail(
                "version.missing",
                "未找到版本文件，安装功能尚未实现。",
                path));
        }

        try
        {
            var json = File.ReadAllText(path);
            var version = JsonSerializer.Deserialize<MinecraftVersion>(json, EclJson.Default);
            if (version is null)
            {
                return Task.FromResult(Result<MinecraftVersion>.Fail("version.empty", "版本文件内容为空。", path));
            }

            if (string.IsNullOrWhiteSpace(version.Id))
            {
                version.Id = versionId;
            }

            logger.LogDebug("已读取版本 {VersionId}：mainClass={MainClass}", versionId, version.MainClass);
            return Task.FromResult(Result<MinecraftVersion>.Ok(version));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "读取版本文件失败：{Path}", path);
            return Task.FromResult(Result<MinecraftVersion>.Fail("version.invalid", "版本文件无法解析。", exception.Message));
        }
    }
}
