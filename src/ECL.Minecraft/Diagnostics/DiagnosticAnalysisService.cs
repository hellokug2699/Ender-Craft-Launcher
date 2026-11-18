// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Diagnostics;
using Ecl.Minecraft.Instances;
using Ecl.Sessions;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Diagnostics;

/// <summary>本地诊断编排选项。默认只读会话证据，不访问网络。</summary>
public sealed record DiagnosticAnalysisOptions
{
    /// <summary>指定会话；为空时使用该实例最近的会话。会话不属于该实例时只记警告，不跨实例读取。</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// 是否接入世界诊断事件（F-10 S6）。开启后只读取本会话登记的日志/崩溃报告证据，
    /// 产出世界事实与定位目标；不读取世界存档、不枚举实例目录。
    /// </summary>
    public bool IncludeWorldEvents { get; init; }

    /// <summary>本地知识库路径；不存在或加载失败时报告明确写“未加载知识库”，findings 保持为空。</summary>
    public string? KnowledgeBasePath { get; init; }

    /// <summary>是否写入实例级诊断历史。写入失败只记日志，不影响返回的报告。</summary>
    public bool SaveHistory { get; init; } = true;
}

/// <summary>事实源输入：只包含已确认的实例、会话与已登记证据，不包含目录枚举器。</summary>
public sealed record DiagnosticFactContext(
    string InstanceId,
    GameInstance Instance,
    GameLogSession Session,
    IReadOnlyList<SessionEvidenceItem> Evidence);

/// <summary>
/// 结构化事实来源。实现必须是幂等的，并且不得抛异常；异常由编排服务降级为警告。
/// </summary>
public interface IDiagnosticFactSource
{
    string Id { get; }

    Task<IReadOnlyList<DiagnosticFact>> CollectAsync(
        DiagnosticFactContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// 本地诊断编排：会话证据 → 结构化解析 → 确定性规则引擎 → 实例级历史。
/// <para>
/// 输入只来自 <see cref="IGameSessionService"/> 登记的会话与 <see cref="ISessionLogReader"/> 登记的证据，
/// 绝不枚举实例目录或按文件名推断归属。读取/解析失败写入报告的 InputIssues/Warnings，报告仍然生成。
/// </para>
/// <para>
/// 知识库缺失时只做结构化解析，不输出猜测性根因；报告中的事实与规则结论分开保存。
/// 分析过程完全本地，不发起网络请求。
/// </para>
/// </summary>
public interface IDiagnosticAnalysisService
{
    Task<Result<DiagnosticReport>> AnalyzeAsync(
        string instanceId,
        DiagnosticAnalysisOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DiagnosticReport>>> LoadHistoryAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteHistoryAsync(
        string instanceId,
        string reportId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 会话 manifest 事实源：Java 版本、加载器、退出码、采集方式、采集丢弃字节和控制台采集空洞。
/// 这些是会话登记的直接事实，不是规则推断；采集空洞显式表达“没有采集到”，不是“采集完整”。
/// </summary>
public sealed class SessionFactSource : IDiagnosticFactSource
{
    public string Id => DiagnosticFactConfidence.SessionManifest;

    public Task<IReadOnlyList<DiagnosticFact>> CollectAsync(
        DiagnosticFactContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // 事实源约定不抛异常：即使 manifest 字段异常，也只返回已经能确认的事实。
        try
        {
            var facts = new List<DiagnosticFact>();
            var manifest = context.Session.Manifest;

            if (!string.IsNullOrWhiteSpace(manifest.JavaVersion))
            {
                facts.Add(DiagnosticFact.Create(
                    DiagnosticFactKinds.JavaVersion,
                    manifest.JavaVersion,
                    Id,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["javaPath"] = manifest.JavaPath ?? "",
                    }));
            }

            if (!string.IsNullOrWhiteSpace(manifest.Loader))
            {
                facts.Add(DiagnosticFact.Create(
                    DiagnosticFactKinds.Loader,
                    manifest.Loader,
                    Id));
            }

            if (manifest.ExitCode is { } exitCode)
            {
                facts.Add(DiagnosticFact.Create(
                    DiagnosticFactKinds.ExitCode,
                    exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Id,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["state"] = manifest.State.ToString(),
                    }));
            }

            if (!string.IsNullOrWhiteSpace(manifest.CaptureMode))
            {
                // 旧会话的 captureMode 是未知；仍作为事实保存，避免把“没有记录”误读成“没有采集”。
                facts.Add(DiagnosticFact.Create(
                    DiagnosticFactKinds.CaptureMode,
                    manifest.CaptureMode,
                    Id,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["writtenBytes"] = manifest.CaptureWrittenBytes.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        ["droppedBytes"] = manifest.CaptureDroppedBytes.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    }));
            }

            if (manifest.CaptureDroppedBytes > 0)
            {
                facts.Add(DiagnosticFact.Create(
                    DiagnosticFactKinds.CaptureDropped,
                    manifest.CaptureDroppedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Id,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["writtenBytes"] = manifest.CaptureWrittenBytes.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    }));
            }

            var consoleGap = string.Equals(
                                  manifest.CaptureMode,
                                  SessionCaptureModes.Console,
                                  StringComparison.Ordinal) ||
                              manifest.Warnings.Contains(SessionWarnings.CaptureConsole, StringComparer.Ordinal);
            if (consoleGap)
            {
                facts.Add(DiagnosticFact.Create(
                    DiagnosticFactKinds.ConsoleCaptureGap,
                    SessionCaptureModes.Console,
                    Id,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["warning"] = SessionWarnings.CaptureConsole,
                    }));
            }

            return Task.FromResult<IReadOnlyList<DiagnosticFact>>(facts);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Task.FromResult<IReadOnlyList<DiagnosticFact>>([]);
        }
    }
}

