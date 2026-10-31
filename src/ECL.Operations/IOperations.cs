// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Operations;

/// <summary>
/// 任务写入端：业务服务通过它登记并更新长时间任务。
/// 现有 <c>IProgress&lt;T&gt;</c> API 仍然保留，Operation 只做统一呈现。
/// </summary>
public interface IOperationService
{
    IOperationStore Store { get; }

    /// <summary>登记一个任务并开始接收进度。</summary>
    IOperationHandle Begin(OperationStart start);

    /// <summary>为任务绑定取消/重试能力；任务结束后自动移除。</summary>
    bool AttachControl(Guid operationId, IOperationControl control);

    /// <summary>请求取消正在运行的任务；未绑定控制或任务已结束时返回 false。</summary>
    bool TryCancel(Guid operationId);

    /// <summary>
    /// 重新执行一个失败的任务。重试工厂由任务提供者注册，通常会在内部登记新的 Operation；
    /// 未提供重试能力或任务不在失败状态时返回 false。
    /// </summary>
    Task<bool> RetryAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>移除一个已结束的任务记录。</summary>
    bool Remove(Guid operationId);

    /// <summary>清理在指定时间之前结束的任务，返回移除数量。</summary>
    int PruneCompleted(DateTimeOffset completedBefore);
}

/// <summary>
/// 任务提供者实现的取消/重试能力。Operation 层只负责转交请求，
/// 具体语义（取消下载、重跑安装等）由提供者决定。
/// </summary>
public interface IOperationControl
{
    /// <summary>请求取消；任务已不可取消时返回 false。</summary>
    bool TryCancel();

    /// <summary>失败后重新执行；为 null 表示该任务不支持重试。</summary>
    Func<CancellationToken, Task>? Retry { get; }
}

/// <summary>任务读取端：UI / CLI 只依赖这个接口，便于以后替换为持久化实现。</summary>
public interface IOperationStore
{
    /// <summary>所有任务快照，按开始时间倒序。</summary>
    IReadOnlyList<OperationSnapshot> Snapshots { get; }

    OperationSnapshot? TryGet(Guid operationId);

    event EventHandler<OperationSnapshot>? Changed;
}

/// <summary>
/// 单个任务的写入句柄。方法线程安全；<see cref="IDisposable.Dispose"/> 时仍未结束的任务会标记为取消。
/// </summary>
public interface IOperationHandle : IDisposable
{
    Guid Id { get; }

    OperationSnapshot Snapshot { get; }

    void Report(OperationProgressUpdate update);

    void SetStage(string stageId, string? stageText = null);

    /// <summary>进入重试状态；后续进度会把任务带回运行中。</summary>
    void Retrying(int attempt, int maxAttempts, string? reason = null);

    void Complete(OperationProgressUpdate? finalUpdate = null);

    void Fail(OperationError error);

    void Cancel();

    void Pause();

    void Resume();
}
