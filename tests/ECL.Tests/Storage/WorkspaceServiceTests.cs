// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Storage;

public sealed class WorkspaceServiceTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly AppPaths _paths;
    private readonly List<string> _directories = [];

    public WorkspaceServiceTests()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "ecl-ws-service", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_dataRoot);
        _paths.EnsureCreated();
    }

    private string NewWorkspacePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "ecl-ws-service-games", Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        return path;
    }

    private WorkspaceService CreateService() => new(_paths, NullLogger<WorkspaceService>.Instance);

    [Fact]
    public void StartsUnconfigured()
    {
        var service = CreateService();

        Assert.False(service.IsConfigured);
        Assert.Null(service.Primary);
        Assert.False(_paths.WorkspaceConfigured);
    }

    [Fact]
    public async Task InitializeCreatesWorkspaceAndAppliesGameRoot()
    {
        var workspace = NewWorkspacePath();
        var service = CreateService();

        var result = await service.InitializeAsync(workspace);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(service.IsConfigured);
        Assert.Equal(workspace, service.Primary!.Path);
        Assert.Equal(workspace, _paths.GameRoot);
        Assert.True(_paths.WorkspaceConfigured);
        foreach (var name in new[] { "instances", "versions", "libraries", "assets" })
        {
            Assert.True(Directory.Exists(Path.Combine(workspace, name)), name);
        }
    }

    [Fact]
    public async Task PersistsConfigurationAcrossInstances()
    {
        var workspace = NewWorkspacePath();
        var first = CreateService();
        Assert.True((await first.InitializeAsync(workspace)).IsSuccess);

        var second = CreateService();

        Assert.True(second.IsConfigured);
        Assert.Equal(workspace, second.Primary!.Path);
        Assert.True(File.Exists(Path.Combine(_paths.ConfigDirectory, "workspace.json")));
    }

    [Fact]
    public async Task InitializeRejectsProtectedPathAndLeavesStateUnconfigured()
    {
        var service = CreateService();

        var result = await service.InitializeAsync(Path.Combine(_dataRoot, "instances"));

        Assert.False(result.IsSuccess);
        Assert.False(service.IsConfigured);
        Assert.False(_paths.WorkspaceConfigured);
    }

    [Fact]
    public async Task SkipOnboardingKeepsWorkspaceUnconfigured()
    {
        var service = CreateService();

        await service.SkipOnboardingAsync();

        Assert.False(service.IsConfigured);
        Assert.True(service.State.OnboardingSkipped);
    }

    [Fact]
    public async Task ValidationReportsMissingAndDoesNotRecreateDirectory()
    {
        var workspace = NewWorkspacePath();
        var service = CreateService();
        Assert.True((await service.InitializeAsync(workspace)).IsSuccess);
        Directory.Delete(workspace, recursive: true);

        var result = await service.ValidatePrimaryAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsUsable);
        Assert.Equal(WorkspaceStatus.Missing, result.Value.Status);
        Assert.False(Directory.Exists(workspace));
        Assert.Equal(workspace, service.Primary!.Path);
        Assert.Equal(WorkspaceStatus.Missing, service.Primary.Status);
    }

    [Fact]
    public async Task RegisterExternalRootDoesNotChangePrimary()
    {
        var primary = NewWorkspacePath();
        var external = NewWorkspacePath();
        Directory.CreateDirectory(external);
        var service = CreateService();
        Assert.True((await service.InitializeAsync(primary)).IsSuccess);

        var result = await service.RegisterExternalAsync(external);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(primary, service.Primary!.Path);
        Assert.Contains(service.Roots, root => root.Path == external && root.Kind == WorkspaceKind.External);
        Assert.Equal(primary, _paths.GameRoot);
    }

    [Fact]
    public void DetectLegacyFindsDataUnderLauncherDataDirectory()
    {
        var instanceDirectory = Path.Combine(_dataRoot, "instances", "demo-abc123");
        Directory.CreateDirectory(instanceDirectory);
        var service = CreateService();

        var legacy = service.DetectLegacy();

        Assert.True(legacy.Found);
        Assert.Equal(1, legacy.InstanceCount);
        Assert.Equal(_dataRoot, legacy.Path);
    }

    [Fact]
    public async Task AdoptLegacyConfiguresExistingDataDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_dataRoot, "instances", "demo-abc123"));
        var service = CreateService();

        var result = await service.AdoptLegacyAsync();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(service.IsConfigured);
        Assert.Equal(_dataRoot, _paths.GameRoot);
    }

    [Fact]
    public async Task StorageLayoutSeparatesAllFourCategories()
    {
        var workspace = NewWorkspacePath();
        var service = CreateService();
        Assert.True((await service.InitializeAsync(workspace)).IsSuccess);

        var layout = service.GetStorageLayout();

        Assert.Equal(_paths.Root, layout.UserDataRoot);
        Assert.Equal(_paths.RuntimesDirectory, layout.RuntimesRoot);
        Assert.Equal(workspace, layout.WorkspaceRoot);
        Assert.NotEqual(layout.UserDataRoot, layout.RuntimesRoot);
        Assert.NotEqual(layout.UserDataRoot, layout.WorkspaceRoot);
        Assert.True(layout.WorkspaceSeparatedFromUserData);
    }

    [Fact]
    public void RecommendsOnlyNonProtectedLocations()
    {
        var service = CreateService();

        var suggestions = service.Suggest();

        Assert.All(suggestions, suggestion => Assert.False(WorkspacePathValidator.IsUnder(suggestion.Path, _dataRoot)));
        Assert.True(suggestions.Count(suggestion => suggestion.IsRecommended) <= 1);
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            TryDelete(directory);
        }

        TryDelete(_dataRoot);
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
}
