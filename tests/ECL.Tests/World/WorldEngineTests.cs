// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ecl.World.Chunks;
using Ecl.World.Diagnostics;
using Ecl.World.Nbt;
using Ecl.World.Recent;
using Ecl.World.Region;
using Ecl.World.Rendering;
using Ecl.World.Semantics;
using Ecl.World.Servers;
using Ecl.World.Worlds;
using Xunit;

namespace Ecl.Tests.World;

public sealed class WorldEngineTests
{
    [Fact]
    public void NbtRoundTripsCompressedAllTypes()
    {
        var root = NbtTag.Compound()
            .Add("byte", NbtTagType.Byte, (byte)7)
            .Add("short", NbtTagType.Short, (short)-12)
            .Add("int", NbtTagType.Int, 123456)
            .Add("long", NbtTagType.Long, -9876543210L)
            .Add("float", NbtTagType.Float, 1.5f)
            .Add("double", NbtTagType.Double, -2.25d)
            .Add("string", NbtTagType.String, "你好 NBT")
            .Add("bytes", NbtTagType.ByteArray, new byte[] { 1, 2, 3 })
            .Add("ints", NbtTagType.IntArray, new[] { -1, 0, 1 })
            .Add("longs", NbtTagType.LongArray, new[] { long.MinValue, long.MaxValue });
        var list = NbtTag.List("list", NbtTagType.Int);
        list.AddTo(new NbtTag(NbtTagType.Int, string.Empty, 4));
        list.AddTo(new NbtTag(NbtTagType.Int, string.Empty, 5));
        root.Add(list);

        using var compressed = new MemoryStream();
        NbtFile.Write(compressed, root, NbtCompression.Zlib);
        var parsed = NbtFile.Read(new MemoryStream(compressed.ToArray()));

        Assert.Equal((byte)7, parsed.Child("byte")!.GetByte());
        Assert.Equal(-12, parsed.Child("short")!.GetShort());
        Assert.Equal(123456, parsed.Child("int")!.GetInt());
        Assert.Equal(-9876543210L, parsed.Child("long")!.GetLong());
        Assert.Equal("你好 NBT", parsed.Child("string")!.GetString());
        Assert.Equal(3, parsed.Child("bytes")!.GetByteArray().Length);
        Assert.Equal(2, parsed.Child("list")!.Children.Count);
        Assert.Equal(5, parsed.Child("list")!.Children[1].GetInt());
    }

