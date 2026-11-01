// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class UpdateLockTests : IDisposable
{
    private readonly string _root;

    public UpdateLockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ecl-update-lock", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void SecondDeploymentCannotTakeTheLock()
    {
        using var first = UpdateLock.TryAcquire(_root);

        Assert.NotNull(first);
        Assert.Null(UpdateLock.TryAcquire(_root));
    }

    [Fact]
    public void LockIsReleasedWhenTheHolderExits()
    {
        var first = UpdateLock.TryAcquire(_root);
        Assert.NotNull(first);
        first!.Dispose();

        using var second = UpdateLock.TryAcquire(_root);

        Assert.NotNull(second);
    }

    [Fact]
    public void LockFileLivesInTheInstallRoot()
    {
        using var handle = UpdateLock.TryAcquire(_root);

        Assert.NotNull(handle);
        Assert.True(File.Exists(Path.Combine(_root, UpdateLock.FileName)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响测试结论。
        }
    }
}
