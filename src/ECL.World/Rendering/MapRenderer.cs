// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.World.Region;
using Ecl.World.Worlds;

namespace Ecl.World.Rendering;

public enum MapLayer
{
    Height,
    Biome,
    Surface,
    ChunkStatus,
}

public enum MapHighlightKind
{
    Diagnostic,
    Selected,
}

public sealed record MapHighlight(int ChunkX, int ChunkZ, MapHighlightKind Kind);

public sealed record MapLegendEntry(string Key, uint Color, string Label);

/// <summary>
/// 与 UI 无关的像素结果，颜色为 <c>0xAARRGGBB</c>。缺失/未知内容使用明确的中性棋盘或标记色，
/// 不会用草地、海洋等原版颜色冒充。
/// </summary>
public sealed record MapImage(
    int Width,
    int Height,
    uint[] Pixels,
    int MinChunkX,
    int MinChunkZ,
    int MaxChunkX,
    int MaxChunkZ,
    int PixelsPerBlock,
    int KnownBiomePixels,
    int UnknownBiomePixels,
    IReadOnlyList<MapLegendEntry> Legend)
{
    public bool IsEmpty => Width == 0 || Height == 0;
}

public static class MapRenderer
{
    private const uint MissingA = 0xFF181820;
    private const uint MissingB = 0xFF24242E;
    private const uint Unknown = 0xFFFF4FD8;
    private const uint HighlightDiagnostic = 0xFFF4797F;
    private const uint HighlightSelected = 0xFFF3F2F7;

    public static MapImage Render(
        IReadOnlyList<ChunkLoadResult> chunks,
        MapLayer layer,
        int pixelsPerBlock = 2,
        IReadOnlyList<MapHighlight>? highlights = null)
    {
        pixelsPerBlock = Math.Clamp(pixelsPerBlock, 1, 8);
        if (chunks.Count == 0)
        {
            return new MapImage(0, 0, [], 0, 0, 0, 0, pixelsPerBlock, 0, 0, BuildLegend(layer));
        }

        var minChunkX = chunks.Min(chunk => chunk.X);
        var maxChunkX = chunks.Max(chunk => chunk.X);
        var minChunkZ = chunks.Min(chunk => chunk.Z);
        var maxChunkZ = chunks.Max(chunk => chunk.Z);
        var width = (maxChunkX - minChunkX + 1) * 16 * pixelsPerBlock;
        var height = (maxChunkZ - minChunkZ + 1) * 16 * pixelsPerBlock;
        if ((long)width * height > 8192L * 8192L)
        {
            return new MapImage(0, 0, [], minChunkX, minChunkZ, maxChunkX, maxChunkZ, pixelsPerBlock, 0, 0, BuildLegend(layer));
        }

        var pixels = new uint[width * height];
        var knownBiomes = 0;
        var unknownBiomes = 0;
        foreach (var chunk in chunks)
        {
            var baseX = (chunk.X - minChunkX) * 16 * pixelsPerBlock;
            var baseZ = (chunk.Z - minChunkZ) * 16 * pixelsPerBlock;
            for (var localZ = 0; localZ < 16; localZ++)
            {
                for (var localX = 0; localX < 16; localX++)
                {
                    var index = localZ * 16 + localX;
                    var color = layer switch
                    {
                        MapLayer.Height => HeightColor(chunk, index),
                        MapLayer.Biome => BiomeColor(chunk, index, ref knownBiomes, ref unknownBiomes),
                        MapLayer.Surface => SurfaceColor(chunk, index),
                        MapLayer.ChunkStatus => StatusColor(chunk.Status),
                        _ => MissingColor(baseX + localX, baseZ + localZ),
                    };
                    if (chunk.Status is RegionChunkStatus.Corrupt or RegionChunkStatus.Missing or RegionChunkStatus.UnsupportedCompression)
                    {
                        color = MissingColor(baseX + localX, baseZ + localZ);
                    }

                    for (var py = 0; py < pixelsPerBlock; py++)
                    {
                        for (var px = 0; px < pixelsPerBlock; px++)
                        {
                            var x = baseX + localX * pixelsPerBlock + px;
                            var z = baseZ + localZ * pixelsPerBlock + py;
                            pixels[z * width + x] = color;
                        }
                    }
                }
            }
        }

        if (highlights is not null)
        {
            foreach (var highlight in highlights)
            {
                DrawChunkBorder(
                    pixels,
                    width,
                    height,
                    minChunkX,
                    minChunkZ,
                    highlight,
                    pixelsPerBlock);
            }
        }

        return new MapImage(
            width,
            height,
            pixels,
            minChunkX,
            minChunkZ,
            maxChunkX,
            maxChunkZ,
            pixelsPerBlock,
            knownBiomes,
            unknownBiomes,
            BuildLegend(layer));
    }

