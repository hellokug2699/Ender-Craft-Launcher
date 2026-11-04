// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Storage;
using Xunit;

namespace Ecl.Tests.Storage;

public sealed class WorkspaceBootstrapTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly string _workspace;
    private readonly AppPaths _paths;

    public WorkspaceBootstrapTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "ecl-ws-bootstrap", Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(Path.GetTempPath(), "ecl-ws-bootstrap-games", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dataRoot);
        _paths.EnsureCreated();
    }

    [Fact]
    public void MigratesLegacyMinecraftDirectoryOnce()
    {
        Directory.CreateDirectory(_workspace);

        var state = WorkspaceBootstrap.Apply(_paths, _workspace);

        Assert.NotNull(state.PrimaryId);
        Assert.Equal(_workspace, _paths.GameRoot);
        Assert.True(_paths.WorkspaceConfigured);
        Assert.True(File.Exists(Path.Combine(_dataRoot, "config", "workspace.json")));
    }

    [Fact]
    public void DoesNotAdoptLegacyValueThatPointsAtDataDirectory()
    {
        var state = WorkspaceBootstrap.Apply(_paths, _dataRoot);

        Assert.Null(state.PrimaryId);
        Assert.False(_paths.WorkspaceConfigured);
        Assert.Equal(_dataRoot, _paths.GameRoot);
    }

    [Fact]
    public void LeavesUnconfiguredWithoutSavedOrLegacyWorkspace()
    {
        var state = WorkspaceBootstrap.Apply(_paths, null);

        Assert.Empty(state.Roots);
        Assert.False(_paths.WorkspaceConfigured);
    }

    [Fact]
    public async Task AppliesSavedPrimaryOnNextStart()
    {
        var service = new WorkspaceService(_paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceService>.Instance);
        Assert.True((await service.InitializeAsync(_workspace)).IsSuccess);

        var restarted = new AppPaths(_dataRoot);
        var state = WorkspaceBootstrap.Apply(restarted, null);

        Assert.NotNull(state.PrimaryId);
        Assert.Equal(_workspace, restarted.GameRoot);
        Assert.True(restarted.WorkspaceConfigured);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _dataRoot, _workspace })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响测试结论。
            }
        }
    }
}
