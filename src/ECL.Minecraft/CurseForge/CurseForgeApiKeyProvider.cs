// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;

namespace Ecl.Minecraft.CurseForge;

public interface ICurseForgeApiKeyProvider
{
    CurseForgeStatus Status { get; }

    string? GetApiKey();
}

/// <summary>
/// CurseForge API key 只从进程环境变量或构建时写入的程序集元数据读取。
/// public 仓库的源码与配置文件永远不会包含 key；release 构建由 Core 仓库的材料注入。
/// </summary>
public sealed class CurseForgeApiKeyProvider : ICurseForgeApiKeyProvider
{
    public const string EnvironmentVariableName = "ECL_CURSEFORGE_API_KEY";
    public const string MetadataKey = "Ecl.CurseForge.ApiKey";

    public CurseForgeApiKeyProvider()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName)?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            ApiKey = fromEnvironment;
            Status = new CurseForgeStatus(CurseForgeKeySource.Environment);
            return;
        }

        var fromMetadata = typeof(CurseForgeApiKeyProvider).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, MetadataKey, StringComparison.Ordinal))
            ?.Value
            ?.Trim();
        if (!string.IsNullOrWhiteSpace(fromMetadata))
        {
            ApiKey = fromMetadata;
            Status = new CurseForgeStatus(CurseForgeKeySource.Embedded);
            return;
        }

        Status = new CurseForgeStatus(CurseForgeKeySource.None);
    }

    public CurseForgeStatus Status { get; }

    public string? ApiKey { get; }

    public string? GetApiKey() => ApiKey;
}