    private static void DrawChunkBorder(
        uint[] pixels,
        int width,
        int height,
        int minChunkX,
        int minChunkZ,
        MapHighlight highlight,
        int pixelsPerBlock)
    {
        var x0 = (highlight.ChunkX - minChunkX) * 16 * pixelsPerBlock;
        var z0 = (highlight.ChunkZ - minChunkZ) * 16 * pixelsPerBlock;
        var size = 16 * pixelsPerBlock;
        if (x0 < 0 || z0 < 0 || x0 + size > width || z0 + size > height)
        {
            return;
        }

        var color = highlight.Kind == MapHighlightKind.Diagnostic ? HighlightDiagnostic : HighlightSelected;
        for (var offset = 0; offset < size; offset++)
        {
            SetPixel(pixels, width, height, x0 + offset, z0, color);
            SetPixel(pixels, width, height, x0 + offset, z0 + size - 1, color);
            SetPixel(pixels, width, height, x0, z0 + offset, color);
            SetPixel(pixels, width, height, x0 + size - 1, z0 + offset, color);
        }
    }

    private static void SetPixel(uint[] pixels, int width, int height, int x, int z, uint color)
    {
        if ((uint)x < (uint)width && (uint)z < (uint)height)
        {
            pixels[z * width + x] = color;
        }
    }

    private static uint HeightColor(ChunkLoadResult chunk, int index)
    {
        if (chunk.Chunk?.SurfaceHeights[index] is not { } y)
        {
            return MissingColor(index % 16, index / 16);
        }

        var block = chunk.Chunk.SurfaceBlocks[index];
        if (block is not null && block.Contains("water", StringComparison.OrdinalIgnoreCase))
        {
            return y < 40 ? 0xFF142B5E : 0xFF1E4F8F;
        }

        if (y < 50)
        {
            return 0xFF3F6B45;
        }

        if (y < 80)
        {
            return (uint)(0xFF4B7A4B + Math.Clamp((y - 50) * 3, 0, 40) * 0x00010100);
        }

        if (y < 120)
        {
            return 0xFF7A7466;
        }

        return y < 180 ? 0xFFAAA49A : 0xFFE8E8E8;
    }

    private static uint BiomeColor(ChunkLoadResult chunk, int index, ref int known, ref int unknown)
    {
        if (chunk.Chunk is null)
        {
            return MissingColor(index % 16, index / 16);
        }

        var biome = chunk.Chunk.BiomeIds[index];
        if (string.IsNullOrWhiteSpace(biome))
        {
            unknown++;
            return Unknown;
        }

        if (BiomeColors.TryGetValue(biome, out var color))
        {
            known++;
            return color;
        }

        unknown++;
        return Unknown;
    }

    private static uint SurfaceColor(ChunkLoadResult chunk, int index)
    {
        var block = chunk.Chunk?.SurfaceBlocks[index];
        if (string.IsNullOrWhiteSpace(block))
        {
            return MissingColor(index % 16, index / 16);
        }

        var name = block;
        var bracket = name.IndexOf('[', StringComparison.Ordinal);
        if (bracket > 0)
        {
            name = name[..bracket];
        }

        return BlockColors.TryGetValue(name, out var color) ? color : Unknown;
    }

