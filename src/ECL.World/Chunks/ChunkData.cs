// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.World.Nbt;

namespace Ecl.World.Chunks;

public enum ChunkCompleteness
{
    Unknown,
    Partial,
    Full,
}

public sealed record BlockPaletteEntry(string Name, string? Properties)
{
    public string DisplayName => Properties is null ? Name : $"{Name}[{Properties}]";
}

public sealed class ChunkSection
{
    public required int Y { get; init; }

    public required IReadOnlyList<BlockPaletteEntry> BlockPalette { get; init; }

    public long[] BlockData { get; init; } = [];

    public int BlockBitsPerEntry { get; init; }

    public bool DensePacking { get; init; }

    public bool HasData => BlockPalette.Count > 1 && BlockData.Length > 0;

    public BlockPaletteEntry? GetBlock(int localX, int localY, int localZ)
    {
        if (BlockPalette.Count == 0)
        {
            return null;
        }

        if (BlockPalette.Count == 1 || BlockData.Length == 0)
        {
            return BlockPalette[0];
        }

        var index = (localY * 16 + localZ) * 16 + localX;
        var value = Unpack(index);
        return value >= 0 && value < BlockPalette.Count
            ? BlockPalette[(int)value]
            : null;
    }

    public long? Unpack(int index)
    {
        if (BlockBitsPerEntry <= 0 || BlockData.Length == 0)
        {
            return null;
        }

        var entriesPerLong = DensePacking ? 64 / BlockBitsPerEntry : 64 / BlockBitsPerEntry;
        var longIndex = index / entriesPerLong;
        if ((uint)longIndex >= (uint)BlockData.Length)
        {
            return null;
        }

        var offset = index % entriesPerLong;
        var mask = (1L << BlockBitsPerEntry) - 1;
        return (BlockData[longIndex] >> (offset * BlockBitsPerEntry)) & mask;
    }
}

/// <summary>
/// 一个已解析的 Chunk。仅暴露磁盘上实际保存的数据；缺失或损坏的字段保持 null，
/// 不根据“看起来像什么”回填原版语义。
/// </summary>
public sealed class ChunkData
{
    private readonly Dictionary<int, ChunkSection> _sections = [];

    public required int X { get; init; }

    public required int Z { get; init; }

    public int DataVersion { get; init; }

    public ChunkCompleteness Completeness { get; init; } = ChunkCompleteness.Unknown;

    public string Status { get; init; } = "unknown";

    public IReadOnlyList<ChunkSection> Sections => _sections.Values.OrderBy(section => section.Y).ToArray();

    public int BlockEntityCount { get; init; }

    public int EntityCount { get; init; }

    public string? SurfaceError { get; init; }

    public string?[] SurfaceBlocks { get; } = new string?[256];

    public int?[] SurfaceHeights { get; } = new int?[256];

    public string?[] BiomeIds { get; } = new string?[256];

    public bool HasHeightmap { get; private set; }

    internal void AddSection(ChunkSection section) => _sections[section.Y] = section;

    public BlockPaletteEntry? GetBlock(int worldX, int worldY, int worldZ)
    {
        var sectionY = worldY >> 4;
        if (!_sections.TryGetValue(sectionY, out var section))
        {
            return null;
        }

        return section.GetBlock(
            ((worldX % 16) + 16) % 16,
            ((worldY % 16) + 16) % 16,
            ((worldZ % 16) + 16) % 16);
    }

    public int Index(int localX, int localZ) => localZ * 16 + localX;

    public string? SurfaceBlockAt(int localX, int localZ) => SurfaceBlocks[Index(localX, localZ)];

    public string? BiomeAt(int localX, int localZ) => BiomeIds[Index(localX, localZ)];

