// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Screenshots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 截图页领域服务（B2c）：只列出截图目录中的图片、读取与删除都受实例边界约束，
/// 删除经过实例事务并且失败时不谎报成功。
/// </summary>
public sealed class ScreenshotServiceTests
{
    // 1×1 PNG 签名与最小数据；只用于验证签名识别，不参与渲染测试。
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl2nLkAAAAASUVORK5CYII=");

    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

    [Fact]
    public async Task ListsOnlyDirectImageFilesAndMarksVerifiedSignatures()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var directory = Path.Combine(gameDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "nested"));

        await File.WriteAllBytesAsync(Path.Combine(directory, "alpha.png"), PngBytes);
        await File.WriteAllBytesAsync(Path.Combine(directory, "beta.jpg"), JpegHeader);
        await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "not an image");
        await File.WriteAllTextAsync(Path.Combine(directory, "fake.png"), "not a png");
        await File.WriteAllBytesAsync(Path.Combine(directory, "nested", "deep.png"), PngBytes);

        var service = provider.GetRequiredService<IScreenshotService>();
        var result = await service.ListAsync(instance.Id);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var list = result.Value!;
        Assert.Equal(["alpha.png", "beta.jpg", "fake.png"], list.Items.Select(item => item.FileName).Order());
        Assert.True(list.Items.Single(item => item.FileName == "alpha.png").IsRecognizedImage);
        Assert.True(list.Items.Single(item => item.FileName == "beta.jpg").IsRecognizedImage);
        Assert.False(list.Items.Single(item => item.FileName == "fake.png").IsRecognizedImage);
        Assert.All(list.Items, item => Assert.StartsWith("screenshots" + Path.DirectorySeparatorChar, item.RelativePath));
        Assert.Equal(0, list.SkippedCount);
    }

    [Fact]
    public async Task ReturnsEmptyListWhenScreenshotsDirectoryDoesNotExist()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);

        var service = provider.GetRequiredService<IScreenshotService>();
        var result = await service.ListAsync(instance.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
    }

    [Fact]
    public async Task ReadImageRejectsPathsOutsideTheScreenshotsDirectory()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var directory = Path.Combine(gameDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "alpha.png"), PngBytes);
        await File.WriteAllTextAsync(Path.Combine(gameDirectory, "options.txt"), "version:1");

        var service = provider.GetRequiredService<IScreenshotService>();
        foreach (var path in new[]
                 {
                     "options.txt",
                     "../instance.json",
                     "screenshots/../options.txt",
                     "screenshots/nested/alpha.png",
                     "screenshots/notes.txt",
                 })
        {
            var rejected = await service.ReadImageAsync(instance.Id, path);
            Assert.False(rejected.IsSuccess, $"路径应被拒绝：{path}");
        }

        var accepted = await service.ReadImageAsync(instance.Id, "screenshots/alpha.png");
        Assert.True(accepted.IsSuccess, accepted.Error?.ToString());
        Assert.Equal(PngBytes, accepted.Value!);
    }

    [Fact]
    public async Task DeletesOnlySelectedScreenshotsThroughTheInstanceTransaction()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var directory = Path.Combine(gameDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "first.png");
        var second = Path.Combine(directory, "second.png");
        await File.WriteAllBytesAsync(first, PngBytes);
        await File.WriteAllBytesAsync(second, PngBytes);

        var service = provider.GetRequiredService<IScreenshotService>();
        var result = await service.DeleteAsync(instance.Id, ["screenshots/first.png"]);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal([Path.Combine("screenshots", "first.png")], result.Value!.DeletedRelativePaths);
        Assert.False(result.Value.RecoveryRequired);
    }

    [Fact]
    public async Task DeleteRejectsNonScreenshotTargetsWithoutTouchingOtherFiles()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var directory = Path.Combine(gameDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "alpha.png"), PngBytes);
        var options = Path.Combine(gameDirectory, "options.txt");
        await File.WriteAllTextAsync(options, "version:1");

        var service = provider.GetRequiredService<IScreenshotService>();
        foreach (var path in new[] { "options.txt", "screenshots/../options.txt", "screenshots/nested/alpha.png" })
        {
            var rejected = await service.DeleteAsync(instance.Id, [path]);
            Assert.False(rejected.IsSuccess, $"路径应被拒绝：{path}");
        }

        Assert.True(File.Exists(Path.Combine(directory, "alpha.png")));
        Assert.True(File.Exists(options));
    }

    [Fact]
    public async Task DeleteFailsAsAWholeWhenOneSelectedFileIsMissing()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instance = await CreateInstanceAsync(provider);
        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        var directory = Path.Combine(gameDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        var existing = Path.Combine(directory, "alpha.png");
        await File.WriteAllBytesAsync(existing, PngBytes);

        var service = provider.GetRequiredService<IScreenshotService>();
        var result = await service.DeleteAsync(
            instance.Id,
            ["screenshots/alpha.png", "screenshots/missing.png"]);

        Assert.False(result.IsSuccess);
        Assert.True(File.Exists(existing));
    }

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var result = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Screenshot Test", "1.21.1", "fabric", "0.16.0");
        Assert.True(result.IsSuccess);
        return result.Value!;
    }
}
