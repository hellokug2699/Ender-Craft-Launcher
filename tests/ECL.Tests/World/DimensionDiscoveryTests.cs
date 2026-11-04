// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.World.Dimensions;
using Ecl.World.Nbt;
using Ecl.World.Worlds;
using Xunit;

namespace Ecl.Tests.World;

/// <summary>ADR 0031：维度身份、存储解析、隔离与降级。</summary>
public sealed class DimensionDiscoveryTests
{
    private const int DataVersion1201 = 3955;

    [Fact]
    public void DimensionIdSeparatesIdentityFromPathAndRejectsAmbiguousInput()
    {
        Assert.True(DimensionId.TryParse("minecraft:the_nether", out var nether));
        Assert.Equal(DimensionKind.Nether, nether.Kind);
        Assert.True(nether.IsVanilla);
        Assert.Equal("minecraft:the_nether", nether.ToString());

        Assert.True(DimensionId.TryParse("example:moon/base", out var custom));
        Assert.Equal(DimensionKind.Custom, custom.Kind);
        Assert.Equal("example", custom.Namespace);
        Assert.Equal("moon/base", custom.Path);

        Assert.False(DimensionId.TryParse("Example:moon", out _));
        Assert.False(DimensionId.TryParse("example:../moon", out _));
        Assert.False(DimensionId.TryParse("example:", out _));
        Assert.False(DimensionId.TryParse("no_namespace", out _));
        Assert.False(DimensionId.TryParse("example:Moon", out _));
        Assert.False(DimensionId.TryParse(string.Empty, out _));
    }

