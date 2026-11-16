// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Storage;
using Ecl.Core.Storage.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Storage;

/// <summary>F-02 S1/S2：只读登记、稳定根 ID、重新定位、解绑活动检查、schema 迁移。</summary>
public sealed class InstanceRootRegistryTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly AppPaths _paths;
    private readonly List<string> _directories = [];

    public InstanceRootRegistryTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "ecl-f02-registry", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dataRoot);
        _paths.EnsureCreated();
    }

    [Fact]
    public async Task RegisterExternalDoesNotWriteSourceDirectory()
    {
        var source = NewDirectory("hmcl");
        File.WriteAllText(Path.Combine(source, "options.txt"), "lang:zh_cn");
        File.WriteAllText(Path.Combine(source, "level.dat"), "data");
        var before = Snapshot(source);
        var service = CreateService();

        var result = await service.RegisterExternalAsync(source);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(before, Snapshot(source));
        Assert.Empty(Directory.EnumerateFiles(source, ".ecl-workspace-probe*"));
        Assert.False(Directory.Exists(Path.Combine(source, ".ecl")));
    }

    [Fact]
    public async Task RegisteringSamePathIsIdempotent()
    {
        var source = NewDirectory(".minecraft");
        var service = CreateService();

        var first = await service.RegisterExternalAsync(source);
        var second = await service.RegisterExternalAsync(source);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value!.Id, second.Value!.Id);
        Assert.Single(service.Roots);
    }

    [Fact]
    public async Task ConflictingPathIdentityIsRejected()
    {
        var primary = NewDirectory("primary");
        var external = NewDirectory("external");
        var service = CreateService();
        Assert.True((await service.InitializeAsync(primary)).IsSuccess);

        // 模拟另一个登记已占用同一物理目录（路径身份相同，路径不同）。
        var state = service.State;
        var occupied = new WorkspaceRoot(
            "ws-occupied",
            "外部目录",
            Path.Combine(Path.GetTempPath(), "ecl-f02-not-existing"),
            WorkspaceKind.External,
            WorkspaceStatus.Ready,
            DateTimeOffset.UtcNow,
            CanonicalPath: PathIdentity.Normalize(external),
            PathIdentityKey: PathIdentity.TryGetKey(external));
        SaveState(new WorkspaceFile
        {
            PrimaryId = state.PrimaryId,
            Roots = [.. state.Roots, occupied],
            OnboardingCompletedAtUtc = state.OnboardingCompletedAtUtc,
        });
        service = CreateService();

        var result = await service.RegisterExternalAsync(external);

        Assert.False(result.IsSuccess);
        Assert.Equal("source.already_registered", result.Error!.Code);
    }

    [Fact]
    public async Task RelocateRootKeepsIdAndDoesNotMoveFiles()
    {
        var primary = NewDirectory("primary");
        var oldPath = NewDirectory("old");
        var newPath = NewDirectory("new");
        File.WriteAllText(Path.Combine(newPath, "keep.txt"), "keep");
        var service = CreateService();
        Assert.True((await service.InitializeAsync(primary)).IsSuccess);
        var registered = await service.RegisterExternalAsync(oldPath);
        Assert.True(registered.IsSuccess);

        var relocated = await service.RelocateRootAsync(registered.Value!.Id, newPath);

        Assert.True(relocated.IsSuccess, relocated.Error?.ToString());
        Assert.Equal(registered.Value.Id, relocated.Value!.Id);
        Assert.Equal(PathIdentity.Normalize(newPath), relocated.Value.Path);
        Assert.Single(service.Roots, root => root.Id == registered.Value.Id);
        Assert.True(Directory.Exists(oldPath));
        Assert.True(File.Exists(Path.Combine(newPath, "keep.txt")));
    }

    [Fact]
    public async Task RelocateRejectsTargetOwnedByAnotherRoot()
    {
        var primary = NewDirectory("primary");
        var first = NewDirectory("first");
        var second = NewDirectory("second");
        var service = CreateService();
        Assert.True((await service.InitializeAsync(primary)).IsSuccess);
        var a = await service.RegisterExternalAsync(first);
        Assert.True((await service.RegisterExternalAsync(second)).IsSuccess);

        var result = await service.RelocateRootAsync(a.Value!.Id, second);

        Assert.False(result.IsSuccess);
        Assert.Equal("source.path_conflict", result.Error!.Code);
        Assert.Equal(PathIdentity.Normalize(first), service.Roots.First(root => root.Id == a.Value.Id).Path);
    }

    [Fact]
    public async Task PrimaryRootCannotBeRelocatedDirectly()
    {
        var primary = NewDirectory("primary");
        var other = NewDirectory("other");
        var service = CreateService();
        Assert.True((await service.InitializeAsync(primary)).IsSuccess);

        var result = await service.RelocateRootAsync(service.Primary!.Id, other);

        Assert.False(result.IsSuccess);
        Assert.Equal("workspace.primary_relocate", result.Error!.Code);
    }

    [Fact]
    public async Task UnbindWithoutProbeIsBlockedConservatively()
    {
        var source = NewDirectory("external");
        var service = CreateService();
        var registered = await service.RegisterExternalAsync(source);
        Assert.True(registered.IsSuccess);

        var removed = await service.RemoveRootAsync(registered.Value!.Id);

        Assert.False(removed.IsSuccess);
        Assert.Equal("source.activity_unknown", removed.Error!.Code);
        Assert.Contains(service.Roots, root => root.Id == registered.Value.Id);
    }

    [Fact]
    public async Task UnbindIsBlockedWhenActiveInstanceExists()
    {
        var source = NewDirectory("external");
        var root = new WorkspaceRoot("ws-x", "外部", source, WorkspaceKind.External, WorkspaceStatus.Ready);
        var probe = new StaticActivityProbe(
            Result<IReadOnlyList<ActiveInstanceReference>>.Ok(
                [new ActiveInstanceReference("demo-1", "session-1", "运行中")]));
        SaveState(new WorkspaceFile { Roots = [root] });
        var service = CreateService(probe);

        var removed = await service.RemoveRootAsync(root.Id);

        Assert.False(removed.IsSuccess);
        Assert.Equal("source.busy", removed.Error!.Code);
        Assert.Single(service.Roots);
    }

    [Fact]
    public async Task UnbindKeepsFilesAndRecordsRootRemoval()
    {
        var source = NewDirectory("external");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "keep");
        var probe = new StaticActivityProbe(Result<IReadOnlyList<ActiveInstanceReference>>.Ok([]));
        var root = new WorkspaceRoot("ws-unbind", "外部", source, WorkspaceKind.External, WorkspaceStatus.Ready);
        SaveState(new WorkspaceFile { Roots = [root] });
        var service = CreateService(probe);

        var removed = await service.RemoveRootAsync(root.Id, RootRemovalMode.Unbind);

        Assert.True(removed.IsSuccess, removed.Error?.ToString());
        Assert.Empty(service.Roots);
        Assert.True(File.Exists(Path.Combine(source, "keep.txt")));
    }

    [Fact]
    public async Task WorkspaceV1IsLoadedAndSavedAsV2()
    {
        var workspaceFile = Path.Combine(_paths.ConfigDirectory, "workspace.json");
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(
            workspaceFile,
            """
            {
              "schemaVersion": 1,
              "primaryId": "ws-legacy",
              "roots": [
                {
                  "id": "ws-legacy",
                  "name": "旧工作区",
                  "path": "C:\\Games\\ECL",
                  "kind": "Managed",
                  "status": "Ready"
                }
              ],
              "onboardingCompletedAtUtc": "2026-11-16T00:00:00+00:00"
            }
            """);

        var service = CreateService();
        Assert.Equal(1, service.State.SchemaVersion);
        Assert.Equal("ws-legacy", service.Primary!.Id);

        await service.ValidatePrimaryAsync();

        using var document = JsonDocument.Parse(File.ReadAllText(workspaceFile));
        Assert.Equal(WorkspaceFile.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ws-legacy", service.Primary!.Id);
    }

    [Fact]
    public void WorkspaceScanPolicyIsClamped()
    {
        var policy = new WorkspaceScanPolicy(MaxDepth: 999, MaxEntries: 0, PerRootTimeoutSeconds: -5).Clamp();

        Assert.Equal(WorkspaceScanPolicy.MaxDepthCeiling, policy.MaxDepth);
        Assert.Equal(1, policy.MaxEntries);
        Assert.Equal(1, policy.PerRootTimeoutSeconds);
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            TryDelete(directory);
        }

        TryDelete(_dataRoot);
    }

    private WorkspaceService CreateService() => new(_paths, NullLogger<WorkspaceService>.Instance);

    private WorkspaceService CreateService(IInstanceSourceActivityProbe probe) =>
        new(_paths, NullLogger<WorkspaceService>.Instance, probe);

    private void SaveState(WorkspaceFile state)
    {
        // 直接写 workspace.json，再让服务重新加载；用于构造已有登记/冲突场景。
        new WorkspaceStore(_paths.ConfigDirectory).Save(state);
    }

    private string NewDirectory(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "ecl-f02-registry-dirs", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(path);
        _directories.Add(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string Snapshot(string directory)
    {
        var entries = Directory
            .EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return $"{path}|{info.Length}|{info.LastWriteTimeUtc:O}|{info.Attributes}";
            });
        return string.Join("\n", entries);
    }

    private static void TryDelete(string directory)
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

    private sealed class StaticActivityProbe(Result<IReadOnlyList<ActiveInstanceReference>> result)
        : IInstanceSourceActivityProbe
    {
        public Task<Result<IReadOnlyList<ActiveInstanceReference>>> FindActiveAsync(
            string rootId,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
}
