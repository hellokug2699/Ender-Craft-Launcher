// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Install;

public enum InstallStage
{
    Manifest,
    Version,
    Loader,
    Processors,
    Client,
    Libraries,
    Assets,
    Logging,
    FabricApi,
    OptiFine,
    Completed,
}

public sealed record InstallProgress(
    InstallStage Stage,
    string Message,
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string? CurrentFile)
{
    public bool IsIndeterminate => TotalFiles == 0;

    public double Percent => TotalFiles > 0
        ? Math.Clamp(CompletedFiles * 100.0 / TotalFiles, 0, 100)
        : 0;

    public double BytesPerSecond { get; init; }

    public bool IsPaused { get; init; }
}

public sealed record InstallReport(
    string VersionId,
    int DownloadedFiles,
    int SkippedFiles,
    long Bytes,
    TimeSpan Duration,
    IReadOnlyList<string>? Components = null);

/// <summary>
/// 安装事务的回滚结果（`instance-modify.md` §7）。<see cref="Actions"/> 是完整的人类可读记录，
/// 写入诊断文件；<see cref="Failures"/> 只包含没有完成的回滚动作，供领域层判断是否需要恢复。
/// </summary>
public sealed record InstallRollbackResult(
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Failures)
{
    public static InstallRollbackResult Empty { get; } = new([], []);

    public bool IsComplete => Failures.Count == 0;
}

/// <summary>
/// 安装失败的真实结果：除错误本身外，只有这里能证明的字段才可以对用户宣称。
/// 回滚没有完成或实例元数据没有还原时，调用方必须按“需要恢复”处理，
/// 不得用“元数据已恢复”或诊断文件内容冒充文件级回滚成功。
/// </summary>
public sealed record InstallFailure(
    EclError Error,
    InstallRollbackResult Rollback,
    bool MetadataRestored,
    string? DiagnosticPath)
{
    public bool RecoveryRequired => !MetadataRestored || !Rollback.IsComplete;
}

/// <summary>
/// 安装结果：成功时携带 <see cref="InstallReport"/>，失败时携带可验证的
/// <see cref="InstallFailure"/>。失败信息必须与事务真实结果一致，不得事后推断。
/// </summary>
public sealed record InstallOutcome
{
    private InstallOutcome(InstallReport? report, InstallFailure? failure)
    {
        Report = report;
        Failure = failure;
    }

    public InstallReport? Report { get; }

    public InstallFailure? Failure { get; }

    public bool IsSuccess => Failure is null;

    public EclError? Error => Failure?.Error;

    public static InstallOutcome Ok(InstallReport report) => new(report, null);

    public static InstallOutcome Fail(InstallFailure failure) => new(null, failure);
}

public interface IVersionInstallService
{
    Task<Result<InstallReport>> InstallAsync(
        string instanceId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 变更实例游戏 / 加载器配置并安装目标版本；失败时恢复原实例元数据。
    /// 与 <see cref="InstallAsync"/> 不同，失败结果携带回滚是否完整、元数据是否还原与诊断文件位置，
    /// 调用方据此区分“失败且已回滚”和“失败且需要恢复”。
    /// </summary>
    Task<InstallOutcome> ReconfigureAsync(
        string instanceId,
        string gameVersion,
        string loader,
        string? loaderVersion = null,
        string? fabricApiVersion = null,
        string? optiFineVersion = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
