// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Instances;

public interface IInstanceService
{
    Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken cancellationToken = default);

    Task<GameInstance?> GetAsync(string instanceId, CancellationToken cancellationToken = default);

    Task<Result<GameInstance>> CreateAsync(
        string name,
        string gameVersion,
        string loader = "vanilla",
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        CancellationToken cancellationToken = default);

    Task<Result<GameInstance>> UpdateConfigurationAsync(
        string instanceId,
        string gameVersion,
        string loader,
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(string instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 生成删除预览：列出启动器记录、ECL 托管游戏数据与外部/共享目录，
    /// 并标明哪些路径可以删除。UI 与 CLI 必须先展示该计划。
    /// </summary>
    Task<Result<InstanceDeletionPlan>> GetDeletionPlanAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>按结构化选项删除实例。外部与共享游戏目录永远不会被递归删除。</summary>
    Task<Result> DeleteAsync(
        string instanceId,
        InstanceDeleteOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 扫描游戏目录下已有的 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>，返回可导入的版本候选。
    /// 用于识别其它启动器留下的安装，不复制文件。
    /// </summary>
    Task<IReadOnlyList<DetectedVersion>> ScanVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>把扫描到的既有版本导入为实例：直接引用原游戏目录。</summary>
    Task<Result<GameInstance>> ImportAsync(
        string versionId,
        string? name = null,
        CancellationToken cancellationToken = default);

    Task TouchLastPlayedAsync(string instanceId, CancellationToken cancellationToken = default);
}
