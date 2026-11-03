// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Engine.Files;
using Ecl.Minecraft.Engine.Inventory;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Engine;

public sealed class InstanceFileAccessTests
{
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("mods/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData("mods//double.txt")]
    [InlineData("mods/./file.txt")]
    [InlineData("mods/file.txt:stream")]
    [InlineData("CON")]
    public void RejectsPathsOutsideInstanceOrAmbiguous(string relative)
    {
        var workspace = new InstanceFileWorkspace(Path.GetTempPath());
        Assert.Throws<InstancePathException>(() => workspace.Resolve(relative));
    }

    [Fact]
    public async Task ListsReadsAndExportsWithinInstance()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var files = provider.GetRequiredService<InstanceFileAccessService>();
        var created = (await instances.CreateAsync("Files", "1.21.1")).Value!;

        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var configDirectory = Path.Combine(game, "config");
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "example.toml"), "key = \"value\"\n");

        var opened = await files.OpenAsync(created.Id);
        Assert.True(opened.IsSuccess, opened.Error?.ToString());
        var workspace = opened.Value!;

        var entries = workspace.List("config");
        var entry = Assert.Single(entries);
        Assert.Equal("example.toml", entry.Name);
        Assert.Equal("application/toml", entry.TypeId);
        Assert.False(entry.IsDirectory);

        var preview = await workspace.PreviewTextAsync("config/example.toml");
        Assert.True(preview.IsSuccess, preview.Error?.ToString());
        Assert.Contains("key = \"value\"", preview.Value!.Text);
        Assert.False(preview.Value.IsBinary);

        var stats = workspace.GetStatistics("config");
        Assert.Equal(DirectoryStatisticsStatus.Complete, stats.Status);
        Assert.Equal(1, stats.FileCount);
        Assert.True(stats.TotalBytes > 0);

        var destination = Path.Combine(environment.Root, "exported.toml");
        var exported = await workspace.ExportAsync("config/example.toml", destination);
        Assert.True(exported.IsSuccess, exported.Error?.ToString());
        Assert.Equal("key = \"value\"\n", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task PreviewDoesNotFollowFileOutsideRoot()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var files = provider.GetRequiredService<InstanceFileAccessService>();
        var created = (await instances.CreateAsync("Preview", "1.21.1")).Value!;

        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var outside = Path.Combine(environment.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "secret");

        var opened = await files.OpenAsync(created.Id);
        var workspace = opened.Value!;
        await Assert.ThrowsAsync<InstancePathException>(
            async () => await workspace.PreviewTextAsync("../outside.txt"));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task SymlinkEscapeIsRejected()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var files = provider.GetRequiredService<InstanceFileAccessService>();
        var created = (await instances.CreateAsync("Links", "1.21.1")).Value!;

        var game = environment.Paths.GetInstanceGameDirectory(created.Id);
        var outside = Path.Combine(environment.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "secret");
        var link = Path.Combine(game, "escape.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                                              or PlatformNotSupportedException)
        {
            // 没有创建符号链接权限时跳过；路径穿越测试仍覆盖字符串边界。
            return;
        }

        var opened = await files.OpenAsync(created.Id);
        var workspace = opened.Value!;
        Assert.Throws<InstancePathException>(() => workspace.Resolve("escape.txt"));
    }
}

public sealed class ModVersionRangeTests
{
    [Theory]
    [InlineData("1.20.1", ">=1.20", true)]
    [InlineData("1.20.1", ">=1.20 <1.21", true)]
    [InlineData("1.21.1", ">=1.20 <1.21", false)]
    [InlineData("1.20.4", "[1.20,1.21)", true)]
    [InlineData("1.21.0", "[1.20,1.21)", false)]
    [InlineData("1.20.1", "1.20.x", true)]
    [InlineData("1.20.1", "1.21.x", false)]
    [InlineData("1.20.1", "*", true)]
    public void MatchesFabricAndMavenRanges(string version, string range, bool expected)
    {
        Assert.Equal(expected, ModVersionRange.Matches(version, range));
    }

    [Fact]
    public void UnknownRangeReturnsNullInsteadOfGuessing()
    {
        Assert.Null(ModVersionRange.Matches("1.20.1", "totally-not-a-version"));
    }
}
