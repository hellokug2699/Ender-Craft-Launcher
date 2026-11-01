// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Core.Storage;
using Xunit;

namespace Ecl.Tests.Storage;

public sealed class WorkspacePathValidatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _workspace;

    public WorkspacePathValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ecl-ws-tests", Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(Path.GetTempPath(), "ecl-ws-games", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void RejectsRelativePath()
    {
        var validator = new WorkspacePathValidator(_root);

        var check = validator.Check("Games\\ECL", WorkspaceCheckMode.CreateIfMissing);

        Assert.False(check.IsUsable);
        Assert.Equal(WorkspaceStatus.Invalid, check.Status);
        Assert.Equal("workspace.path.relative", check.ProblemCode);
    }

    [Fact]
    public void RejectsEmptyPath()
    {
        var validator = new WorkspacePathValidator(_root);

        var check = validator.Check("  ", WorkspaceCheckMode.CreateIfMissing);

        Assert.False(check.IsUsable);
        Assert.Equal("workspace.path.empty", check.ProblemCode);
    }

    [Fact]
    public void RejectsLauncherDataDirectory()
    {
        var validator = new WorkspacePathValidator(_root);

        var check = validator.Check(Path.Combine(_root, "instances"), WorkspaceCheckMode.CreateIfMissing);

        Assert.False(check.IsUsable);
        Assert.Equal("workspace.path.protected", check.ProblemCode);
    }

    [Fact]
    public void WarnsButAcceptsExplicitApplicationDataDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            return;
        }

        var path = Path.Combine(localApplicationData, "ECLTestWorkspace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var validator = new WorkspacePathValidator(_root);

            var check = validator.Check(path, WorkspaceCheckMode.CreateIfMissing);

            Assert.True(check.IsUsable, check.ProblemMessage);
            Assert.Contains("workspace.warning.appData", check.WarningCodes);
            if (OperatingSystem.IsWindows())
            {
                Assert.Contains("workspace.warning.systemDrive", check.WarningCodes);
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void CreatesMissingDirectoryWhenSelectionIsNew()
    {
        var validator = new WorkspacePathValidator(_root);

        var check = validator.Check(_workspace, WorkspaceCheckMode.CreateIfMissing);

        Assert.True(check.IsUsable, check.ProblemMessage);
        Assert.Equal(WorkspaceStatus.Ready, check.Status);
        Assert.True(Directory.Exists(_workspace));
    }

    [Fact]
    public void DoesNotCreateMissingRegisteredWorkspace()
    {
        var validator = new WorkspacePathValidator(_root);

        var check = validator.Check(_workspace, WorkspaceCheckMode.RequireExisting);

        Assert.False(check.IsUsable);
        Assert.Equal(WorkspaceStatus.Missing, check.Status);
        Assert.Equal("workspace.path.missing", check.ProblemCode);
        Assert.False(Directory.Exists(_workspace));
    }

    [Fact]
    public void RejectsPathThatIsAFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_workspace)!);
        File.WriteAllText(_workspace, "x");
        var validator = new WorkspacePathValidator(_root);

        var check = validator.Check(_workspace, WorkspaceCheckMode.CreateIfMissing);

        Assert.False(check.IsUsable);
        Assert.Equal("workspace.path.notDirectory", check.ProblemCode);
    }

    [Fact]
    public void NormalizesTrailingSeparatorAndCaseForComparison()
    {
        Assert.True(WorkspacePathValidator.IsUnder(
            Path.Combine(_workspace, "instances", "demo"),
            _workspace));
        Assert.False(WorkspacePathValidator.IsUnder(
            _workspace + "-other",
            _workspace));
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _workspace })
        {
            TryDelete(directory);
        }
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