    public static ChunkParseResult Parse(NbtTag root, string? dimensionId = null)
    {
        if (root.Type != NbtTagType.Compound)
        {
            return ChunkParseResult.Fail("Chunk 根标签不是复合标签。");
        }

        var level = root.Child("Level") ?? root;
        var dataVersion = root.Child("DataVersion")?.GetInt()
                          ?? level.Child("DataVersion")?.GetInt()
                          ?? 0;

        var x = level.Child("xPos")?.GetInt() ?? root.Child("xPos")?.GetInt() ?? 0;
        var z = level.Child("zPos")?.GetInt() ?? root.Child("zPos")?.GetInt() ?? 0;
        var status = level.Child("Status")?.GetString()
                     ?? (level.Child("TerrainPopulated")?.GetByte() == 1 ? "full" : "unknown");
        var completeness = status.Contains("full", StringComparison.OrdinalIgnoreCase)
            ? ChunkCompleteness.Full
            : status is "unknown" or "" ? ChunkCompleteness.Unknown : ChunkCompleteness.Partial;

        var chunk = new ChunkData
        {
            X = x,
            Z = z,
            DataVersion = dataVersion,
            Completeness = completeness,
            Status = status,
            BlockEntityCount = CountList(level.Child("block_entities") ?? level.Child("TileEntities")),
            EntityCount = CountList(level.Child("Entities")),
        };

        var sections = level.Child("sections")?.Children ?? level.Child("Sections")?.Children;
        if (sections is not null)
        {
            foreach (var sectionTag in sections)
            {
                try
                {
                    var section = ParseSection(sectionTag, dataVersion);
                    if (section is not null)
                    {
                        chunk.AddSection(section);
                    }

                    var biomes = ParseBiomeSection(sectionTag);
                    if (biomes is not null)
                    {
                        chunk._biomeSections[biomes.Value.Y] = biomes.Value.Section;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
                {
                    // 单个 Section 损坏不应导致整个 Chunk 不可浏览。
                }
            }
        }

        chunk.HasHeightmap = TryReadHeightmap(level, dataVersion, chunk);
        chunk.ComputeSurfaceAndBiomes(level, dataVersion);
        return ChunkParseResult.Ok(chunk);
    }

    private static ChunkSection? ParseSection(NbtTag sectionTag, int dataVersion)
    {
        if (sectionTag.Type != NbtTagType.Compound)
        {
            return null;
        }

        var y = sectionTag.Child("Y")?.GetInt() ?? 0;
        var blockStates = sectionTag.Child("block_states");
        var paletteTag = blockStates?.Child("palette")
                         ?? sectionTag.Child("Palette");
        var dataTag = blockStates?.Child("data")
                      ?? sectionTag.Child("BlockStates");
        if (paletteTag is null)
        {
            paletteTag = sectionTag.Child("block_states")?.Child("palette");
        }

        var palette = new List<BlockPaletteEntry>();
        if (paletteTag is { Type: NbtTagType.List })
        {
            foreach (var entry in paletteTag.Children)
            {
                if (entry.Type == NbtTagType.Compound)
                {
                    palette.Add(new BlockPaletteEntry(
                        entry.Child("Name")?.GetString() ?? "minecraft:air",
                        FormatProperties(entry.Child("Properties"))));
                }
                else if (entry.Type == NbtTagType.String)
                {
                    palette.Add(new BlockPaletteEntry(entry.GetString() ?? "minecraft:air", null));
                }
            }
        }

        var data = dataTag?.GetLongArray() ?? [];
        var bits = palette.Count <= 1
            ? 0
            : Math.Max(4, BitsRequired(palette.Count));
        var dense = dataVersion >= 2529;
        return new ChunkSection
        {
            Y = y,
            BlockPalette = palette,
            BlockData = data,
            BlockBitsPerEntry = bits,
            DensePacking = dense,
        };
    }

    private static string? FormatProperties(NbtTag? properties)
    {
        if (properties is not { Type: NbtTagType.Compound } || properties.Children.Count == 0)
        {
            return null;
        }

        return string.Join(
            ',',
            properties.Children
                .OrderBy(child => child.Name, StringComparer.Ordinal)
                .Select(child => $"{child.Name}={child.GetString() ?? string.Empty}"));
    }

    private void ComputeSurfaceAndBiomes(NbtTag level, int dataVersion)
    {
        var legacyBiomes = level.Child("Biomes");
        for (var z = 0; z < 16; z++)
        {
            for (var x = 0; x < 16; x++)
            {
                var index = z * 16 + x;
                var surfaceY = SurfaceHeights[index];
                if (surfaceY is null)
                {
                    surfaceY = FindSurface(x, z);
                    SurfaceHeights[index] = surfaceY;
                }

                if (surfaceY is not null)
                {
                    var block = GetBlock(x, surfaceY.Value, z);
                    SurfaceBlocks[index] = block?.DisplayName;
                }

                BiomeIds[index] = ResolveBiome(x, z, surfaceY ?? 63, legacyBiomes, dataVersion);
            }
        }
    }

    private int? FindSurface(int localX, int localZ)
    {
        foreach (var section in _sections.Values.OrderByDescending(section => section.Y))
        {
            for (var localY = 15; localY >= 0; localY--)
            {
                var block = section.GetBlock(localX, localY, localZ);
                if (block is null || IsAir(block.Name))
                {
                    continue;
                }

                return section.Y * 16 + localY;
            }
        }

        return null;
    }

    private static bool IsAir(string name) => name is
        "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air" or
        "minecraft:structure_void" or "minecraft:light";

    private static bool TryReadHeightmap(NbtTag level, int dataVersion, ChunkData chunk)
    {
        var heightmap = level.Child("Heightmaps")?.Child("WORLD_SURFACE")
                        ?? level.Child("Heightmaps")?.Child("WORLD_SURFACE_WG");
        if (heightmap is not { Type: NbtTagType.LongArray })
        {
            return false;
        }

        var values = heightmap.GetLongArray();
        if (values.Length == 0)
        {
            return false;
        }

        var bits = dataVersion >= 2529 ? 9 : 8;
        var perLong = 64 / bits;
        var mask = (1L << bits) - 1;
        for (var index = 0; index < 256; index++)
        {
            var longIndex = index / perLong;
            if (longIndex >= values.Length)
            {
                break;
            }

            var value = (int)((values[longIndex] >> ((index % perLong) * bits)) & mask);
            if (value > 0)
            {
                chunk.SurfaceHeights[index] = value;
            }
        }

        return true;
    }

    private string? ResolveBiome(int localX, int localZ, int surfaceY, NbtTag? legacyBiomes, int dataVersion)
    {
        var biome = ReadModernBiome(localX, localZ, surfaceY);
        if (biome is not null)
        {
            return biome;
        }

        if (legacyBiomes is { Type: NbtTagType.IntArray })
        {
            var values = legacyBiomes.GetIntArray();
            var index = localZ * 16 + localX;
            if (index < values.Length)
            {
                return LegacyBiomeNames.TryGetValue(values[index], out var name)
                    ? name
                    : $"legacy:{values[index].ToString(CultureInfo.InvariantCulture)}";
            }
        }
        else if (legacyBiomes is { Type: NbtTagType.ByteArray })
        {
            var values = legacyBiomes.GetByteArray();
            var index = localZ * 16 + localX;
            if (index < values.Length)
            {
                return LegacyBiomeNames.TryGetValue(values[index], out var name)
                    ? name
                    : $"legacy:{values[index].ToString(CultureInfo.InvariantCulture)}";
            }
        }

        return null;
    }

    private string? ReadModernBiome(int localX, int localZ, int surfaceY)
    {
        // 生物群系数据由 ChunkData.Parse 在 Section 读取阶段写入 _biomeSections。
        var sectionY = surfaceY >> 4;
        if (!_biomeSections.TryGetValue(sectionY, out var biomes) || biomes.Palette.Count == 0)
        {
            return null;
        }

        var quartX = localX >> 2;
        var quartZ = localZ >> 2;
        var localQuartY = (surfaceY & 15) >> 2;
        var index = (localQuartY * 4 + quartZ) * 4 + quartX;
        var paletteIndex = biomes.Get(index);
        if (paletteIndex is null)
        {
            return null;
        }

        var value = paletteIndex.Value;
        return value >= 0 && value < biomes.Palette.Count
            ? biomes.Palette[(int)value]
            : $"unknown-biome:{value}";
    }

    private static (int Y, BiomeSection Section)? ParseBiomeSection(NbtTag sectionTag)
    {
        var biomes = sectionTag.Child("biomes");
        var paletteTag = biomes?.Child("palette");
        if (paletteTag is not { Type: NbtTagType.List } || paletteTag.Children.Count == 0)
        {
            return null;
        }

        var palette = new List<string>();
        foreach (var entry in paletteTag.Children)
        {
            if (entry.Type == NbtTagType.String)
            {
                palette.Add(entry.GetString() ?? "unknown");
            }
        }

        if (palette.Count == 0)
        {
            return null;
        }

        var data = biomes?.Child("data")?.GetLongArray() ?? [];
        var bits = palette.Count <= 1 ? 0 : BitsRequired(palette.Count);
        return (sectionTag.Child("Y")?.GetInt() ?? 0, new BiomeSection(palette, data, bits));
    }

    private readonly Dictionary<int, BiomeSection> _biomeSections = [];

    private sealed record BiomeSection(IReadOnlyList<string> Palette, long[] Data, int Bits)
    {
        public long? Get(int index)
        {
            if (Palette.Count <= 1)
            {
                return 0;
            }

            var perLong = 64 / Bits;
            var longIndex = index / perLong;
            if ((uint)longIndex >= (uint)Data.Length)
            {
                return null;
            }

            var mask = (1L << Bits) - 1;
            return (Data[longIndex] >> ((index % perLong) * Bits)) & mask;
        }
    }

    private static int BitsRequired(int count)
    {
        var bits = 0;
        var value = count - 1;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return Math.Max(1, bits);
    }

    private static int CountList(NbtTag? tag) => tag is { Type: NbtTagType.List } ? tag.Children.Count : 0;

    private static readonly Dictionary<int, string> LegacyBiomeNames = new()
    {
        [0] = "minecraft:ocean",
        [1] = "minecraft:plains",
        [2] = "minecraft:desert",
        [3] = "minecraft:mountains",
        [4] = "minecraft:forest",
        [5] = "minecraft:taiga",
        [6] = "minecraft:swamp",
        [7] = "minecraft:river",
        [8] = "minecraft:nether_wastes",
        [9] = "minecraft:the_end",
        [10] = "minecraft:frozen_ocean",
        [11] = "minecraft:frozen_river",
        [12] = "minecraft:snowy_tundra",
        [13] = "minecraft:snowy_mountains",
        [14] = "minecraft:mushroom_fields",
        [15] = "minecraft:mushroom_field_shore",
        [16] = "minecraft:beach",
        [17] = "minecraft:desert_hills",
        [18] = "minecraft:wooded_hills",
        [19] = "minecraft:taiga_hills",
        [20] = "minecraft:mountain_edge",
        [21] = "minecraft:jungle",
        [22] = "minecraft:jungle_hills",
        [23] = "minecraft:jungle_edge",
        [24] = "minecraft:deep_ocean",
        [25] = "minecraft:stone_shore",
        [26] = "minecraft:snowy_beach",
        [27] = "minecraft:birch_forest",
        [28] = "minecraft:birch_forest_hills",
        [29] = "minecraft:dark_forest",
        [30] = "minecraft:snowy_taiga",
        [31] = "minecraft:snowy_taiga_hills",
        [32] = "minecraft:giant_tree_taiga",
        [33] = "minecraft:giant_tree_taiga_hills",
        [34] = "minecraft:wooded_mountains",
        [35] = "minecraft:savanna",
        [36] = "minecraft:savanna_plateau",
        [37] = "minecraft:badlands",
        [38] = "minecraft:wooded_badlands_plateau",
        [39] = "minecraft:badlands_plateau",
    };
}

public sealed record ChunkParseResult(ChunkData? Chunk, string? Error)
{
    public bool Success => Chunk is not null;

    public static ChunkParseResult Ok(ChunkData chunk) => new(chunk, null);

    public static ChunkParseResult Fail(string error) => new(null, error);
}
