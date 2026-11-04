// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Operations;

namespace Ecl.App.Tests.Support;

/// <summary>输出面板测试只需要任务中心的读取端；这里提供最小只读实现。</summary>
internal sealed class StubOperationService : IOperationService
{
    private readonly StubOperationStore _store = new();

    public IOperationStore Store => _store;

    public IOperationHandle Begin(OperationStart start) => throw new NotSupportedException();

    public bool AttachControl(Guid operationId, IOperationControl control) => false;

    public bool TryCancel(Guid operationId) => false;

    public Task<bool> RetryAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public bool Remove(Guid operationId) => false;

    public int PruneCompleted(DateTimeOffset completedBefore) => 0;

    private sealed class StubOperationStore : IOperationStore
    {
        public IReadOnlyList<OperationSnapshot> Snapshots => [];

        public event EventHandler<OperationSnapshot>? Changed
        {
            add { }
            remove { }
        }

        public OperationSnapshot? TryGet(Guid operationId) => null;
    }
}
