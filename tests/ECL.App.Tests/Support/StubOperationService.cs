// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Operations;

namespace Ecl.App.Tests.Support;

/// <summary>
/// 只读取端的任务中心实现；可按需注入任务快照，用于验证冲突检测。
/// <see cref="Begin"/> 返回一个不落库的空句柄，便于测试进度桥接而不产生任务记录。
/// </summary>
internal sealed class StubOperationService(IReadOnlyList<OperationSnapshot>? snapshots = null) : IOperationService
{
    private readonly StubOperationStore _store = new(snapshots ?? []);

    public IOperationStore Store => _store;

    public IOperationHandle Begin(OperationStart start) => new NoopHandle();

    public bool AttachControl(Guid operationId, IOperationControl control) => false;

    public bool TryCancel(Guid operationId) => false;

    public Task<bool> RetryAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public bool Remove(Guid operationId) => false;

    public int PruneCompleted(DateTimeOffset completedBefore) => 0;

    private sealed class NoopHandle : IOperationHandle
    {
        public Guid Id { get; } = Guid.NewGuid();

        public OperationSnapshot Snapshot => new()
        {
            Id = Id,
            Kind = OperationKind.Generic,
            Title = "stub",
            State = OperationState.Running,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        public void Report(OperationProgressUpdate update)
        {
        }

        public void SetStage(string stageId, string? stageText = null)
        {
        }

        public void Retrying(int attempt, int maxAttempts, string? reason = null)
        {
        }

        public void Complete(OperationProgressUpdate? finalUpdate = null)
        {
        }

        public void Fail(OperationError error)
        {
        }

        public void Cancel()
        {
        }

        public void Pause()
        {
        }

        public void Resume()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubOperationStore(IReadOnlyList<OperationSnapshot> snapshots) : IOperationStore
    {
        public IReadOnlyList<OperationSnapshot> Snapshots => snapshots;

        public event EventHandler<OperationSnapshot>? Changed
        {
            add { }
            remove { }
        }

        public OperationSnapshot? TryGet(Guid operationId) => null;
    }
}
