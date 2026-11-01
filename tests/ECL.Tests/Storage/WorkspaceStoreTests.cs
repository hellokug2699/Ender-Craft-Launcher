// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Storage;
using Xunit;

namespace Ecl.Tests.Storage;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly WorkspaceStore _store;

    public WorkspaceStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ecl-ws-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new WorkspaceStore(_directory);
    }

    [Fact]
    public void RoundTripsStateAtomically()
    {
        var root = new WorkspaceRoot(
            "ws-1",
            "Games",
            Path.Combine(_directory, "games"),
            WorkspaceKind.Managed,
            WorkspaceStatus.Ready,
            DateTimeOffset.UtcNow);
        var state = new WorkspaceFile
        {
            PrimaryId = root.Id,
            Roots = [root],
            OnboardingCompletedAtUtc = DateTimeOffset.UtcNow,
        };

        _store.Save(state);
        var loaded = _store.Load(out var error);

        Assert.Null(error);
        Assert.Equal(root.Id, loaded.PrimaryId);
        Assert.Single(loaded.Roots);
        Assert.Equal(root.Path, loaded.Roots[0].Path);
        Assert.Equal(WorkspaceStatus.Ready, loaded.Roots[0].Status);
    }

    [Fact]
    public void CorruptFileIsReportedButNotDeleted()
    {
        var path = Path.Combine(_directory, "workspace.json");
        File.WriteAllText(path, "{ not json");

        var loaded = _store.Load(out var error);

        Assert.NotNull(error);
        Assert.Empty(loaded.Roots);
        Assert.True(File.Exists(path));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void NewerSchemaIsRejectedInsteadOfPartiallyApplied()
    {
        var path = Path.Combine(_directory, "workspace.json");
        File.WriteAllText(path, """{ "schemaVersion": 99, "primaryId": "ws-1", "roots": [] }""");

        var loaded = _store.Load(out var error);

        Assert.NotNull(error);
        Assert.Null(loaded.PrimaryId);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}
