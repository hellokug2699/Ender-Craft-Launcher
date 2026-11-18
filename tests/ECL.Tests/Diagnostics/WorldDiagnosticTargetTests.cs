// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Diagnostics;
using Ecl.Minecraft.Diagnostics;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.DependencyInjection;
using WorldDiagnostics = Ecl.World.Diagnostics;
using Xunit;

namespace Ecl.Tests.Diagnostics;

/// <summary>
/// F-10 S6：世界诊断事件 → 统一事实/定位目标，以及“只有明确维度 + 区块坐标 + 非推断才允许地图标记”的硬约束。
/// </summary>
public sealed class WorldDiagnosticTargetTests
{
    [Fact]
    public void ExplicitChunkAndDimensionAllowsMapHighlight()
    {
        var worldEvent = CreateEvent("minecraft:overworld", false, chunkX: 12, chunkZ: -4, confidence: WorldDiagnostics.DiagnosticConfidence.Exact);

        var target = WorldDiagnosticTargetMapping.TryCreate(worldEvent);

        Assert.NotNull(target);
        Assert.True(target!.SupportsMapHighlight);
        Assert.Equal(DiagnosticWorldGranularity.Chunk, target.Granularity);
        Assert.Equal("minecraft:overworld", target.DimensionId);
        Assert.Equal(12, target.ChunkX);
        Assert.Equal(-4, target.ChunkZ);
        Assert.Contains("Chunk (12, -4)", target.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void InferredDimensionIsNeverMapped()
    {
        var worldEvent = CreateEvent("minecraft:the_nether", dimensionInferred: true, chunkX: 3, chunkZ: 7, confidence: WorldDiagnostics.DiagnosticConfidence.Exact);

        var target = WorldDiagnosticTargetMapping.TryCreate(worldEvent);

        Assert.NotNull(target);
        Assert.False(target!.SupportsMapHighlight);
        Assert.False(target.HasExplicitDimension);
        Assert.Contains("维度推断", target.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void RegionLevelLocationIsNeverPromotedToChunk()
    {
        var worldEvent = CreateEvent(
            "minecraft:overworld",
            dimensionInferred: false,
            chunkX: null,
            chunkZ: null,
            confidence: WorldDiagnostics.DiagnosticConfidence.LocationOnly,
            regionX: 1,
            regionZ: 2);

        var target = WorldDiagnosticTargetMapping.TryCreate(worldEvent);

        Assert.NotNull(target);
        Assert.Equal(DiagnosticWorldGranularity.Region, target!.Granularity);
        Assert.Null(target.ChunkX);
        Assert.False(target.SupportsMapHighlight);
        Assert.Contains("r.1.2", target.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerPositionConfidenceCannotBecomeAChunkTarget()
    {
        // 玩家坐标会出现 ChunkX/ChunkZ，但它只说明“玩家在哪”，不说明“哪个区块出问题”。
        var worldEvent = CreateEvent(
            "minecraft:overworld",
            dimensionInferred: false,
            chunkX: 5,
            chunkZ: 6,
            confidence: WorldDiagnostics.DiagnosticConfidence.LocationOnly);

        var target = WorldDiagnosticTargetMapping.TryCreate(worldEvent);

        Assert.NotNull(target);
        Assert.Null(target!.ChunkX);
        Assert.Null(target.ChunkZ);
        Assert.False(target.SupportsMapHighlight);
    }

    [Fact]
    public async Task WorldFactSourceReadsOnlyRegisteredEvidenceAndProducesFactsAndTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-world-facts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var logPath = Path.Combine(root, "game.log");
            await File.WriteAllTextAsync(
                logPath,
                """
                [10:00:00] [main/INFO]: Launching game
                [10:00:01] [Worker-Main-1/ERROR]: Failed to load chunk at (12, -4) dimension=minecraft:overworld
                [10:00:02] [Worker-Main-1/ERROR]: Failed to load chunk at (13, -4) time_nether the_nether
                """);

            var instance = new GameInstance
            {
                Id = "demo",
                Name = "Demo",
                GameVersion = "1.21.1",
                VersionId = "1.21.1",
                GameDirectory = Path.Combine(root, "game"),
            };
            var session = new GameLogSession(
                new SessionManifest { SessionId = "session-1", InstanceId = instance.Id },
                Path.Combine(root, "session"));
            var evidence = new[]
            {
                new SessionEvidenceItem(
                    new SessionArtifact
                    {
                        Id = "game_log:game.log",
                        Kind = SessionArtifactKinds.GameLog,
                        SourcePath = logPath,
                        CapturedPath = logPath,
                        Ownership = ArtifactOwnership.Direct,
                    },
                    SessionSourceAvailability.Available,
                    new FileInfo(logPath).Length,
                    null,
                    logPath,
                    null),
            };

            var source = new WorldEventFactSource(new WorldDiagnostics.WorldDiagnosticsParser());
            var collected = await source.CollectAsync(
                new DiagnosticFactContext(instance.Id, instance, session, evidence),
                CancellationToken.None);

            Assert.NotEmpty(collected.Facts);
            Assert.All(
                collected.Facts,
                fact => Assert.True(fact.Kind.EndsWith("_anomaly", StringComparison.Ordinal), fact.Kind));
            var chunkFact = Assert.Single(
                collected.Facts,
                fact => fact.Attributes.TryGetValue("chunkX", out var chunk) && chunk == "12");
            Assert.Equal("minecraft:overworld", chunkFact.Related);
            Assert.Equal("true", chunkFact.Attributes["supportsMapHighlight"]);

            var target = Assert.Single(collected.Targets, item => item.SupportsMapHighlight);
            Assert.Equal(12, target.ChunkX);
            Assert.Equal("minecraft:overworld", target.DimensionId);
            // 第二条日志的维度来自名称线索（the_nether），只能作为推断定位，不可用于地图标记。
            Assert.Contains(
                collected.Targets,
                item => item.DimensionInferred && !item.SupportsMapHighlight);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task AnalysisServiceIncludesWorldTargetsOnlyWhenEnabled()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var instance = (await instances.CreateAsync("World Diag", "1.21.1", "fabric", "0.16.0")).Value!;
        var sessions = provider.GetRequiredService<IGameSessionService>();
        var session = sessions.Create(new SessionCreateRequest
        {
            InstanceId = instance.Id,
            MinecraftVersion = "1.21.1",
            Loader = "fabric",
        });
        var logPath = Path.Combine(environment.Root, "world-game.log");
        await File.WriteAllTextAsync(
            logPath,
            "[10:00:01] [Worker-Main-1/ERROR]: Failed to load chunk at (12, -4) dimension=minecraft:overworld\n");
        sessions.RegisterArtifact(session.SessionId, new SessionArtifact
        {
            Id = "game_log:world-game.log",
            Kind = SessionArtifactKinds.GameLog,
            SourcePath = logPath,
            CapturedPath = logPath,
            Ownership = ArtifactOwnership.Direct,
        });

        var service = provider.GetRequiredService<IDiagnosticAnalysisService>();
        var withoutWorld = await service.AnalyzeAsync(
            instance.Id,
            new DiagnosticAnalysisOptions { IncludeWorldEvents = false, SaveHistory = false });
        Assert.True(withoutWorld.IsSuccess, withoutWorld.Error?.ToString());
        Assert.Empty(withoutWorld.Value!.WorldTargets);
        Assert.DoesNotContain(withoutWorld.Value.Facts, fact => fact.Kind.StartsWith("world.", StringComparison.Ordinal));

        var withWorld = await service.AnalyzeAsync(
            instance.Id,
            new DiagnosticAnalysisOptions { IncludeWorldEvents = true, SaveHistory = false });
        Assert.True(withWorld.IsSuccess, withWorld.Error?.ToString());
        var target = Assert.Single(withWorld.Value!.WorldTargets, item => item.SupportsMapHighlight);
        Assert.Equal("minecraft:overworld", target.DimensionId);
        Assert.Contains(
            withWorld.Value.Facts,
            fact => fact.Kind == DiagnosticFactKinds.WorldChunkAnomaly);
    }

    private static WorldDiagnostics.DiagnosticEvent CreateEvent(
        string? dimensionId,
        bool dimensionInferred,
        int? chunkX,
        int? chunkZ,
        WorldDiagnostics.DiagnosticConfidence confidence,
        int? regionX = null,
        int? regionZ = null) => new()
        {
            InstanceId = "demo",
            Category = WorldDiagnostics.DiagnosticCategory.ChunkLoad,
            Severity = WorldDiagnostics.DiagnosticSeverity.Medium,
            Summary = "区块加载异常",
            Location = new WorldDiagnostics.DiagnosticLocation(
                dimensionId,
                dimensionInferred,
                chunkX,
                chunkZ,
                null,
                null,
                null,
                regionX,
                regionZ,
                confidence),
            Hypotheses = [],
            SuggestedActions = [],
            Evidence = [new WorldDiagnostics.DiagnosticEvidence("game-log", "game.log", 1, "line")],
        };

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不影响断言。
        }
    }
}
