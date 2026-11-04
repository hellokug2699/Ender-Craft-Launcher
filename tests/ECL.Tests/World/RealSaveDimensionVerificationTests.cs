// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.World.Dimensions;
using Xunit;

namespace Ecl.Tests.World;

/// <summary>
/// 真实存档只读验证入口。设置 <c>ECL_WORLD_VERIFY_DIR</c> 指向一个包含多个存档目录的父目录后运行；
/// CI 没有该环境变量时直接返回，不把本机路径写入仓库。
/// </summary>
public sealed class RealSaveDimensionVerificationTests
{
    [Fact]
    public void RealSavesResolveVanillaAndCustomDimensionsWithoutGuessing()
    {
        var root = Environment.GetEnvironmentVariable("ECL_WORLD_VERIFY_DIR");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        var service = new DimensionDiscoveryService();
        var worldCount = 0;
        var customCount = 0;
        foreach (var world in Directory.EnumerateDirectories(root).Take(64))
        {
            worldCount++;
            var result = service.Discover(world);
            Assert.Null(result.LevelDatError);
            var overworld = Assert.Single(result.Dimensions, dimension => dimension.Id == DimensionId.Overworld);
            Assert.Equal(DimensionResolutionStatus.Resolved, overworld.Resolution);

            if (Directory.Exists(Path.Combine(world, "DIM-1")))
            {
                var nether = Assert.Single(result.Dimensions, dimension => dimension.Id == DimensionId.Nether);
                Assert.NotEqual(DimensionResolutionStatus.Conflicted, nether.Resolution);
            }

            foreach (var dimension in result.Dimensions.Where(item => item.IsCustom))
            {
                customCount++;
                Assert.False(dimension.Id.IsVanilla);
                Assert.NotEmpty(dimension.Storage);
            }

            // 候选目录不得被归入主世界或任何已确认维度。
            foreach (var candidate in result.Candidates)
            {
                Assert.DoesNotContain(
                    result.Dimensions,
                    dimension => string.Equals(dimension.RootDirectory, candidate.RootDirectory, StringComparison.OrdinalIgnoreCase));
            }
        }

        Assert.True(worldCount > 0);
        _ = customCount;
    }
}