    [Fact]
    public void VanillaLayoutResolvesEachStorageCategoryIndependently()
    {
        var directory = CreateTempDirectory();
        try
        {
            var world = Path.Combine(directory, "World");
            Touch(Path.Combine(world, "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "entities", "r.0.0.mca"));
            Touch(Path.Combine(world, "poi", "r.0.0.mca"));
            Touch(Path.Combine(world, "DIM-1", "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "DIM-1", "entities", "r.0.0.mca"));
            Touch(Path.Combine(world, "DIM1", "region", "r.0.0.mca"));
            WriteLevelDat(
                Path.Combine(world, "level.dat"),
                DataVersion1201,
                "minecraft:overworld",
                "minecraft:the_nether",
                "minecraft:the_end");

            var result = new DimensionDiscoveryService().Discover(world);

            Assert.Null(result.LevelDatError);
            Assert.Equal(3, result.Dimensions.Count);
            var overworld = Assert.Single(result.Dimensions, item => item.Id == DimensionId.Overworld);
            var nether = Assert.Single(result.Dimensions, item => item.Id == DimensionId.Nether);
            var end = Assert.Single(result.Dimensions, item => item.Id == DimensionId.End);

            Assert.Equal(DimensionResolutionStatus.Resolved, overworld.Resolution);
            Assert.Equal(DimensionResolutionStatus.Resolved, nether.Resolution);
            Assert.Equal(DimensionResolutionStatus.Resolved, end.Resolution);
            Assert.True(overworld.HasUsableStorage);

            Assert.Equal(DimensionStorageStatus.Available, nether.Find(DimensionStorageCategory.Region)!.Status);
            Assert.Equal(DimensionStorageStatus.Available, nether.Find(DimensionStorageCategory.Entities)!.Status);
            Assert.Equal(DimensionStorageStatus.Missing, nether.Find(DimensionStorageCategory.Poi)!.Status);
            Assert.Equal(DimensionStorageStatus.Available, end.Find(DimensionStorageCategory.Region)!.Status);
            Assert.Equal(DimensionStorageStatus.Missing, end.Find(DimensionStorageCategory.Entities)!.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DatapackDimensionDeclaredByLevelDatIsResolved()
    {
        var directory = CreateTempDirectory();
        try
        {
            var world = Path.Combine(directory, "World");
            Touch(Path.Combine(world, "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "dimensions", "example", "moon", "region", "r.0.0.mca"));
            WriteLevelDat(
                Path.Combine(world, "level.dat"),
                DataVersion1201,
                "minecraft:overworld",
                "minecraft:the_nether",
                "minecraft:the_end",
                "example:moon");

            var result = new DimensionDiscoveryService().Discover(world);

            var moon = result.Find(DimensionId.Parse("example:moon"));
            Assert.NotNull(moon);
            Assert.Equal(DimensionResolutionStatus.Resolved, moon!.Resolution);
            Assert.True(moon.DeclaredByLevelDat);
            Assert.Equal(DimensionStorageStatus.Available, moon.Find(DimensionStorageCategory.Region)!.Status);
            Assert.Equal(DimensionStorageEvidence.DatapackLayout, moon.Find(DimensionStorageCategory.Region)!.Evidence);
            Assert.True(moon.HasUsableStorage);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UndeclaredDatapackDirectoryStaysUnconfirmed()
    {
        var directory = CreateTempDirectory();
        try
        {
            var world = Path.Combine(directory, "World");
            Touch(Path.Combine(world, "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "dimensions", "example", "moon", "region", "r.0.0.mca"));
            WriteLevelDat(Path.Combine(world, "level.dat"), DataVersion1201, "minecraft:overworld");

            var result = new DimensionDiscoveryService().Discover(world);

            var moon = result.Find("example:moon");
            Assert.NotNull(moon);
            Assert.Equal(DimensionResolutionStatus.Unconfirmed, moon!.Resolution);
            Assert.False(moon.Resolution == DimensionResolutionStatus.Resolved);
            Assert.True(result.HasUnconfirmedContent);
            Assert.Contains(result.Warnings, warning => warning.Contains("未在 level.dat", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConflictingVanillaStorageIsNotSilentlySelected()
    {
        var directory = CreateTempDirectory();
        try
        {
            var world = Path.Combine(directory, "World");
            Touch(Path.Combine(world, "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "DIM-1", "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "dimensions", "minecraft", "the_nether", "region", "r.0.0.mca"));
            WriteLevelDat(
                Path.Combine(world, "level.dat"),
                DataVersion1201,
                "minecraft:overworld",
                "minecraft:the_nether");

            var result = new DimensionDiscoveryService().Discover(world);

            var nether = Assert.Single(result.Dimensions, item => item.Id == DimensionId.Nether);
            Assert.Equal(DimensionResolutionStatus.Conflicted, nether.Resolution);
            Assert.False(nether.HasUsableStorage);
            Assert.Null(nether.FindReadable(DimensionStorageCategory.Region));
            Assert.Contains("多个存储位置", nether.Note);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnknownDimensionStorageBecomesCandidateInsteadOfOverworld()
    {
        var directory = CreateTempDirectory();
        try
        {
            var world = Path.Combine(directory, "World");
            Touch(Path.Combine(world, "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "DIM2", "region", "r.0.0.mca"));
            WriteLevelDat(Path.Combine(world, "level.dat"), DataVersion1201, "minecraft:overworld");

            var result = new DimensionDiscoveryService().Discover(world);

            Assert.DoesNotContain(result.Dimensions, item => item.Id == DimensionId.Overworld && item.RootDirectory.EndsWith("DIM2", StringComparison.OrdinalIgnoreCase));
            var candidate = Assert.Single(result.Candidates);
            Assert.Equal("DIM2", candidate.CandidateKey);
            Assert.All(candidate.Storage, mapping =>
            {
                if (mapping.AbsolutePath is not null)
                {
                    Assert.Equal(DimensionStorageStatus.Unconfirmed, mapping.Status);
                }
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RegistryDimensionWithoutStorageIsReportedAsMissingStorage()
    {
        var directory = CreateTempDirectory();
        try
        {
            var world = Path.Combine(directory, "World");
            Touch(Path.Combine(world, "region", "r.0.0.mca"));
            WriteLevelDat(
                Path.Combine(world, "level.dat"),
                DataVersion1201,
                "minecraft:overworld",
                "example:void");

            var result = new DimensionDiscoveryService().Discover(world);

            var voidDimension = result.Find("example:void");
            Assert.NotNull(voidDimension);
            Assert.Equal(DimensionResolutionStatus.Resolved, voidDimension!.Resolution);
            Assert.False(voidDimension.HasUsableStorage);
            Assert.All(
                voidDimension.Storage,
                mapping => Assert.Equal(DimensionStorageStatus.Missing, mapping.Status));
            Assert.Contains("没有可发现的存储目录", voidDimension.Note);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OldDataVersionTreatsUnexpectedStorageAsUnconfirmed()
    {
        var directory = CreateTempDirectory();
        try
        {
            var world = Path.Combine(directory, "World");
            Touch(Path.Combine(world, "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "poi", "r.0.0.mca"));
            WriteLevelDat(Path.Combine(world, "level.dat"), 1000, "minecraft:overworld");

            var result = new DimensionDiscoveryService().Discover(world);

            var overworld = Assert.Single(result.Dimensions, item => item.Id == DimensionId.Overworld);
            var poi = overworld.Find(DimensionStorageCategory.Poi)!;
            Assert.Equal(DimensionStorageExpectation.NotApplicable, poi.Expectation);
            Assert.Equal(DimensionStorageStatus.Unconfirmed, poi.Status);
            Assert.Equal(DimensionStorageStatus.Missing, overworld.Find(DimensionStorageCategory.Entities)!.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptLevelDatKeepsCustomDirectoriesUnconfirmed()
    {
        var directory = CreateTempDirectory();
        try
        {
            var world = Path.Combine(directory, "World");
            Touch(Path.Combine(world, "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "dimensions", "example", "moon", "region", "r.0.0.mca"));
            Directory.CreateDirectory(world);
            File.WriteAllBytes(Path.Combine(world, "level.dat"), [0x01, 0x02, 0x03]);

            var result = new DimensionDiscoveryService().Discover(world);

            Assert.NotNull(result.LevelDatError);
            var moon = result.Find("example:moon");
            Assert.NotNull(moon);
            Assert.Equal(DimensionResolutionStatus.Unconfirmed, moon!.Resolution);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WorldSaveScanProjectsResolutionStatus()
    {
        var directory = CreateTempDirectory();
        try
        {
            var game = Path.Combine(directory, "game");
            var world = Path.Combine(game, "saves", "TestWorld");
            Touch(Path.Combine(world, "region", "r.0.0.mca"));
            Touch(Path.Combine(world, "DIM-1", "region", "r.0.0.mca"));
            WriteLevelDat(
                Path.Combine(world, "level.dat"),
                DataVersion1201,
                "minecraft:overworld",
                "minecraft:the_nether");

            var scan = await new WorldSaveService(game).ScanAsync();
            var save = Assert.Single(scan.Worlds);
            Assert.NotNull(save.Discovery);
            var nether = Assert.Single(save.Dimensions, item => item.Id == "minecraft:the_nether");
            Assert.True(nether.CanRead);
            Assert.Equal(nether, save.FindReadableDimension("minecraft:the_nether"));
            Assert.Null(save.FindReadableDimension("example:absent"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WorldDimensionKeyIsolatesSameCoordinatesAcrossDimensionsAndWorlds()
    {
        var worldA = Path.Combine(Path.GetTempPath(), "ecl-key", "a");
        var worldB = Path.Combine(Path.GetTempPath(), "ecl-key", "b");

        var overworldA = new WorldDimensionKey(worldA, DimensionId.Overworld);
        var netherA = new WorldDimensionKey(worldA, DimensionId.Nether);
        var overworldB = new WorldDimensionKey(worldB, DimensionId.Overworld);

        var chunk = new ChunkIdentity(overworldA, 10, -4);
        Assert.Equal(chunk, new ChunkIdentity(overworldA, 10, -4));
        Assert.NotEqual(chunk, new ChunkIdentity(netherA, 10, -4));
        Assert.NotEqual(chunk, new ChunkIdentity(overworldB, 10, -4));
        Assert.Equal(new RegionIdentity(overworldA, 0, -1), chunk.Region);
        Assert.NotEqual(chunk.World, netherA);
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x00]);
    }

    private static void WriteLevelDat(string path, int dataVersion, params string[] dimensionIds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = NbtTag.Compound("Data")
            .Add("LevelName", NbtTagType.String, "TestWorld")
            .Add("DataVersion", NbtTagType.Int, dataVersion)
            .Add("LastPlayed", NbtTagType.Long, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        data.Add(NbtTag.Compound("Version").Add("Name", NbtTagType.String, "1.20.1"));
        if (dimensionIds.Length > 0)
        {
            var dimensions = NbtTag.Compound("dimensions");
            foreach (var id in dimensionIds)
            {
                dimensions.Add(NbtTag.Compound(id));
            }

            data.Add(NbtTag.Compound("WorldGenSettings").Add(dimensions));
        }

        NbtFile.WriteFile(path, NbtTag.Compound().Add(data), NbtCompression.Gzip);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ecl-dimension-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
