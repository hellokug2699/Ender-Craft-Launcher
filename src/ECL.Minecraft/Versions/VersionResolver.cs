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

        var chain = new List<MinecraftVersion>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentId = versionId;

        while (true)
        {
            if (!visited.Add(currentId))
            {
                return Task.FromResult(Result<MinecraftVersion>.Fail(
                    "version.inheritance_cycle",
                    "版本继承链存在循环。",
                    currentId));
            }

            var read = ReadVersionFile(currentId);
            if (!read.IsSuccess)
            {
                return Task.FromResult(Result<MinecraftVersion>.Fail(read.Error!));
            }

            var version = read.Value!;
            chain.Add(version);

            if (string.IsNullOrWhiteSpace(version.InheritsFrom))
            {
                break;
            }

            if (!MinecraftVersion.IsValidVersionId(version.InheritsFrom))
            {
                return Task.FromResult(Result<MinecraftVersion>.Fail(
                    "version.invalid_parent",
                    $"父版本 ID 不合法：{version.InheritsFrom}"));
            }

            currentId = version.InheritsFrom;
        }

        var merged = chain[^1];
        for (var index = chain.Count - 2; index >= 0; index--)
        {
            merged = VersionMerger.Merge(merged, chain[index]);
        }

        // client jar 属于继承链中最先声明 downloads.client 的版本（合并时 Downloads 也取最近的一层）。
        // 外部启动器安装的加载器版本通常不声明 downloads，jar 由被继承的原版版本提供；
        // 启动器自建实例由安装流程把 jar 放在实例版本目录，具体选择见 VersionFilePlanner.ResolveClientJarPath。
        merged.ClientJarVersionId = chain
            .FirstOrDefault(version => version.Downloads?.Client is not null)
            ?.Id ?? chain[^1].Id;

        logger.LogDebug(
            "已解析版本 {VersionId}：mainClass={MainClass}，libraries={LibraryCount}",
            versionId,
            merged.MainClass,
            merged.Libraries.Count);

        return Task.FromResult(Result<MinecraftVersion>.Ok(merged));
    }

    private Result<MinecraftVersion> ReadVersionFile(string versionId)
    {
        var path = VersionFilePlanner.GetVersionJsonPath(paths, versionId);
        if (!File.Exists(path))
        {
            return Result<MinecraftVersion>.Fail(
                "version.missing",
                "未找到版本文件。",
                path);
        }

        try
        {
            var json = File.ReadAllText(path);
            var version = JsonSerializer.Deserialize<MinecraftVersion>(json, EclJson.Default);
            if (version is null)
            {
                return Result<MinecraftVersion>.Fail("version.empty", "版本文件内容为空。", path);
            }

            if (string.IsNullOrWhiteSpace(version.Id))
            {
                version.Id = versionId;
            }

            return Result<MinecraftVersion>.Ok(version);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "读取版本文件失败：{Path}", path);
            return Result<MinecraftVersion>.Fail("version.invalid", "版本文件无法解析。", exception.Message);
        }
    }
}
