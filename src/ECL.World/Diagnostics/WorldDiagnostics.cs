// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Ecl.World.Diagnostics;

public enum DiagnosticConfidence
{
    Exact,
    Derived,
    Inferred,
    LocationOnly,
    Unknown,
}

public enum DiagnosticSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

public enum DiagnosticCategory
{
    ChunkLoad,
    ChunkDecode,
    NbtDecode,
    EntityTick,
    EntityRender,
    RegionAccess,
    Unknown,
}

public enum DiagnosticStatus
{
    New,
    Checked,
    Ignored,
    Fixed,
    Unconfirmed,
}

public sealed record DiagnosticEvidence(string SourceKind, string SourceFile, int? LineNumber, string Excerpt);

public sealed record DiagnosticLocation(
    string? DimensionId,
    bool DimensionInferred,
    int? ChunkX,
    int? ChunkZ,
    int? BlockX,
    int? BlockY,
    int? BlockZ,
    int? RegionX,
    int? RegionZ,
    DiagnosticConfidence Confidence)
{
    public bool HasChunk => ChunkX.HasValue && ChunkZ.HasValue;

    public static DiagnosticLocation Unknown => new(null, false, null, null, null, null, null, null, null, DiagnosticConfidence.Unknown);
}

public sealed record DiagnosticEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string InstanceId { get; init; }

    public string? SessionId { get; init; }

    public DiagnosticCategory Category { get; init; }

    public DiagnosticSeverity Severity { get; init; }

    public required string Summary { get; init; }

    public string? ExceptionType { get; init; }

    public required DiagnosticLocation Location { get; init; }

    public required IReadOnlyList<string> Hypotheses { get; init; }

    public required IReadOnlyList<string> SuggestedActions { get; init; }

    public required IReadOnlyList<DiagnosticEvidence> Evidence { get; init; }

    public DiagnosticStatus Status { get; init; } = DiagnosticStatus.New;

    public int OccurrenceCount { get; init; } = 1;

    public DateTimeOffset FirstSeenUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;

    public string AggregateKey => AggregateKeyOf(Category, Location, ExceptionType, Summary);

    public static string AggregateKeyOf(
        DiagnosticCategory category,
        DiagnosticLocation location,
        string? exceptionType,
        string summary) =>
        string.Join(
            '|',
            category,
            location.DimensionId ?? "?",
            location.ChunkX?.ToString(CultureInfo.InvariantCulture) ?? "-",
            location.ChunkZ?.ToString(CultureInfo.InvariantCulture) ?? "-",
            exceptionType ?? "-",
            summary);
}

public sealed record DiagnosticAnalysisResult(
    IReadOnlyList<DiagnosticEvent> Events,
    int LinesScanned,
    bool Truncated,
    string? Error);

/// <summary>
/// 从游戏日志/崩溃报告提取世界相关诊断。原始证据、定位、推断和风险等级分开保存；
/// 日志异常不会被自动判定为 Chunk 损坏，无法可靠定位时不生成虚构坐标。
/// </summary>
public sealed partial class WorldDiagnosticsParser
{
    private const int MaxBytesPerFile = 8 * 1024 * 1024;
    private const int MaxLineLength = 8 * 1024;
    private const int MaxLines = 250_000;
    private const int MaxEvidencePerEvent = 5;

