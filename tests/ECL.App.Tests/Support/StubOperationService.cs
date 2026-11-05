// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Operations;

namespace Ecl.App.Tests.Support;

/// <summary>只读取端的任务中心实现；可按需注入任务快照，用于验证冲突检测。</summary>
internal sealed class StubOperationService(IReadOnlyList<OperationSnapshot>? snapshots = null) : IOperationService
{
    private readonly StubOperationStore _store = new(snapshots ?? []);

    public IOperationStore Store => _store;

    public IOperationHandle Begin(OperationStart start) => throw new NotSupportedException();

    public bool AttachControl(Guid operationId, IOperationControl control) => false;

    public bool TryCancel(Guid operationId) => false;

    public Task<bool> RetryAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public bool Remove(Guid operationId) => false;

    public int PruneCompleted(DateTimeOffset completedBefore) => 0;

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
