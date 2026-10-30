// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Xunit;

namespace Ecl.Tests.Core;

public sealed class AppPathsTests
{
    [Fact]
    public void DefaultsGameRootToDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-paths", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);

        Assert.Equal(paths.Root, paths.GameRoot);
        Assert.Equal(Path.Combine(root, "instances"), paths.InstancesDirectory);
        Assert.Equal(Path.Combine(root, "downloads"), paths.DownloadsDirectory);
    }

    [Fact]
    public void UsesConfiguredMinecraftDirectoryForInstancesOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-paths", Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(Path.GetTempPath(), "ecl-game", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, gameRoot);

        Assert.Equal(Path.GetFullPath(gameRoot), paths.GameRoot);
        Assert.Equal(Path.GetFullPath(root), paths.Root);
        Assert.Equal(Path.Combine(gameRoot, "instances", "demo-abc123"), paths.GetInstanceDirectory("demo-abc123"));
        Assert.Equal(
            Path.Combine(gameRoot, "instances", "demo-abc123", "minecraft"),
            paths.GetInstanceGameDirectory("demo-abc123"));
        Assert.StartsWith(paths.Root, paths.GlobalSettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(paths.Root, paths.DownloadsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetGameRootResetsToDataRootWhenEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-paths", Guid.NewGuid().ToString("N"));
        var gameRoot = Path.Combine(Path.GetTempPath(), "ecl-game", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root, gameRoot);

        paths.SetGameRoot(null);

        Assert.Equal(Path.GetFullPath(root), paths.GameRoot);
    }
}