    public async Task<DiagnosticAnalysisResult> AnalyzeAsync(
        string instanceId,
        string? sessionId,
        IEnumerable<string> files,
        CancellationToken cancellationToken = default)
    {
        var events = new Dictionary<string, DiagnosticEvent>(StringComparer.Ordinal);
        var linesScanned = 0;
        var truncated = false;
        try
        {
            foreach (var file in files.Where(File.Exists))
            {
                var read = await ReadFileAsync(file, instanceId, sessionId, events, cancellationToken).ConfigureAwait(false);
                linesScanned += read.Lines;
                truncated |= read.Truncated;
            }

            return new DiagnosticAnalysisResult(events.Values.OrderByDescending(item => item.LastSeenUtc).ToArray(), linesScanned, truncated, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiagnosticAnalysisResult(events.Values.ToArray(), linesScanned, truncated, $"日志读取失败：{exception.Message}");
        }
    }

    private static async Task<(int Lines, bool Truncated)> ReadFileAsync(
        string file,
        string instanceId,
        string? sessionId,
        Dictionary<string, DiagnosticEvent> events,
        CancellationToken cancellationToken)
    {
        var lines = 0;
        var bytes = 0L;
        var truncated = false;
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 64 * 1024, leaveOpen: false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lines++;
            bytes += line.Length + 1L;
            if (lines > MaxLines || bytes > MaxBytesPerFile)
            {
                truncated = true;
                break;
            }

            if (line.Length > MaxLineLength)
            {
                line = line[..MaxLineLength];
            }

            var parsed = ParseLine(line, file, lines, instanceId, sessionId);
            if (parsed is null)
            {
                continue;
            }

            var key = parsed.AggregateKey;
            if (events.TryGetValue(key, out var existing))
            {
                var evidence = existing.Evidence.Count >= MaxEvidencePerEvent
                    ? existing.Evidence
                    : existing.Evidence.Concat(parsed.Evidence).ToArray();
                events[key] = existing with
                {
                    OccurrenceCount = existing.OccurrenceCount + 1,
                    FirstSeenUtc = parsed.FirstSeenUtc < existing.FirstSeenUtc ? parsed.FirstSeenUtc : existing.FirstSeenUtc,
                    LastSeenUtc = parsed.LastSeenUtc > existing.LastSeenUtc ? parsed.LastSeenUtc : existing.LastSeenUtc,
                    Evidence = evidence,
                };
            }
            else
            {
                events[key] = parsed;
            }
        }

        return (lines, truncated);
    }

    internal static DiagnosticEvent? ParseLine(
        string line,
        string file,
        int lineNumber,
        string instanceId,
        string? sessionId)
    {
        var category = Classify(line);
        if (category is null)
        {
            return null;
        }

        var location = ExtractLocation(line, category.Value);
        if (location is null && category is null)
        {
            return null;
        }

        location ??= DiagnosticLocation.Unknown;
        var exceptionType = ExtractExceptionType(line);
        var severity = SeverityOf(category.Value, line);
        var summary = BuildSummary(category.Value, exceptionType, line);
        var key = DiagnosticEvent.AggregateKeyOf(category.Value, location, exceptionType, summary);
        return new DiagnosticEvent
        {
            Id = key.GetHashCode().ToString("x8", CultureInfo.InvariantCulture),
            InstanceId = instanceId,
            SessionId = sessionId,
            Category = category.Value,
            Severity = severity,
            Summary = summary,
            ExceptionType = exceptionType,
            Location = location,
            Hypotheses = HypothesesOf(category.Value, line),
            SuggestedActions = ActionsOf(category.Value, location, severity),
            Evidence = [new DiagnosticEvidence("game-log", file, lineNumber, line.Length > 300 ? line[..300] : line)],
        };
    }