    [Fact]
    public void RegionReadsChunkAndIsolatesMissingAndCorruptEntries()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "r.-1.-2.mca");
            var chunk = BuildChunk(-1, -2);
            WriteRegion(path, 31, 30, chunk);

            using var region = new RegionFile(path);
            var read = region.ReadChunk(31, 30);
            Assert.Equal(RegionChunkStatus.Ok, read.Status);
            Assert.NotNull(read.Data);

            var missing = region.ReadChunk(0, 0);
            Assert.Equal(RegionChunkStatus.Missing, missing.Status);

            var header = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan((31 + 30 * 32) * 4, 4), 0xFFFF01);
            File.WriteAllBytes(path, header);
            using var corrupt = new RegionFile(path);
            Assert.Equal(RegionChunkStatus.Corrupt, corrupt.ReadChunk(31, 30).Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ChunkParsesSurfaceBiomeAndNegativeCoordinates()
    {
        var parsed = ChunkData.Parse(BuildChunk(-3, 5));
        Assert.True(parsed.Success);
        var chunk = parsed.Chunk!;
        Assert.Equal(-3, chunk.X);
        Assert.Equal(5, chunk.Z);
        Assert.Equal(10, chunk.SurfaceHeights[0]);
        Assert.Equal("minecraft:stone", chunk.SurfaceBlocks[0]);
        Assert.Equal("minecraft:plains", chunk.BiomeIds[0]);
        Assert.Equal(256, chunk.BiomeIds.Count(value => value is not null));
    }

    [Fact]
    public async Task WorldSaveScanBackupAndDeleteStayInsideSaves()
    {
        var directory = CreateTempDirectory();
        try
        {
            var game = Path.Combine(directory, "game");
            var world = Path.Combine(game, "saves", "TestWorld");
            Directory.CreateDirectory(Path.Combine(world, "region"));
            WriteLevelDat(Path.Combine(world, "level.dat"), "TestWorld");
            File.WriteAllText(Path.Combine(world, "region", "r.0.0.mca"), "not-a-real-region-for-size-test");

            var service = new WorldSaveService(game);
            var scan = await service.ScanAsync();
            var save = Assert.Single(scan.Worlds);
            Assert.Equal("TestWorld", save.Name);
            Assert.Equal("1.20.1", save.Level.VersionName);
            Assert.Contains(save.Dimensions, dimension => dimension.Id == "minecraft:overworld");

            var backup = await service.BackupAsync(save, Path.Combine(directory, "backups"));
            Assert.True(backup.Success);
            Assert.True(File.Exists(Path.Combine(backup.Value!.DirectoryPath, "level.dat")));

            var outside = save with { DirectoryPath = Path.Combine(directory, "outside") };
            var refused = await service.DeleteAsync(outside);
            Assert.False(refused.Success);

            var deleted = await service.DeleteAsync(save);
            Assert.True(deleted.Success);
            Assert.False(Directory.Exists(world));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ServerListParsesPortsIpv6AndIcons()
    {
        var directory = CreateTempDirectory();
        try
        {
            var root = NbtTag.Compound();
            var servers = NbtTag.List("servers", NbtTagType.Compound);
            servers.AddTo(NbtTag.Compound()
                .Add("name", NbtTagType.String, "Survival")
                .Add("ip", NbtTagType.String, "example.com:25566"));
            servers.AddTo(NbtTag.Compound()
                .Add("name", NbtTagType.String, "Local")
                .Add("ip", NbtTagType.String, "[::1]:25565"));
            root.Add(servers);
            NbtFile.WriteFile(Path.Combine(directory, "servers.dat"), root);

            var result = await new ServerListService(directory).ReadAsync();
            Assert.True(result.IsReadable);
            Assert.Equal(2, result.Servers.Count);
            Assert.Equal(25566, result.Servers[0].Port);
            Assert.Equal("::1", result.Servers[1].Host);
            Assert.Equal("example.com", result.Servers[0].DisplayAddress.Split(':')[0]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ServerPingReadsStatusAndRejectsOversizedFrame()
    {
        var (listener, port, json) = await StartStatusServer();
        try
        {
            using var service = new ServerPingService(timeout: TimeSpan.FromSeconds(3));
            var result = await service.PingAsync("127.0.0.1", port);
            Assert.Equal(ServerPingStatus.Online, result.Status);
            Assert.Equal("Hello ECL", result.Motd);
            Assert.Equal(3, result.PlayersOnline);
            Assert.Equal("1.20.1", result.VersionName);
            Assert.NotNull(result.LatencyMilliseconds);
            Assert.Equal("{\"version\":{\"name\":\"1.20.1\",\"protocol\":763},\"players\":{\"online\":3,\"max\":20},\"description\":{\"text\":\"Hello ECL\"}}", json);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task DiagnosticsExtractsNegativeChunksAndKeepsRenderLowRisk()
    {
        var directory = CreateTempDirectory();
        try
        {
            var log = Path.Combine(directory, "latest.log");
            await File.WriteAllLinesAsync(
                log,
                [
                    "[12:00:00] [Render thread/ERROR]: Entity Rendering Exception for minecraft:zombie at Chunk [ -1, -2 ]",
                    "java.lang.IndexOutOfBoundsException: render",
                    "[12:00:01] [Server thread/ERROR]: Failed to load chunk at 32, -1 in minecraft:the_nether",
                    "[12:00:02] [Server thread/ERROR]: Failed to load chunk at 32, -1 in minecraft:the_nether",
                ]);
            var result = await new WorldDiagnosticsParser().AnalyzeAsync("instance-a", "session-1", [log]);
            Assert.Null(result.Error);
            var render = Assert.Single(result.Events, item => item.Category == DiagnosticCategory.EntityRender);
            Assert.Equal(DiagnosticSeverity.Low, render.Severity);
            Assert.Equal(-1, render.Location.ChunkX);
            Assert.Equal(-2, render.Location.ChunkZ);

            var chunkLoad = Assert.Single(result.Events, item => item.Category == DiagnosticCategory.ChunkLoad);
            Assert.Equal(2, chunkLoad.OccurrenceCount);
            Assert.Equal("minecraft:the_nether", chunkLoad.Location.DimensionId);
            Assert.Equal(32, chunkLoad.Location.ChunkX);
            Assert.Equal(-1, chunkLoad.Location.ChunkZ);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecentAccessDeduplicatesAndToleratesCorruption()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "recent.json");
            var store = new RecentAccessStore(path);
            await store.RecordAsync(new RecentAccessEntry
            {
                Kind = RecentAccessKind.World,
                InstanceId = "i1",
                TargetId = "world",
                DisplayName = "World",
            });
            await store.RecordAsync(new RecentAccessEntry
            {
                Kind = RecentAccessKind.World,
                InstanceId = "i1",
                TargetId = "world",
                DisplayName = "World renamed",
            });

            var snapshot = await store.LoadAsync();
            var entry = Assert.Single(snapshot.Entries);
            Assert.Equal("World renamed", entry.DisplayName);

            await File.WriteAllTextAsync(path, "{not json");
            var corrupt = await store.LoadAsync();
            Assert.True(corrupt.IsCorrupt);
            Assert.Empty(corrupt.Entries);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MapRendererUsesExplicitUnknownColorsAndHighlights()
    {
        var chunk = ChunkData.Parse(BuildChunk(0, 0)).Chunk!;
        for (var index = 0; index < chunk.BiomeIds.Length; index++)
        {
            chunk.BiomeIds[index] = "minecraft:plains";
        }

        chunk.BiomeIds[0] = "custom:unknown_biome";
        var results = new[]
        {
            new ChunkLoadResult(0, 0, chunk, RegionChunkStatus.Ok, null),
            new ChunkLoadResult(1, 0, null, RegionChunkStatus.Missing, null),
        };

        var biome = MapRenderer.Render(results, MapLayer.Biome, pixelsPerBlock: 1);
        Assert.Equal(32, biome.Width);
        Assert.Equal(1, biome.UnknownBiomePixels);
        Assert.Equal(0xFFFF4FD8u, biome.Pixels[0]);
        Assert.NotEqual(biome.Pixels[0], biome.Pixels[16]);

        var status = MapRenderer.Render(
            results,
            MapLayer.ChunkStatus,
            pixelsPerBlock: 1,
            highlights: [new MapHighlight(1, 0, MapHighlightKind.Diagnostic)]);
        Assert.Equal(0xFFF4797F, status.Pixels[16]);
    }

    [Fact]
    public void SemanticCatalogMatchesPathAndTypeAndEditSessionTracksChanges()
    {
        var catalog = NbtSemanticCatalog.LoadDefault();
        var matches = catalog.Query("level.dat", 3955, "Data/LevelName", "LevelName", "zh-CN");
        Assert.NotEmpty(matches);
        Assert.Equal("世界名称", matches[0].Definition.DisplayName.Get("zh-CN"));

        var root = NbtTag.Compound()
            .Add(NbtTag.Compound("Data"));
        var data = root.Child("Data")!;
        data.Add("LevelName", NbtTagType.String, "Before");
        data.Add("Health", NbtTagType.Float, 20f);
        var session = new NbtEditSession(root);
        session.Set("Data/LevelName", new NbtTag(NbtTagType.String, "LevelName", "After"));
        Assert.Single(session.Changes);
        Assert.Contains("Data", session.AggregatedAncestorPaths);
        Assert.True(session.CanSave(session.Validate(catalog, "level.dat", 3955)));

        session.Set("Data/LevelName", new NbtTag(NbtTagType.Int, "LevelName", 42));
        var issues = session.Validate(catalog, "level.dat", 3955);
        Assert.Contains(issues, issue => issue.Code == "nbt.type_mismatch" && issue.Severity == NbtValidationSeverity.Error);
        Assert.False(session.CanSave(issues));

        Assert.True(session.Undo());
        Assert.True(session.Undo());
        Assert.False(session.HasChanges);
    }

    private static async Task<(TcpListener Listener, int Port, string Json)> StartStatusServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string json = "{\"version\":{\"name\":\"1.20.1\",\"protocol\":763},\"players\":{\"online\":3,\"max\":20},\"description\":{\"text\":\"Hello ECL\"}}";
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ReadFrameAsync(stream);
            await ReadFrameAsync(stream);
            var payload = new MemoryStream();
            WriteVarInt(payload, 0);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            WriteVarInt(payload, jsonBytes.Length);
            payload.Write(jsonBytes);
            var frame = new MemoryStream();
            WriteVarInt(frame, (int)payload.Length);
            payload.Position = 0;
            payload.CopyTo(frame);
            await stream.WriteAsync(frame.ToArray());
            await stream.FlushAsync();
        });
        await Task.Yield();
        return (listener, port, json);
    }

    private static async Task ReadFrameAsync(NetworkStream stream)
    {
        var length = await ReadVarIntAsync(stream);
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream)
    {
        var value = 0;
        for (var index = 0; index < 5; index++)
        {
            var buffer = new byte[1];
            await stream.ReadExactlyAsync(buffer);
            value |= (buffer[0] & 0x7F) << (index * 7);
            if ((buffer[0] & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("VarInt too long");
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            stream.WriteByte((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        stream.WriteByte((byte)remaining);
    }

    private static NbtTag BuildChunk(int x, int z)
    {
        var section = NbtTag.Compound()
            .Add("Y", NbtTagType.Byte, (byte)0);
        var blockStates = NbtTag.Compound("block_states");
        var palette = NbtTag.List("palette", NbtTagType.Compound);
        palette.AddTo(NbtTag.Compound().Add("Name", NbtTagType.String, "minecraft:air"));
        palette.AddTo(NbtTag.Compound().Add("Name", NbtTagType.String, "minecraft:stone"));
        palette.AddTo(NbtTag.Compound().Add("Name", NbtTagType.String, "minecraft:grass_block"));
        var blocks = new long[256];
        blocks[160] = 1; // y=10, x=0, z=0 => palette index 1 (stone)
        blockStates.Add(palette);
        blockStates.Add("data", NbtTagType.LongArray, blocks);
        section.Add(blockStates);

        var biomes = NbtTag.Compound("biomes");
        var biomePalette = NbtTag.List("palette", NbtTagType.String);
        biomePalette.AddTo(new NbtTag(NbtTagType.String, string.Empty, "minecraft:plains"));
        biomes.Add(biomePalette);
        biomes.Add("data", NbtTagType.LongArray, new long[] { 0 });
        section.Add(biomes);

        var sections = NbtTag.List("sections", NbtTagType.Compound);
        sections.AddTo(section);

        var heightmaps = NbtTag.Compound("Heightmaps");
        var heightmap = new long[37];
        for (var index = 0; index < 256; index++)
        {
            heightmap[index / 7] |= 10L << ((index % 7) * 9);
        }

        heightmaps.Add("WORLD_SURFACE", NbtTagType.LongArray, heightmap);

        return NbtTag.Compound()
            .Add("DataVersion", NbtTagType.Int, 3955)
            .Add("xPos", NbtTagType.Int, x)
            .Add("zPos", NbtTagType.Int, z)
            .Add("Status", NbtTagType.String, "full")
            .Add(sections)
            .Add(heightmaps);
    }

    private static void WriteLevelDat(string path, string name)
    {
        var data = NbtTag.Compound("Data")
            .Add("LevelName", NbtTagType.String, name)
            .Add("DataVersion", NbtTagType.Int, 3955)
            .Add("Seed", NbtTagType.Long, 123456789L)
            .Add("GameType", NbtTagType.Int, 0)
            .Add("Difficulty", NbtTagType.Byte, (byte)2)
            .Add("DayTime", NbtTagType.Long, 6000L)
            .Add("LastPlayed", NbtTagType.Long, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var version = NbtTag.Compound("Version").Add("Name", NbtTagType.String, "1.20.1");
        data.Add(version);
        NbtFile.WriteFile(path, NbtTag.Compound().Add(data), NbtCompression.Gzip);
    }

    private static void WriteRegion(string path, int localX, int localZ, NbtTag chunk)
    {
        using var raw = new MemoryStream();
        NbtFile.Write(raw, chunk, NbtCompression.None);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        var payload = compressed.ToArray();
        var length = 1 + payload.Length;
        var sectors = (length + 4 + RegionFile.SectorSize - 1) / RegionFile.SectorSize;
        var file = new byte[8192 + sectors * RegionFile.SectorSize];
        var location = (2 << 8) | sectors;
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan((localX + localZ * 32) * 4, 4), (uint)location);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(8192, 4), length);
        file[8196] = 2;
        payload.CopyTo(file.AsSpan(8197));
        File.WriteAllBytes(path, file);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ecl-world-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
