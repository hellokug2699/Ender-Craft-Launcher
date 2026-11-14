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
    public async Task PlanExposesStructuredProblemCodes()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);

        var overlap = await migration.PlanAsync(Path.Combine(_source, "nested"));
        var overlapProblem = Assert.Single(overlap.Value!.ProblemDetails);
        Assert.Equal("workspace.migration.overlap", overlapProblem.Code);
        Assert.Equal(overlap.Value.Problems[0], overlapProblem.Message);

        Directory.CreateDirectory(_target);
        await File.WriteAllTextAsync(Path.Combine(_target, "existing.txt"), "keep");
        var notEmpty = await migration.PlanAsync(_target);
        Assert.Contains(
            notEmpty.Value!.ProblemDetails,
            problem => problem.Code == "workspace.migration.target_not_empty");
        Assert.Equal(
            notEmpty.Value.Problems,
            notEmpty.Value.ProblemDetails.Select(problem => problem.Message));
    }

    [Fact]
    public async Task MigrationReportsRealStagesAndFileCounts()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);
        var stages = new List<WorkspaceMigrationState>();
        var copied = 0;
        var progress = new CollectingProgress(record =>
        {
            stages.Add(record.State);
            copied = Math.Max(copied, record.CopiedFiles);
        });

        var result = await migration.StartAsync(_target, progress);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(WorkspaceMigrationState.Completed, result.Value!.State);
        Assert.Contains(WorkspaceMigrationState.Copying, stages);
        Assert.Contains(WorkspaceMigrationState.Verifying, stages);
        Assert.Contains(WorkspaceMigrationState.Switching, stages);
        Assert.Equal(WorkspaceMigrationState.Completed, stages[^1]);
        Assert.True(copied >= 4);
        Assert.Equal(result.Value.TotalFiles, copied);
    }

    [Fact]
    public async Task CompleteSwitchFinishesARollbackRequiredMigrationWithoutCopyingAgain()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);

        // 模拟“文件已复制到新位置、但切换工作区配置失败”：目标目录已有完整副本。
        CopyTree(_source, _target);
        var record = new WorkspaceMigrationRecord
        {
            Id = "rollback-required",
            SourcePath = _source,
            TargetPath = _target,
            StagingPath = _target + ".ecl-staging",
            State = WorkspaceMigrationState.RollbackRequired,
            ErrorCode = "workspace.migration.switch_failed",
            ErrorMessage = "切换失败。",
        };
        await File.WriteAllTextAsync(
            Path.Combine(_paths.ConfigDirectory, "workspace-migration.json"),
            System.Text.Json.JsonSerializer.Serialize(record));

        var services = new WorkspaceMigrationService(
            _paths,
            workspace,
            NullLogger<WorkspaceMigrationService>.Instance);

        var completed = await services.CompleteSwitchAsync();

        Assert.True(completed.IsSuccess, completed.Error?.ToString());
        Assert.Equal(WorkspaceMigrationState.Completed, completed.Value!.State);
        Assert.Equal(_target, workspace.Primary!.Path);
        Assert.Equal(_target, _paths.GameRoot);
        Assert.True(File.Exists(Path.Combine(_target, "instances", "demo-abc123", "minecraft", "options.txt")));
        Assert.True(Directory.Exists(_source));
    }

    [Fact]
    public async Task CompleteSwitchRejectsOtherStates()
    {
        var (workspace, migration) = CreateServices();
        await SeedWorkspaceAsync(workspace);

        var rejected = await migration.CompleteSwitchAsync();
        Assert.False(rejected.IsSuccess);
        Assert.Equal("workspace.migration.none", rejected.Error!.Code);

        // 复制尚未完成（Copying）时只能继续迁移，不能直接切换工作区。
        var copying = new WorkspaceMigrationRecord
        {
            Id = "copying",
            SourcePath = _source,
            TargetPath = _target,
            StagingPath = _target + ".ecl-staging",
            State = WorkspaceMigrationState.Copying,
            CopiedFiles = 1,
        };
        await File.WriteAllTextAsync(
            Path.Combine(_paths.ConfigDirectory, "workspace-migration.json"),
            System.Text.Json.JsonSerializer.Serialize(copying));
        var services = new WorkspaceMigrationService(
            _paths,
            workspace,
            NullLogger<WorkspaceMigrationService>.Instance);

        var blocked = await services.CompleteSwitchAsync();
        Assert.False(blocked.IsSuccess);
        Assert.Equal("workspace.migration.not_switchable", blocked.Error!.Code);
        Assert.Equal(_source, workspace.Primary!.Path);
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private sealed class CollectingProgress(Action<WorkspaceMigrationRecord> report)
        : IProgress<WorkspaceMigrationRecord>
    {
        public void Report(WorkspaceMigrationRecord value) => report(value);
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