    private static DiagnosticCategory? Classify(string line)
    {
        var hasChunk = line.Contains("chunk", StringComparison.OrdinalIgnoreCase);
        var hasRegion = line.Contains("region", StringComparison.OrdinalIgnoreCase);
        var hasEntity = line.Contains("entity", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("ticking", StringComparison.OrdinalIgnoreCase);
        var hasRender = line.Contains("render", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("renderer", StringComparison.OrdinalIgnoreCase);
        var hasNbt = line.Contains("nbt", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("tag", StringComparison.OrdinalIgnoreCase);
        if (hasChunk && line.Contains("load", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticCategory.ChunkLoad;
        }

        if (hasChunk && (line.Contains("decode", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("palette", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("index", StringComparison.OrdinalIgnoreCase)))
        {
            return DiagnosticCategory.ChunkDecode;
        }

        if (hasNbt && (line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                       line.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
        {
            return DiagnosticCategory.NbtDecode;
        }

        if (hasEntity && hasRender)
        {
            return DiagnosticCategory.EntityRender;
        }

        if (hasEntity && (line.Contains("tick", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("exception", StringComparison.OrdinalIgnoreCase)))
        {
            return DiagnosticCategory.EntityTick;
        }

        if (hasRegion && (line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("failed", StringComparison.OrdinalIgnoreCase)))
        {
            return DiagnosticCategory.RegionAccess;
        }

        return null;
    }

    private static DiagnosticLocation? ExtractLocation(string line, DiagnosticCategory category)
    {
        string? dimension = null;
        var dimensionInferred = false;
        var dimensionMatch = DimensionRegex().Match(line);
        if (dimensionMatch.Success)
        {
            dimension = dimensionMatch.Groups[1].Value;
        }
        else if (line.Contains("the_nether", StringComparison.OrdinalIgnoreCase))
        {
            dimension = "minecraft:the_nether";
        }
        else if (line.Contains("the_end", StringComparison.OrdinalIgnoreCase))
        {
            dimension = "minecraft:the_end";
        }
        else if (line.Contains("overworld", StringComparison.OrdinalIgnoreCase))
        {
            dimension = "minecraft:overworld";
        }

        var chunkMatch = ChunkCoordinateRegex().Match(line);
        if (chunkMatch.Success &&
            int.TryParse(chunkMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chunkX) &&
            int.TryParse(chunkMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chunkZ))
        {
            return new DiagnosticLocation(dimension, dimensionInferred, chunkX, chunkZ, null, null, null, null, null, DiagnosticConfidence.Exact);
        }

        var blockMatch = BlockCoordinateRegex().Match(line);
        if (blockMatch.Success &&
            int.TryParse(blockMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockX) &&
            int.TryParse(blockMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockY) &&
            int.TryParse(blockMatch.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockZ))
        {
            return new DiagnosticLocation(
                dimension,
                dimensionInferred,
                FloorChunk(blockX),
                FloorChunk(blockZ),
                blockX,
                blockY,
                blockZ,
                null,
                null,
                DiagnosticConfidence.Derived);
        }

        var regionMatch = RegionCoordinateRegex().Match(line);
        if (regionMatch.Success &&
            int.TryParse(regionMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var regionX) &&
            int.TryParse(regionMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var regionZ))
        {
            return new DiagnosticLocation(dimension, dimensionInferred, null, null, null, null, null, regionX, regionZ, DiagnosticConfidence.LocationOnly);
        }

        var playerMatch = PlayerPositionRegex().Match(line);
        if (playerMatch.Success &&
            double.TryParse(playerMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var playerX) &&
            double.TryParse(playerMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var playerZ))
        {
            var x = (int)Math.Floor(playerX);
            var z = (int)Math.Floor(playerZ);
            return new DiagnosticLocation(dimension, dimensionInferred, FloorChunk(x), FloorChunk(z), x, null, z, null, null, DiagnosticConfidence.LocationOnly);
        }

        return dimension is null
            ? null
            : new DiagnosticLocation(dimension, dimensionInferred, null, null, null, null, null, null, null, DiagnosticConfidence.Unknown);
    }

    private static int FloorChunk(int block) => block >> 4;

    private static string? ExtractExceptionType(string line)
    {
        var match = ExceptionTypeRegex().Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static DiagnosticSeverity SeverityOf(DiagnosticCategory category, string line)
    {
        if (line.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("outofbounds", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticSeverity.High;
        }

        return category switch
        {
            DiagnosticCategory.ChunkDecode or DiagnosticCategory.NbtDecode => DiagnosticSeverity.High,
            DiagnosticCategory.ChunkLoad or DiagnosticCategory.RegionAccess => DiagnosticSeverity.Medium,
            DiagnosticCategory.EntityTick => DiagnosticSeverity.Low,
            // 渲染异常不等于存档损坏，按 ADR 0030 保持低风险并提示检查模组。
            DiagnosticCategory.EntityRender => DiagnosticSeverity.Low,
            _ => DiagnosticSeverity.Info,
        };
    }

    private static string BuildSummary(DiagnosticCategory category, string? exceptionType, string line)
    {
        var normalized = NumbersRegex().Replace(line.Trim(), "#");
        if (normalized.Length > 160)
        {
            normalized = normalized[..160];
        }

        var prefix = category switch
        {
            DiagnosticCategory.ChunkLoad => "区块加载异常",
            DiagnosticCategory.ChunkDecode => "区块数据解析异常",
            DiagnosticCategory.NbtDecode => "NBT 数据解析异常",
            DiagnosticCategory.EntityTick => "实体 Tick 异常",
            DiagnosticCategory.EntityRender => "实体渲染异常",
            DiagnosticCategory.RegionAccess => "Region 访问异常",
            _ => "世界相关异常",
        };
        return exceptionType is null ? $"{prefix}：{normalized}" : $"{prefix}（{exceptionType}）：{normalized}";
    }

    private static IReadOnlyList<string> HypothesesOf(DiagnosticCategory category, string line) => category switch
    {
        DiagnosticCategory.EntityRender =>
        [
            "渲染器、光影或模组版本兼容性问题（日志不能证明实体 NBT 损坏）。",
            "资源包/模型/材质缺失或冲突。",
            "实体 NBT 可能异常，但需要只读检查才能确认。",
        ],
        DiagnosticCategory.EntityTick =>
        [
            "实体数据或模组 Tick 逻辑异常。",
            "附近区块/实体状态不一致。",
        ],
        DiagnosticCategory.ChunkLoad or DiagnosticCategory.ChunkDecode =>
        [
            "区块或 Region 文件可能损坏。",
            "模组在加载该区块时抛出异常。",
            "磁盘、权限或并发写入问题。",
        ],
        DiagnosticCategory.NbtDecode =>
        [
            "NBT 结构不符合当前版本或模组预期。",
            "文件写入中断或外部工具修改导致结构损坏。",
        ],
        DiagnosticCategory.RegionAccess =>
        [
            "Region 文件缺失、损坏或被其他进程占用。",
            "磁盘/权限问题。",
        ],
        _ => ["原因未知，需要结合原始日志和只读检查进一步确认。"],
    };

    private static IReadOnlyList<string> ActionsOf(
        DiagnosticCategory category,
        DiagnosticLocation location,
        DiagnosticSeverity severity)
    {
        var actions = new List<string> { "查看原始日志/崩溃报告与堆栈。" };
        if (location.HasChunk)
        {
            actions.Add($"在只读模式下检查 {location.DimensionId ?? "当前维度"} 的 Chunk ({location.ChunkX}, {location.ChunkZ})。");
        }

        if (category is DiagnosticCategory.ChunkLoad or DiagnosticCategory.ChunkDecode or DiagnosticCategory.NbtDecode)
        {
            actions.Add("检查相关模组、加载器与 Minecraft 版本兼容性。");
            actions.Add("如需修复，先创建可验证备份并确认世界未被游戏占用。");
        }
        else if (category is DiagnosticCategory.EntityRender or DiagnosticCategory.EntityTick)
        {
            actions.Add("优先更新/回退相关模组，或向模组维护者报告；不要直接删除整个区块。");
        }

        if (severity >= DiagnosticSeverity.High)
        {
            actions.Add("高风险：任何写入前必须预览目标、备份并重新解析验证。");
        }

        return actions;
    }

    [GeneratedRegex(@"(?i)\b(?:chunk(?:pos)?|chunk\s+at|chunk\s+position)\s*(?:at|:|=\s*)?\s*\[?\s*\(?\s*(-?\d+)\s*[,;/ ]\s*(-?\d+)")]
    private static partial Regex ChunkCoordinateRegex();

    [GeneratedRegex(@"(?i)\b(?:block(?:pos| position)?|pos)\s*(?:at|:|=)?\s*\[?\s*\(?\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)")]
    private static partial Regex BlockCoordinateRegex();

    [GeneratedRegex(@"(?i)\bregion(?:\s+file)?\s*(?:at|:|=)?\s*r\.(-?\d+)\.(-?\d+)")]
    private static partial Regex RegionCoordinateRegex();

    [GeneratedRegex(@"(?i)\bx\s*=\s*(-?\d+(?:\.\d+)?)\s*,\s*y\s*=\s*(-?\d+(?:\.\d+)?)\s*,\s*z\s*=\s*(-?\d+(?:\.\d+)?)")]
    private static partial Regex PlayerPositionRegex();

    [GeneratedRegex(@"(?i)\b(?:dimension|dim|level|world)\s*(?:id)?\s*[:=]\s*([a-z0-9_.-]+:[a-z0-9_./-]+)")]
    private static partial Regex DimensionRegex();

    [GeneratedRegex(@"(?:java\.lang\.|java\.io\.|net\.minecraft\.|com\.mojang\.)?([A-Z][A-Za-z0-9_$]*(?:Exception|Error))")]
    private static partial Regex ExceptionTypeRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumbersRegex();
}

/// <summary>版本化本地诊断报告存储；损坏/不兼容时返回空报告，不阻止 ECL 启动。</summary>
public sealed class DiagnosticReportStore(string directory)
{
    public const int CurrentSchemaVersion = 1;
    private const int MaxEvents = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Directory { get; } = Path.GetFullPath(directory);

    public string GetPath(string instanceId)
    {
        var safe = string.Concat(instanceId.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'));
        return Path.Combine(Directory, $"{safe}.json");
    }

    public async Task<IReadOnlyList<DiagnosticEvent>> LoadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(instanceId);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, FileOptions.Asynchronous);
            var document = await JsonSerializer.DeserializeAsync<DiagnosticDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return document is null || document.SchemaVersion > CurrentSchemaVersion ? [] : document.Events;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public async Task MergeAsync(string instanceId, IEnumerable<DiagnosticEvent> events, CancellationToken cancellationToken = default)
    {
        var existing = (await LoadAsync(instanceId, cancellationToken).ConfigureAwait(false)).ToList();
        var map = existing.ToDictionary(item => item.AggregateKey, StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (map.TryGetValue(item.AggregateKey, out var old))
            {
                map[item.AggregateKey] = item with
                {
                    Id = old.Id,
                    Status = old.Status,
                    OccurrenceCount = old.OccurrenceCount + item.OccurrenceCount,
                    FirstSeenUtc = old.FirstSeenUtc < item.FirstSeenUtc ? old.FirstSeenUtc : item.FirstSeenUtc,
                    LastSeenUtc = old.LastSeenUtc > item.LastSeenUtc ? old.LastSeenUtc : item.LastSeenUtc,
                    Evidence = old.Evidence.Concat(item.Evidence).Distinct().Take(8).ToArray(),
                };
            }
            else
            {
                map[item.AggregateKey] = item;
            }
        }

        var merged = map.Values
            .OrderByDescending(item => item.LastSeenUtc)
            .Take(MaxEvents)
            .ToArray();
        var path = GetPath(instanceId);
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        new DiagnosticDocument { SchemaVersion = CurrentSchemaVersion, Events = merged },
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // 忽略临时文件清理失败。
                }
            }
        }
    }

    public Task ClearAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var path = GetPath(instanceId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            },
            cancellationToken);

    private sealed record DiagnosticDocument
    {
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        public IReadOnlyList<DiagnosticEvent> Events { get; init; } = [];
    }
}
