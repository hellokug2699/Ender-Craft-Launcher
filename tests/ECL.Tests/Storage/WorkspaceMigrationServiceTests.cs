// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Storage;

public sealed class WorkspaceMigrationServiceTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly string _source;
    private readonly string _target;
    private readonly AppPaths _paths;

    public WorkspaceMigrationServiceTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-ws-migrate", Guid.NewGuid().ToString("N"));
        _dataRoot = Path.Combine(root, "data");
        _source = Path.Combine(root, "games");
        _target = Path.Combine(root, "games-moved");
        _paths = new AppPaths(_dataRoot);
        _paths.EnsureCreated();
    }

    private (WorkspaceService Workspace, WorkspaceMigrationService Migration) CreateServices()
    {
        var workspace = new WorkspaceService(_paths, NullLogger<WorkspaceService>.Instance);
        var migration = new WorkspaceMigrationService(
            _paths,
            workspace,
            NullLogger<WorkspaceMigrationService>.Instance);
        return (workspace, migration);
    }

    private async Task SeedWorkspaceAsync(WorkspaceService workspace)
    {
        Assert.True((await workspace.InitializeAsync(_source)).IsSuccess);

        var game = Path.Combine(_source, "instances", "demo-abc123", "minecraft");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(Path.Combine(game, "options.txt"), "version:1\n");
        Directory.CreateDirectory(Path.Combine(game, "saves", "world"));
        await File.WriteAllTextAsync(Path.Combine(game, "saves", "world", "level.dat"), "level");
        Directory.CreateDirectory(Path.Combine(_source, "versions", "1.21.1"));
        await File.WriteAllTextAsync(Path.Combine(_source, "versions", "1.21.1", "1.21.1.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_source, "libraries", "org", "example"));
        await File.WriteAllBytesAsync(Path.Combine(_source, "libraries", "org", "example", "a.jar"), new byte[2048]);
    }

    [Fact]
    public async Task PlanMeasuresSourceAndTarget()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);

        var result = await migration.PlanAsync(_target);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var plan = result.Value!;
        Assert.True(plan.CanProceed, string.Join("; ", plan.Problems));
        Assert.Equal(_source, plan.SourcePath);
        Assert.Equal(1, plan.InstanceCount);
        Assert.True(plan.TotalFiles >= 4);
        Assert.True(plan.TotalBytes >= 2048);
    }

    [Fact]
    public async Task MigrationCopiesVerifiesAndSwitchesWhileKeepingSource()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);

        var result = await migration.StartAsync(_target);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(WorkspaceMigrationState.Completed, result.Value!.State);
        Assert.Equal(_target, workspace.Primary!.Path);
        Assert.Equal(_target, _paths.GameRoot);
        Assert.True(Directory.Exists(_source));
        Assert.True(File.Exists(Path.Combine(_target, "instances", "demo-abc123", "minecraft", "options.txt")));
        Assert.True(File.Exists(Path.Combine(_target, "libraries", "org", "example", "a.jar")));
        Assert.True(File.Exists(Path.Combine(_paths.ConfigDirectory, "workspace-migration.json")));
        Assert.False(Directory.Exists(_target + ".ecl-staging"));
    }

    [Fact]
    public async Task RefusesNonEmptyTarget()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);
        Directory.CreateDirectory(_target);
        await File.WriteAllTextAsync(Path.Combine(_target, "existing.txt"), "keep");

        var result = await migration.StartAsync(_target);

        Assert.False(result.IsSuccess);
        Assert.True(Directory.Exists(_target));
        Assert.True(File.Exists(Path.Combine(_target, "existing.txt")));
        Assert.Equal(_source, workspace.Primary!.Path);
    }

    [Fact]
    public async Task RefusesTargetInsideSource()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);

        var plan = await migration.PlanAsync(Path.Combine(_source, "nested"));

        Assert.False(plan.Value!.CanProceed);
        Assert.Contains(plan.Value.Problems, problem => problem.Contains("重叠", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusesWhenWorkspaceIsNotConfigured()
    {
        var (_, migration) = CreateServices();

        var result = await migration.StartAsync(_target);

        Assert.False(result.IsSuccess);
        Assert.Equal("workspace.migration.not_configured", result.Error!.Code);
    }

    [Fact]
    public async Task ResumeCompletesInterruptedMigration()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);

        // 模拟中断：留下不完整的 staging 与 Copying 记录。
        var staging = _target + ".ecl-staging";
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "partial.txt"), "x");
        var record = new WorkspaceMigrationRecord
        {
            Id = "interrupted",
            SourcePath = _source,
            TargetPath = _target,
            StagingPath = staging,
            State = WorkspaceMigrationState.Copying,
            CopiedFiles = 1,
        };
        await File.WriteAllTextAsync(
            Path.Combine(_paths.ConfigDirectory, "workspace-migration.json"),
            System.Text.Json.JsonSerializer.Serialize(record));

        var resumedService = new WorkspaceMigrationService(
            _paths,
            workspace,
            NullLogger<WorkspaceMigrationService>.Instance);
        var resumed = await resumedService.ResumeAsync();

        Assert.True(resumed.IsSuccess, resumed.Error?.ToString());
        Assert.Equal(WorkspaceMigrationState.Completed, resumed.Value!.State);
        Assert.Equal(_target, workspace.Primary!.Path);
        Assert.False(File.Exists(Path.Combine(_target, "partial.txt")));
        Assert.True(Directory.Exists(_source));
    }

    [Fact]
    public async Task DiscardRemovesOnlyStaging()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);
        var staging = _target + ".ecl-staging";
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "partial.txt"), "x");
        var record = new WorkspaceMigrationRecord
        {
            Id = "interrupted",
            SourcePath = _source,
            TargetPath = _target,
            StagingPath = staging,
            State = WorkspaceMigrationState.Interrupted,
        };
        await File.WriteAllTextAsync(
            Path.Combine(_paths.ConfigDirectory, "workspace-migration.json"),
            System.Text.Json.JsonSerializer.Serialize(record));
        var service = new WorkspaceMigrationService(
            _paths,
            workspace,
            NullLogger<WorkspaceMigrationService>.Instance);

        var result = await service.DiscardAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(_source));
        Assert.Equal(_source, workspace.Primary!.Path);
    }

    public void Dispose()
    {
        try
        {
            var root = Path.GetDirectoryName(_dataRoot);
            if (root is not null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}