    private static uint StatusColor(RegionChunkStatus status) => status switch
    {
        RegionChunkStatus.Ok => 0xFF3F8F5F,
        RegionChunkStatus.Missing => 0xFF2A2A33,
        RegionChunkStatus.Corrupt => 0xFFB23A48,
        RegionChunkStatus.UnsupportedCompression => 0xFF8A4FD8,
        _ => Unknown,
    };

    private static uint MissingColor(int x, int z) => ((x + z) & 1) == 0 ? MissingA : MissingB;

    private static IReadOnlyList<MapLegendEntry> BuildLegend(MapLayer layer) => layer switch
    {
        MapLayer.Height =>
        [
            new("water", 0xFF1E4F8F, "水面"),
            new("low", 0xFF3F6B45, "低地"),
            new("mid", 0xFF7A7466, "丘陵"),
            new("high", 0xFFE8E8E8, "高山"),
            new("missing", MissingA, "缺失"),
        ],
        MapLayer.Biome =>
        [
            new("known", 0xFF5A9E52, "已识别群系"),
            new("unknown", Unknown, "未知群系"),
            new("missing", MissingA, "缺失"),
        ],
        MapLayer.Surface =>
        [
            new("grass", 0xFF5B8F3E, "草/植被"),
            new("stone", 0xFF7D7D7D, "石质"),
            new("sand", 0xFFD9CC8C, "沙"),
            new("water", 0xFF1E4F8F, "水"),
            new("unknown", Unknown, "未知方块"),
        ],
        _ =>
        [
            new("full", 0xFF3F8F5F, "完整"),
            new("partial", 0xFFE9B949, "部分"),
            new("corrupt", 0xFFB23A48, "损坏"),
            new("missing", MissingA, "缺失"),
        ],
    };

    private static readonly Dictionary<string, uint> BiomeColors = new(StringComparer.Ordinal)
    {
        ["minecraft:ocean"] = 0xFF3E6EA8,
        ["minecraft:deep_ocean"] = 0xFF2B4C7E,
        ["minecraft:warm_ocean"] = 0xFF3D8FB0,
        ["minecraft:lukewarm_ocean"] = 0xFF4287A8,
        ["minecraft:cold_ocean"] = 0xFF3A6D9C,
        ["minecraft:deep_cold_ocean"] = 0xFF2E537A,
        ["minecraft:frozen_ocean"] = 0xFF6C93B8,
        ["minecraft:river"] = 0xFF4C86C0,
        ["minecraft:frozen_river"] = 0xFF7BA4C8,
        ["minecraft:beach"] = 0xFFD9CC8C,
        ["minecraft:snowy_beach"] = 0xFFE8E8E8,
        ["minecraft:plains"] = 0xFF7CB45A,
        ["minecraft:sunflower_plains"] = 0xFF8FBE5F,
        ["minecraft:snowy_plains"] = 0xFFDDE8E8,
        ["minecraft:desert"] = 0xFFD9C27A,
        ["minecraft:forest"] = 0xFF3F7A3A,
        ["minecraft:flower_forest"] = 0xFF5A9E52,
        ["minecraft:birch_forest"] = 0xFF78A86A,
        ["minecraft:dark_forest"] = 0xFF2E5E2C,
        ["minecraft:taiga"] = 0xFF3F6B50,
        ["minecraft:snowy_taiga"] = 0xFFAFC4C4,
        ["minecraft:swamp"] = 0xFF4F6B3A,
        ["minecraft:mangrove_swamp"] = 0xFF4A6B45,
        ["minecraft:jungle"] = 0xFF2E8F3A,
        ["minecraft:sparse_jungle"] = 0xFF4C9E4A,
        ["minecraft:bamboo_jungle"] = 0xFF3E9E44,
        ["minecraft:savanna"] = 0xFFB0A85A,
        ["minecraft:savanna_plateau"] = 0xFFC0B86A,
        ["minecraft:badlands"] = 0xFFB07A4A,
        ["minecraft:eroded_badlands"] = 0xFF9A643A,
        ["minecraft:wooded_badlands"] = 0xFF8F6A3A,
        ["minecraft:meadow"] = 0xFF8FBE6A,
        ["minecraft:grove"] = 0xFF6A8F7A,
        ["minecraft:snowy_slopes"] = 0xFFE0E8F0,
        ["minecraft:jagged_peaks"] = 0xFFBFC8D0,
        ["minecraft:frozen_peaks"] = 0xFFE8F0F8,
        ["minecraft:stony_peaks"] = 0xFF8F8F8A,
        ["minecraft:mushroom_fields"] = 0xFFA87A9E,
        ["minecraft:lush_caves"] = 0xFF4F8F4A,
        ["minecraft:dripstone_caves"] = 0xFF9A8A6A,
        ["minecraft:deep_dark"] = 0xFF202A38,
        ["minecraft:nether_wastes"] = 0xFF8F3A2A,
        ["minecraft:soul_sand_valley"] = 0xFF6A4A3A,
        ["minecraft:crimson_forest"] = 0xFF9A2A2A,
        ["minecraft:warped_forest"] = 0xFF2A8F8A,
        ["minecraft:basalt_deltas"] = 0xFF5A5A5A,
        ["minecraft:the_end"] = 0xFFB8A8C8,
        ["minecraft:end_highlands"] = 0xFFC8B8A8,
        ["minecraft:end_midlands"] = 0xFFA898B8,
        ["minecraft:small_end_islands"] = 0xFF9A8AA8,
        ["minecraft:end_barrens"] = 0xFF8A7A98,
    };