/// <summary>本地诊断编排服务。所有依赖通过构造器注入，便于测试只读会话证据与历史降级路径。</summary>
public sealed class DiagnosticAnalysisService : IDiagnosticAnalysisService
{
    private readonly IInstanceService _instances;
    private readonly IGameSessionService _sessions;
    private readonly ISessionLogReader _logReader;
    private readonly DiagnosticEngine _engine;
    private readonly IReadOnlyList<IDiagnosticFactSource> _factSources;
    private readonly IDiagnosticWorldFactSource _worldFactSource;
    private readonly DiagnosticHistoryStore _history;
    private readonly ILogger<DiagnosticAnalysisService> _logger;

    public DiagnosticAnalysisService(
        IInstanceService instances,
        IGameSessionService sessions,
        ISessionLogReader logReader,
        DiagnosticEngine engine,
        IEnumerable<IDiagnosticFactSource> factSources,
        IDiagnosticWorldFactSource worldFactSource,
        DiagnosticHistoryStore history,
        ILogger<DiagnosticAnalysisService> logger)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(logReader);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(factSources);
        ArgumentNullException.ThrowIfNull(worldFactSource);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(logger);

        _instances = instances;
        _sessions = sessions;
        _logReader = logReader;
        _engine = engine;
        _factSources = factSources.ToArray();
        _worldFactSource = worldFactSource;
        _history = history;
        _logger = logger;
    }

    public async Task<Result<DiagnosticReport>> AnalyzeAsync(
        string instanceId,
        DiagnosticAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DiagnosticAnalysisOptions();

        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<DiagnosticReport>.Fail(
                "instance.not_found",
                "实例不存在。",
                instanceId);
        }

        var warnings = new List<string>();
        var issues = new List<DiagnosticInputIssue>();
        var events = new List<ParsedLogEvent>();
        var crashReports = new List<CrashReport>();
        var fatalErrors = new List<JvmFatalErrorReport>();
        var evidence = new List<SessionEvidenceItem>();

        var session = ResolveSession(instanceId, options.SessionId, warnings, issues);
        if (session is null)
        {
            warnings.Add("未找到该实例的游戏会话，报告只包含环境信息。");
            issues.Add(new DiagnosticInputIssue(
                string.Empty,
                "diagnostics.no_session",
                "没有可用于分析的会话。"));
        }
        else
        {
            LoadSessionEvidence(session, evidence, warnings, issues);
            AddSharedLogWarnings(session, evidence, warnings);

            foreach (var item in evidence)
            {
                await ReadEvidenceAsync(
                        session,
                        item,
                        events,
                        crashReports,
                        fatalErrors,
                        warnings,
                        issues,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (evidence.Count == 0)
            {
                warnings.Add("该会话没有登记可读取的证据，诊断只包含会话事实与环境信息。");
                issues.Add(new DiagnosticInputIssue(
                    string.Empty,
                    "diagnostics.no_evidence",
                    "该会话没有登记可读取的证据。"));
            }
        }

        var facts = (await CollectFactsAsync(instance, session, evidence, warnings, cancellationToken)
                .ConfigureAwait(false))
            .ToList();
        IReadOnlyList<DiagnosticWorldTarget> worldTargets = [];
        if (options.IncludeWorldEvents)
        {
            if (session is null)
            {
                warnings.Add("没有可用于世界诊断的会话证据：本次分析未读取世界文件，也不推断世界问题。");
            }
            else
            {
                worldTargets = await CollectWorldEvidenceAsync(
                        instance,
                        session,
                        evidence,
                        facts,
                        warnings,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var knowledgeBase = LoadKnowledgeBase(options.KnowledgeBasePath, warnings);
        var environment = BuildEnvironment(instance, session);

        var report = _engine.Analyze(new DiagnosticAnalysisRequest
        {
            Events = events,
            CrashReports = crashReports,
            JvmFatalErrors = fatalErrors,
            KnowledgeBase = knowledgeBase,
            Environment = environment,
            InputIssues = issues,
            Warnings = warnings,
            InstanceId = instanceId,
            SessionId = session?.SessionId,
            Facts = facts,
            WorldTargets = worldTargets,
        });

        if (options.SaveHistory)
        {
            var saved = _history.Save(report);
            if (!saved.IsSuccess)
            {
                // 历史写入失败不应让用户丢失本次结论；只记录日志并保留返回的报告。
                _logger.LogWarning(
                    "保存诊断历史失败：{Code} {Message}",
                    saved.Error!.Code,
                    saved.Error.Message);
            }
        }

        return Result<DiagnosticReport>.Ok(report);
    }

    public async Task<Result<IReadOnlyList<DiagnosticReport>>> LoadHistoryAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<IReadOnlyList<DiagnosticReport>>.Fail(
                "instance.not_found",
                "实例不存在。",
                instanceId);
        }

        var loaded = await Task.Run(() => _history.LoadAll(instanceId), cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<DiagnosticReport>>.Fail(loaded.Error!);
        }

        return Result<IReadOnlyList<DiagnosticReport>>.Ok(loaded.Value!.Reports);
    }

    public async Task<Result> DeleteHistoryAsync(
        string instanceId,
        string reportId,
        CancellationToken cancellationToken = default)
    {
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result.Fail("instance.not_found", "实例不存在。", instanceId);
        }

        return await Task.Run(() => _history.Delete(instanceId, reportId), cancellationToken)
            .ConfigureAwait(false);
    }

    private GameLogSession? ResolveSession(
        string instanceId,
        string? sessionId,
        List<string> warnings,
        List<DiagnosticInputIssue> issues)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var requested = _sessions.Get(sessionId);
                if (requested is null)
                {
                    warnings.Add($"指定的会话 {sessionId} 不存在，已按无会话处理。");
                    issues.Add(new DiagnosticInputIssue(
                        sessionId,
                        "diagnostics.session_not_found",
                        "指定的会话不存在。"));
                    return null;
                }

                if (!string.Equals(requested.Manifest.InstanceId, instanceId, StringComparison.Ordinal))
                {
                    warnings.Add($"指定的会话 {sessionId} 不属于实例 {instanceId}，已拒绝跨实例读取。");
                    issues.Add(new DiagnosticInputIssue(
                        sessionId,
                        "diagnostics.session_instance_mismatch",
                        "指定的会话不属于该实例。"));
                    return null;
                }

                return requested;
            }

            // 只从会话服务返回的 manifest 中筛选，不枚举实例目录。
            return _sessions.List()
                .Where(candidate => string.Equals(
                    candidate.Manifest.InstanceId,
                    instanceId,
                    StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.Manifest.UpdatedAtUtc)
                .ThenByDescending(candidate => candidate.Manifest.CreatedAtUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"读取游戏会话失败：{exception.Message}");
            issues.Add(new DiagnosticInputIssue(
                string.Empty,
                "diagnostics.sessions_unavailable",
                "无法读取游戏会话目录。"));
            return null;
        }
    }

    private void LoadSessionEvidence(
        GameLogSession session,
        List<SessionEvidenceItem> evidence,
        List<string> warnings,
        List<DiagnosticInputIssue> issues)
    {
        try
        {
            evidence.AddRange(_logReader.DescribeEvidence(session));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"读取会话证据登记失败：{exception.Message}");
            issues.Add(new DiagnosticInputIssue(
                session.SessionId,
                "diagnostics.evidence_unavailable",
                "无法读取会话证据登记。"));
        }
    }

    private async Task ReadEvidenceAsync(
        GameLogSession session,
        SessionEvidenceItem item,
        List<ParsedLogEvent> events,
        List<CrashReport> crashReports,
        List<JvmFatalErrorReport> fatalErrors,
        List<string> warnings,
        List<DiagnosticInputIssue> issues,
        CancellationToken cancellationToken)
    {
        var artifact = item.Artifact;
        var displayName = DescribeArtifact(item);

        if (item.Availability is SessionSourceAvailability.Missing
            or SessionSourceAvailability.NotGenerated
            or SessionSourceAvailability.Unreadable)
        {
            var code = item.Availability switch
            {
                SessionSourceAvailability.Missing => "diagnostics.evidence_missing",
                SessionSourceAvailability.NotGenerated => "diagnostics.evidence_not_generated",
                _ => "diagnostics.evidence_unreadable",
            };
            var message = item.Detail ?? $"证据 {displayName} 不可读取。";
            warnings.Add($"{displayName}：{message}");
            issues.Add(new DiagnosticInputIssue(displayName, code, message));
            return;
        }

        SessionLogChunk chunk;
        try
        {
            chunk = await _logReader
                .ReadEvidenceAsync(session, artifact, SessionLogReader.MaxChunkBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            warnings.Add($"{displayName}：读取证据失败：{exception.Message}");
            issues.Add(new DiagnosticInputIssue(
                displayName,
                "diagnostics.evidence_read_failed",
                exception.Message));
            return;
        }

        if (!string.IsNullOrWhiteSpace(chunk.Error))
        {
            warnings.Add($"{displayName}：读取证据失败：{chunk.Error}");
            issues.Add(new DiagnosticInputIssue(
                displayName,
                "diagnostics.evidence_read_failed",
                chunk.Error));
            if (chunk.Lines.Count == 0)
            {
                return;
            }
        }

        if (chunk.Lines.Count == 0)
        {
            warnings.Add($"{displayName}：证据为空，未参与诊断。");
            issues.Add(new DiagnosticInputIssue(
                displayName,
                "diagnostics.evidence_empty",
                "证据为空。"));
            return;
        }

        if (chunk.HasMoreBefore)
        {
            warnings.Add($"{displayName}：证据超过单次读取上限，报告中的行号相对于已读取片段。");
        }

        var text = string.Join('\n', chunk.Lines);
        ParseEvidenceText(
            displayName,
            text,
            artifact.Kind,
            events,
            crashReports,
            fatalErrors,
            warnings);
    }

    private static void ParseEvidenceText(
        string fileName,
        string text,
        string? artifactKind,
        List<ParsedLogEvent> events,
        List<CrashReport> crashReports,
        List<JvmFatalErrorReport> fatalErrors,
        List<string> warnings)
    {
        var classification = artifactKind switch
        {
            SessionArtifactKinds.GameLog or SessionArtifactKinds.RotatedLog =>
                DiagnosticArtifactClassification.GameLog,
            SessionArtifactKinds.ClientCrashReport =>
                DiagnosticArtifactClassification.ClientCrashReport,
            SessionArtifactKinds.ServerCrashReport =>
                DiagnosticArtifactClassification.ServerCrashReport,
            SessionArtifactKinds.JvmFatalError =>
                DiagnosticArtifactClassification.JvmFatalError,
            _ => DiagnosticArtifactClassifier.Classify(
                fileName,
                text.AsSpan(0, Math.Min(text.Length, DiagnosticArtifactClassifier.HeadCharLimit))),
        };

        switch (classification)
        {
            case DiagnosticArtifactClassification.GameLog:
                {
                    var parsed = LatestLogParser.Parse(fileName, text);
                    events.AddRange(parsed.Events);
                    foreach (var warning in parsed.Warnings)
                    {
                        warnings.Add($"{fileName}：{warning}");
                    }

                    return;
                }

            case DiagnosticArtifactClassification.ClientCrashReport:
            case DiagnosticArtifactClassification.ServerCrashReport:
                {
                    var parsed = CrashReportParser.Parse(fileName, text);
                    crashReports.Add(parsed);
                    foreach (var warning in parsed.Warnings)
                    {
                        warnings.Add($"{fileName}：{warning}");
                    }

                    return;
                }

            case DiagnosticArtifactClassification.JvmFatalError:
                {
                    var parsed = JvmFatalErrorParser.Parse(fileName, text);
                    fatalErrors.Add(parsed);
                    foreach (var warning in parsed.Warnings)
                    {
                        warnings.Add($"{fileName}：{warning}");
                    }

                    return;
                }

            case DiagnosticArtifactClassification.Ambiguous:
                warnings.Add($"{fileName}：文件名与内容签名冲突，无法确认证据类型，未参与诊断。");
                return;

            default:
                warnings.Add($"{fileName}：无法识别证据类型，未参与诊断。");
                return;
        }
    }

    private async Task<IReadOnlyList<DiagnosticFact>> CollectFactsAsync(
        GameInstance instance,
        GameLogSession? session,
        IReadOnlyList<SessionEvidenceItem> evidence,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return [];
        }

        var context = new DiagnosticFactContext(instance.Id, instance, session, evidence);
        var facts = new List<DiagnosticFact>();
        foreach (var source in _factSources)
        {
            try
            {
                var collected = await source.CollectAsync(context, cancellationToken).ConfigureAwait(false);
                foreach (var fact in collected)
                {
                    if (fact is not null && !string.IsNullOrWhiteSpace(fact.Kind))
                    {
                        facts.Add(fact with
                        {
                            Confidence = DiagnosticFactConfidence.Cap(fact.SourceId, fact.Confidence),
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // 事实源异常只降级为警告：报告仍生成，不把“没收集到”伪装成“事实不存在”。
                warnings.Add($"事实源 {source.Id} 执行失败，已降级：{exception.Message}");
            }
        }

        return facts;
    }

    private async Task<IReadOnlyList<DiagnosticWorldTarget>> CollectWorldEvidenceAsync(
        GameInstance instance,
        GameLogSession session,
        IReadOnlyList<SessionEvidenceItem> evidence,
        List<DiagnosticFact> facts,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var collected = await _worldFactSource
                .CollectAsync(
                    new DiagnosticFactContext(instance.Id, instance, session, evidence),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var warning in collected.Warnings)
            {
                warnings.Add(warning);
            }

            foreach (var fact in collected.Facts)
            {
                if (fact is not null && !string.IsNullOrWhiteSpace(fact.Kind))
                {
                    facts.Add(fact with
                    {
                        Confidence = DiagnosticFactConfidence.Cap(fact.SourceId, fact.Confidence),
                    });
                }
            }

            return collected.Targets;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 世界事实源失败只降级为警告：不把“没收集到”伪装成“没有世界问题”。
            warnings.Add($"世界诊断事实源执行失败，已降级：{exception.Message}");
            return [];
        }
    }

    private static KnowledgeBase? LoadKnowledgeBase(string? path, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            warnings.Add("未加载知识库：未指定知识库路径，只做结构化解析，不产生结论。");
            return null;
        }

        if (!File.Exists(path))
        {
            warnings.Add(
                $"未加载知识库：文件不存在（{BoundedTextReader.SafeFileName(path)}），只做结构化解析，不产生结论。");
            return null;
        }

        var loaded = KnowledgeBaseLoader.LoadFile(path);
        if (!loaded.IsSuccess)
        {
            warnings.Add(
                $"未加载知识库：{loaded.Error!.Code}，只做结构化解析，不产生结论。");
            return null;
        }

        return loaded.Value;
    }

    private static DiagnosticEnvironment BuildEnvironment(GameInstance instance, GameLogSession? session)
    {
        var manifest = session?.Manifest;
        return new DiagnosticEnvironment
        {
            MinecraftVersion = FirstNonEmpty(manifest?.MinecraftVersion, instance.GameVersion),
            Loader = FirstNonEmpty(manifest?.Loader, instance.Loader),
            LoaderVersion = instance.LoaderVersion,
            JavaVersion = manifest?.JavaVersion,
        };
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;

    private static string DescribeArtifact(SessionEvidenceItem item)
    {
        var path = item.Path ?? item.Artifact.CapturedPath ?? item.Artifact.SourcePath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            return BoundedTextReader.SafeFileName(path);
        }

        return string.IsNullOrWhiteSpace(item.Artifact.Kind) ? item.Artifact.Id : item.Artifact.Kind;
    }

    /// <summary>
    /// 共享 latest.log 的归属不确定必须保留在报告警告中：它可能由多个进程写入，
    /// 不能当作本会话的完整证据。
    /// </summary>
    private static void AddSharedLogWarnings(
        GameLogSession session,
        IReadOnlyList<SessionEvidenceItem> evidence,
        List<string> warnings)
    {
        const string message = "共享日志归属不确定：game_log 可能由多个进程写入，不能视为本会话的完整证据。";

        var shared = evidence.Any(item =>
                         string.Equals(item.Artifact.Kind, SessionArtifactKinds.GameLog, StringComparison.Ordinal) &&
                         item.Artifact.Ownership == ArtifactOwnership.SharedOrAmbiguous) ||
                     session.Manifest.Artifacts.Any(artifact =>
                         string.Equals(artifact.Kind, SessionArtifactKinds.GameLog, StringComparison.Ordinal) &&
                         artifact.Ownership == ArtifactOwnership.SharedOrAmbiguous) ||
                     string.Equals(session.Manifest.LogIsolation, "shared", StringComparison.OrdinalIgnoreCase);

        if (shared && !warnings.Contains(message, StringComparer.Ordinal))
        {
            warnings.Add(message);
        }
    }
}
