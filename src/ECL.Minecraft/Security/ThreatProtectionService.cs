// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Minecraft.Engine.Files;
using Ecl.Minecraft.Engine.Transactions;
using Ecl.Minecraft.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Security;

/// <summary>
/// ECL Threat Protection（ADR 0043）。只扫描 ECL 管理的 Minecraft 内容；手动扫描优先，
/// 启动门控只消费最近扫描结果与启动参数，不做全实例实时扫描。
/// </summary>
public sealed class ThreatProtectionService : IThreatProtectionService
{
    private const string ScannerVersion = "1.0.0";
    private const string RulesVersion = "2026-11-03";
    private const int MaxArchiveEntries = 20_000;
    private const long MaxScannedEntryBytes = 2L * 1024 * 1024;

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".sh", ".jar.exe",
    };

    private static readonly string[] SuspiciousTokens =
    [
        "Runtime.getRuntime",
        "ProcessBuilder",
        "System.loadLibrary",
        "defineClass",
        "URLClassLoader",
        "powershell",
        "cmd.exe",
        "wscript.shell",
        "CreateRemoteThread",
        "VirtualAllocEx",
    ];

    private readonly IInstanceService _instances;
    private readonly InstanceTransactionCoordinator _transactions;
    private readonly IAppPaths _paths;
    private readonly ILogger<ThreatProtectionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ThreatProtectionService(
        IInstanceService instances,
        InstanceTransactionCoordinator transactions,
        IAppPaths paths,
        ILogger<ThreatProtectionService>? logger = null)
    {
        _instances = instances;
        _transactions = transactions;
        _paths = paths;
        _logger = logger ?? NullLogger<ThreatProtectionService>.Instance;
    }

    private string SecurityRoot => Path.Combine(_paths.ConfigDirectory, "security");

    private string AcceptancesPath => Path.Combine(SecurityRoot, "acceptances.json");

    private string QuarantineRoot => Path.Combine(_paths.Root, "quarantine");

    public async Task<Result<ThreatScanReport>> ScanAsync(
        string instanceId,
        SecurityScanOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ThreatScanReport>.Fail("instance.not_found", "实例不存在。");
        }

        var workspace = new InstanceFileWorkspace(InstancePaths.GetGameDirectory(_paths, instance));
        if (!Directory.Exists(workspace.RootPath))
        {
            return Result<ThreatScanReport>.Fail("security.root_missing", "实例游戏目录不存在。");
        }

        options ??= new SecurityScanOptions();
        var knownThreats = LoadKnownThreats();
        var findings = new List<ThreatFinding>();
        var warnings = new List<string>();
        var filesScanned = 0;
        var archivesScanned = 0;
        var complete = true;

        foreach (var file in EnumerateScanCandidates(workspace, warnings, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filesScanned >= options.MaxFiles)
            {
                complete = false;
                warnings.Add($"扫描达到 {options.MaxFiles} 个文件上限，结果不完整。");
                break;
            }

            filesScanned++;
            progress?.Report($"扫描 {file.RelativePath}");
            try
            {
                var hash = SnapshotHash(file.FullPath);
                var evidence = new List<ThreatEvidence>();
                if (knownThreats.TryGetValue(hash, out var known))
                {
                    evidence.Add(new ThreatEvidence(
                        "threat.known_hash",
                        RulesVersion,
                        known.Level,
                        $"命中本地已知威胁记录：{known.Name}。"));
                }

                var extension = Path.GetExtension(file.RelativePath);
                if (ScriptExtensions.Contains(extension))
                {
                    evidence.Add(new ThreatEvidence(
                        "file.executable",
                        RulesVersion,
                        ThreatLevel.Suspicious,
                        "实例内出现可执行/脚本文件，需确认来源。",
                        file.RelativePath));
                }

                if (IsArchive(file.RelativePath))
                {
                    archivesScanned++;
                    evidence.AddRange(options.IncludeDeepArchiveAnalysis
                        ? AnalyzeArchive(file, cancellationToken)
                        : [new ThreatEvidence(
                            "archive.not_deep_scanned",
                            RulesVersion,
                            ThreatLevel.Notice,
                            "未执行深度归档分析。")]);
                }
                else if (file.RelativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    evidence.Add(new ThreatEvidence(
                        "archive.signature",
                        RulesVersion,
                        ThreatLevel.Suspicious,
                        "扩展名为 .jar，但文件不是有效 ZIP/JAR。",
                        file.RelativePath));
                }

                if (evidence.Count == 0)
                {
                    continue;
                }

                var level = evidence.Max(item => item.Level);
                findings.Add(new ThreatFinding(
                    $"{Path.GetFileNameWithoutExtension(file.RelativePath)}-{findings.Count + 1}",
                    file.RelativePath,
                    hash,
                    level,
                    evidence,
                    level >= ThreatLevel.HighRisk
                        ? "建议隔离该文件并确认来源；如确认可信，可在启动时明确接受风险。"
                        : "请核对文件来源，必要时隔离或更新。"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or InvalidDataException)
            {
                complete = false;
                warnings.Add($"{file.RelativePath} 无法扫描：{exception.Message}");
            }
        }

        var report = new ThreatScanReport(
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(4).ToLowerInvariant()}",
            instanceId,
            DateTimeOffset.UtcNow,
            ScannerVersion,
            RulesVersion,
            findings,
            filesScanned,
            archivesScanned,
            complete,
            warnings);
        await SaveLatestScanAsync(report, cancellationToken).ConfigureAwait(false);
        return Result<ThreatScanReport>.Ok(report);
    }

    public Task<Result<ThreatScanReport?>> GetLatestScanAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        GetLatestScanCoreAsync(instanceId, cancellationToken);

    private async Task<Result<ThreatScanReport?>> GetLatestScanCoreAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        if (!AppPaths.IsValidInstanceId(instanceId))
        {
            return Result<ThreatScanReport?>.Fail("security.invalid_instance", "实例 ID 不合法。");
        }

        var path = Path.Combine(SecurityRoot, "scans", $"{instanceId}.json");
        if (!File.Exists(path))
        {
            return Result<ThreatScanReport?>.Ok(null);
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return Result<ThreatScanReport?>.Ok(
                JsonSerializer.Deserialize<ThreatScanReport>(json, EclJson.Default));
        }
        catch (JsonException exception)
        {
            return Result<ThreatScanReport?>.Fail(
                "security.scan_corrupt",
                "最近一次安全扫描记录损坏，不能当作未扫描或 Clean。",
                exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<ThreatScanReport?>.Fail(
                "security.scan_unreadable",
                "最近一次安全扫描记录无法读取。",
                exception.Message);
        }
    }

    public async Task<Result<LaunchSecurityDecision>> EvaluateLaunchAsync(
        string instanceId,
        IReadOnlyList<string>? launchArguments = null,
        CancellationToken cancellationToken = default)
    {
        var instance = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<LaunchSecurityDecision>.Fail("instance.not_found", "实例不存在。");
        }

        var latest = await GetLatestScanAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!latest.IsSuccess)
        {
            return Result<LaunchSecurityDecision>.Fail(latest.Error!);
        }

        var findings = latest.Value is null
            ? new List<ThreatFinding>()
            : latest.Value.Findings.ToList();

        var argumentFinding = EvaluateArguments(instanceId, launchArguments);
        if (argumentFinding is not null)
        {
            findings.Add(argumentFinding);
        }

        if (latest.Value is null && findings.Count == 0)
        {
            return Result<LaunchSecurityDecision>.Ok(new LaunchSecurityDecision(
                true,
                false,
                findings,
                "尚未执行安全扫描；本次启动未被安全中心阻断，但覆盖范围未知。",
                false));
        }

        var blocking = findings
            .Where(finding => finding.Level >= ThreatLevel.Suspicious)
            .ToArray();
        if (blocking.Length == 0)
        {
            var notices = findings.Count(finding => finding.Level == ThreatLevel.Notice);
            return Result<LaunchSecurityDecision>.Ok(new LaunchSecurityDecision(
                true,
                false,
                findings,
                notices == 0
                    ? "最近一次本地扫描未发现需要处理的风险。"
                    : $"最近一次扫描有 {notices} 条提示，不阻断启动。",
                false));
        }

        var accepted = await LoadAcceptancesAsync(cancellationToken).ConfigureAwait(false);
        var unaccepted = blocking
            .Where(finding => !IsAccepted(accepted, finding))
            .ToArray();
        if (unaccepted.Length == 0)
        {
            return Result<LaunchSecurityDecision>.Ok(new LaunchSecurityDecision(
                true,
                false,
                blocking,
                "此前接受的风险未发生变化，本次启动不重复提示。",
                true));
        }

        return Result<LaunchSecurityDecision>.Ok(new LaunchSecurityDecision(
            false,
            true,
            unaccepted,
            $"检测到 {unaccepted.Length} 项需要确认的风险；本次启动已暂停。",
            false));
    }

    public async Task<Result> AcceptRiskAsync(
        string instanceId,
        IReadOnlyList<ThreatFinding> findings,
        CancellationToken cancellationToken = default)
    {
        if (findings.Count == 0)
        {
            return Result.Ok();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var acceptances = await LoadAcceptancesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var finding in findings)
            {
                var rule = finding.Evidence.FirstOrDefault()?.RuleId ?? "unknown";
                acceptances.RemoveAll(item =>
                    string.Equals(item.InstanceId, instanceId, StringComparison.Ordinal) &&
                    string.Equals(item.RelativePath, finding.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.RuleId, rule, StringComparison.Ordinal) &&
                    string.Equals(item.Sha256, finding.Sha256, StringComparison.OrdinalIgnoreCase));
                acceptances.Add(new RiskAcceptanceRecord(
                    instanceId,
                    finding.RelativePath,
                    finding.Sha256,
                    rule,
                    finding.Level,
                    DateTimeOffset.UtcNow,
                    ScannerVersion,
                    RulesVersion));
            }

            await SaveAcceptancesAsync(acceptances, cancellationToken).ConfigureAwait(false);
            return Result.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<QuarantineRecord>> QuarantineAsync(
        string instanceId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var open = await _instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (open is null)
        {
            return Result<QuarantineRecord>.Fail("instance.not_found", "实例不存在。");
        }

        var workspace = new InstanceFileWorkspace(InstancePaths.GetGameDirectory(_paths, open));
        string fullPath;
        string normalized;
        try
        {
            normalized = workspace.NormalizeRelative(relativePath);
            fullPath = workspace.Resolve(relativePath);
        }
        catch (InstancePathException exception)
        {
            return Result<QuarantineRecord>.Fail("security.invalid_path", "隔离目标超出实例边界。", exception.Message);
        }

        if (!File.Exists(fullPath))
        {
            return Result<QuarantineRecord>.Fail("security.not_found", "目标文件不存在。");
        }

        var hash = SnapshotHash(fullPath);
        var size = new FileInfo(fullPath).Length;
        var quarantineId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{RandomNumberGenerator.GetHexString(6).ToLowerInvariant()}";
        var directory = Path.Combine(QuarantineRoot, instanceId, quarantineId);
        var payload = Path.Combine(directory, "payload.bin");
        var record = new QuarantineRecord(
            quarantineId,
            instanceId,
            normalized,
            hash,
            size,
            DateTimeOffset.UtcNow,
            ThreatLevel.Suspicious);
        try
        {
            Directory.CreateDirectory(directory);
            File.Copy(fullPath, payload, overwrite: false);
            if (SnapshotHash(payload) != hash)
            {
                throw new InvalidDataException("隔离副本哈希不一致。");
            }

            await File.WriteAllTextAsync(
                Path.Combine(directory, "manifest.json"),
                JsonSerializer.Serialize(record, EclJson.Indented),
                cancellationToken).ConfigureAwait(false);

            var transaction = await _transactions.ExecuteAsync(
                instanceId,
                new InstanceChangePlan(
                    [InstanceChangeOperation.Delete(normalized)],
                    $"隔离 {normalized}"),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!transaction.IsSuccess)
            {
                TryDeleteDirectory(directory);
                return Result<QuarantineRecord>.Fail(transaction.Error!);
            }

            return Result<QuarantineRecord>.Ok(record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException)
        {
            TryDeleteDirectory(directory);
            return Result<QuarantineRecord>.Fail("security.quarantine_failed", "隔离文件失败。", exception.Message);
        }
    }

    public async Task<Result> RestoreQuarantineAsync(
        string instanceId,
        string quarantineId,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeQuarantineId(quarantineId))
        {
            return Result.Fail("security.invalid_quarantine", "隔离记录 ID 不合法。");
        }

        var directory = Path.Combine(QuarantineRoot, instanceId, quarantineId);
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return Result.Fail("security.quarantine_missing", "隔离记录不存在。");
        }

        QuarantineRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<QuarantineRecord>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                EclJson.Default);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Result.Fail("security.quarantine_corrupt", "隔离记录损坏。", exception.Message);
        }

        if (record is null || !string.Equals(record.InstanceId, instanceId, StringComparison.Ordinal))
        {
            return Result.Fail("security.quarantine_conflict", "隔离记录与实例不匹配。");
        }

        var payload = Path.Combine(directory, "payload.bin");
        if (!File.Exists(payload) || SnapshotHash(payload) != record.Sha256)
        {
            return Result.Fail("security.quarantine_corrupt", "隔离副本缺失或哈希不一致。");
        }

        var transaction = await _transactions.ExecuteAsync(
            instanceId,
            new InstanceChangePlan(
                [InstanceChangeOperation.CopyFrom(record.OriginalRelativePath, payload, record.Sha256)],
                $"恢复隔离文件 {record.OriginalRelativePath}"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!transaction.IsSuccess)
        {
            return Result.Fail(transaction.Error!);
        }

        TryDeleteDirectory(directory);
        return Result.Ok();
    }

    public async Task<Result<IReadOnlyList<QuarantineRecord>>> ListQuarantineAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(QuarantineRoot, instanceId);
        if (!Directory.Exists(directory))
        {
            return Result<IReadOnlyList<QuarantineRecord>>.Ok([]);
        }

        var records = new List<QuarantineRecord>();
        foreach (var manifest in Directory.EnumerateFiles(directory, "manifest.json", SearchOption.AllDirectories))
        {
            try
            {
                var record = JsonSerializer.Deserialize<QuarantineRecord>(
                    await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false),
                    EclJson.Default);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // 单个损坏记录不影响其它隔离项。
            }
        }

        return Result<IReadOnlyList<QuarantineRecord>>.Ok(
            records.OrderByDescending(item => item.QuarantinedAt).ToArray());
    }

    private static ThreatFinding? EvaluateArguments(string instanceId, IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        var evidence = new List<ThreatEvidence>();
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase) &&
                !argument.Contains("authlib-injector", StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(new ThreatEvidence(
                    "launch.unknown_javaagent",
                    RulesVersion,
                    ThreatLevel.Suspicious,
                    "启动参数包含未经 ECL 管理的 Java Agent。",
                    argument));
            }
            else if (argument.StartsWith("-agentlib:", StringComparison.OrdinalIgnoreCase) ||
                     argument.StartsWith("-Xbootclasspath", StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add(new ThreatEvidence(
                    "launch.native_agent",
                    RulesVersion,
                    ThreatLevel.Suspicious,
                    "启动参数包含 native agent 或 bootclasspath 注入。",
                    argument));
            }
        }

        return evidence.Count == 0
            ? null
            : new ThreatFinding(
                $"launch-{instanceId}",
                "launch-arguments",
                null,
                evidence.Max(item => item.Level),
                evidence,
                "请确认启动参数来源；ECL 不会自动修改或删除这些参数。");
    }

    private IEnumerable<ScanCandidate> EnumerateScanCandidates(
        InstanceFileWorkspace workspace,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var roots = new[] { "mods", "resourcepacks", "shaderpacks", "config", "scripts" };
        foreach (var root in roots)
        {
            var path = Path.Combine(workspace.RootPath, root);
            if (!Directory.Exists(path))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                IEnumerable<string> entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法枚举 {directory}：{exception.Message}");
                    continue;
                }

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    var relative = Path.GetRelativePath(workspace.RootPath, entry).Replace('\\', '/');
                    try
                    {
                        workspace.NormalizeRelative(relative);
                    }
                    catch (InstancePathException)
                    {
                        warnings.Add($"跳过无法安全表示的路径：{relative}");
                        continue;
                    }

                    yield return new ScanCandidate(relative, entry);
                }
            }
        }
    }

    private static IEnumerable<ThreatEvidence> AnalyzeArchive(
        ScanCandidate file,
        CancellationToken cancellationToken)
    {
        var evidence = new List<ThreatEvidence>();
        try
        {
            using var archive = ZipFile.OpenRead(file.FullPath);
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                evidence.Add(new ThreatEvidence(
                    "archive.entry_limit",
                    RulesVersion,
                    ThreatLevel.Notice,
                    $"归档条目数量 {archive.Entries.Count} 超过建议上限。",
                    file.RelativePath));
            }

            long totalUncompressed = 0;
            long totalCompressed = 0;
            var nestedArchives = 0;
            var scriptEntries = new List<string>();
            var scannedBytes = 0L;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') ||
                    name.Contains("..", StringComparison.Ordinal) ||
                    (name.Length >= 2 && char.IsLetter(name[0]) && name[1] == ':'))
                {
                    evidence.Add(new ThreatEvidence(
                        "archive.path_traversal",
                        RulesVersion,
                        ThreatLevel.HighRisk,
                        $"归档条目包含越界路径：{name}",
                        file.RelativePath));
                }

                totalUncompressed += entry.Length;
                totalCompressed += entry.CompressedLength;
                if (IsArchive(name))
                {
                    nestedArchives++;
                }

                if (ScriptExtensions.Contains(Path.GetExtension(name)))
                {
                    scriptEntries.Add(name);
                }

                if (scannedBytes < MaxScannedEntryBytes &&
                    (name.EndsWith(".class", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
                {
                    var read = ReadEntryCapped(entry, 128 * 1024);
                    scannedBytes += read.Length;
                    var text = Encoding.Latin1.GetString(read);
                    var hits = SuspiciousTokens
                        .Where(token => text.Contains(token, StringComparison.Ordinal))
                        .ToArray();
                    if (hits.Length > 0)
                    {
                        evidence.Add(new ThreatEvidence(
                            "archive.suspicious_bytecode",
                            RulesVersion,
                            hits.Length >= 2 ? ThreatLevel.Suspicious : ThreatLevel.Notice,
                            $"归档内 {name} 包含敏感调用特征：{string.Join("、", hits)}。",
                            file.RelativePath));
                    }
                }
            }

            if (scriptEntries.Count > 0)
            {
                evidence.Add(new ThreatEvidence(
                    "archive.executable_payload",
                    RulesVersion,
                    ThreatLevel.Suspicious,
                    $"归档包含可执行/脚本条目：{string.Join("、", scriptEntries.Take(5))}",
                    file.RelativePath));
            }

            if (nestedArchives > 100)
            {
                evidence.Add(new ThreatEvidence(
                    "archive.nested_limit",
                    RulesVersion,
                    ThreatLevel.Notice,
                    $"归档嵌套归档数量较多：{nestedArchives}。",
                    file.RelativePath));
            }

            if (totalCompressed > 0 &&
                totalUncompressed > 100L * 1024 * 1024 &&
                totalUncompressed / Math.Max(1, totalCompressed) > 200)
            {
                evidence.Add(new ThreatEvidence(
                    "archive.compression_ratio",
                    RulesVersion,
                    ThreatLevel.Suspicious,
                    "归档压缩比异常，可能是压缩炸弹。",
                    file.RelativePath));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                              or UnauthorizedAccessException)
        {
            evidence.Add(new ThreatEvidence(
                "archive.read_failed",
                RulesVersion,
                ThreatLevel.Notice,
                $"归档无法完整读取：{exception.Message}",
                file.RelativePath));
        }

        return evidence;
    }

    private static byte[] ReadEntryCapped(ZipArchiveEntry entry, int maxBytes)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream(Math.Min((int)Math.Min(entry.Length, maxBytes), maxBytes));
        var buffer = new byte[Math.Min(maxBytes, 64 * 1024)];
        var total = 0;
        while (total < maxBytes)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes - total));
            if (read == 0)
            {
                break;
            }

            memory.Write(buffer, 0, read);
            total += read;
        }

        return memory.ToArray();
    }

    private static bool IsArchive(string path) =>
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".litematic", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".schem", StringComparison.OrdinalIgnoreCase);

    private static string SnapshotHash(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            useAsync: false);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private Dictionary<string, KnownThreat> LoadKnownThreats()
    {
        var path = Path.Combine(SecurityRoot, "known-threats.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, KnownThreat>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<KnownThreat>>(
                File.ReadAllText(path),
                EclJson.Default);
            return entries?
                .Where(item => item.Sha256.Length == 64)
                .GroupBy(item => item.Sha256, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, KnownThreat>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取本地威胁记录失败：{Path}", path);
            return new Dictionary<string, KnownThreat>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task SaveLatestScanAsync(ThreatScanReport report, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(SecurityRoot, "scans");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{report.InstanceId}.json");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(report, EclJson.Indented),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private async Task<List<RiskAcceptanceRecord>> LoadAcceptancesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AcceptancesPath))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(AcceptancesPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<RiskAcceptanceRecord>>(json, EclJson.Default) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "读取风险接受记录失败。");
            return [];
        }
    }

    private async Task SaveAcceptancesAsync(
        List<RiskAcceptanceRecord> acceptances,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(SecurityRoot);
        var temporary = $"{AcceptancesPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(acceptances, EclJson.Indented),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, AcceptancesPath, overwrite: true);
    }

    private static bool IsAccepted(
        List<RiskAcceptanceRecord> acceptances,
        ThreatFinding finding) =>
        acceptances.Any(record =>
            string.Equals(record.RelativePath, finding.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.Sha256, finding.Sha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(record.RulesVersion, RulesVersion, StringComparison.Ordinal) &&
            string.Equals(record.ScannerVersion, ScannerVersion, StringComparison.Ordinal) &&
            record.Level >= finding.Level);

    private static bool IsSafeQuarantineId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 80 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主结果。
        }
    }

    private sealed record ScanCandidate(string RelativePath, string FullPath);

    public sealed record KnownThreat(string Sha256, ThreatLevel Level, string Name);
}