    private static readonly Dictionary<string, uint> BlockColors = new(StringComparer.Ordinal)
    {
        ["minecraft:grass_block"] = 0xFF5B8F3E,
        ["minecraft:dirt"] = 0xFF7A5A3A,
        ["minecraft:coarse_dirt"] = 0xFF6A4A2A,
        ["minecraft:podzol"] = 0xFF5A4A2A,
        ["minecraft:mycelium"] = 0xFF8A7A8A,
        ["minecraft:stone"] = 0xFF7D7D7D,
        ["minecraft:granite"] = 0xFF9A6A5A,
        ["minecraft:diorite"] = 0xFFB0B0B0,
        ["minecraft:andesite"] = 0xFF8A8A8A,
        ["minecraft:deepslate"] = 0xFF3A3A3E,
        ["minecraft:tuff"] = 0xFF5A5A50,
        ["minecraft:calcite"] = 0xFFD8D8D0,
        ["minecraft:sand"] = 0xFFD9CC8C,
        ["minecraft:red_sand"] = 0xFFB06A3A,
        ["minecraft:sandstone"] = 0xFFC8B87A,
        ["minecraft:gravel"] = 0xFF8A8A8A,
        ["minecraft:water"] = 0xFF1E4F8F,
        ["minecraft:lava"] = 0xFFE86A1A,
        ["minecraft:snow"] = 0xFFF0F0F0,
        ["minecraft:snow_block"] = 0xFFF0F0F0,
        ["minecraft:ice"] = 0xFFA8D8F0,
        ["minecraft:packed_ice"] = 0xFF8FB8D8,
        ["minecraft:blue_ice"] = 0xFF6A9AC8,
        ["minecraft:netherrack"] = 0xFF8F3A3A,
        ["minecraft:nether_wart_block"] = 0xFF9A2A2A,
        ["minecraft:soul_sand"] = 0xFF5A4A3A,
        ["minecraft:soul_soil"] = 0xFF4A3A2A,
        ["minecraft:basalt"] = 0xFF5A5A5A,
        ["minecraft:blackstone"] = 0xFF2A2A2E,
        ["minecraft:end_stone"] = 0xFFD8D8A8,
        ["minecraft:chorus_plant"] = 0xFF6A4A6A,
        ["minecraft:moss_block"] = 0xFF4A7A3A,
        ["minecraft:mud"] = 0xFF4A3A3A,
        ["minecraft:clay"] = 0xFFA0A8B8,
        ["minecraft:terracotta"] = 0xFF9A5A3A,
        ["minecraft:bedrock"] = 0xFF1A1A1A,
    };
}
