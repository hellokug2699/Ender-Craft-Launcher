// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Ecl.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Minecraft.Engine.Lifecycle;

public enum InstanceLifecycleKind
{
    Relocate,
    Move,
    Reconfigure,
    Delete,
}

/// <summary>
/// 生命周期 journal 的只读投影（F-03 §4.3/§9.3）。三类 journal 形状不同，
/// 这里统一成稳定摘要，供恢复入口、CLI 与诊断共用；不改变任何 journal 的落盘格式。
/// </summary>
public sealed record InstanceLifecycleRecord(
    string PlanId,
    string InstanceId,
    InstanceLifecycleKind Kind,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string JournalPath,
    string? RecordDirectory = null,
    string? GameDirectory = null,
    string? StagingPath = null,
    string? TombstonePath = null,
    string? RetiredSourcePath = null,
    string? DetachedGameDataPath = null,
    string? ErrorCode = null,
    string? ErrorDetail = null,
    int? SchemaVersion = null)
{
    /// <summary>终态：不会再自动变化，重复恢复应保持幂等。</summary>
    public bool IsTerminal => State is "Purged" or "Completed" or "RolledBack" or "Discarded"
        or "Cancelled" or "SourceCleaned";

    public bool NeedsRecovery => State == "NeedsRecovery";

    /// <summary>恢复入口建议动作：resume / resume_purge / cleanup_source / inspect_only / none。</summary>
    public string SuggestedAction => Kind switch
    {
        InstanceLifecycleKind.Move when State is "Planned" or "Copying" or "Verifying" or "Interrupted" => "resume",
        InstanceLifecycleKind.Move when State is "Committed" or "Completed" && RetiredSourcePath is not null =>
            "cleanup_source",
        InstanceLifecycleKind.Move => "none",
        InstanceLifecycleKind.Delete when State is "Quarantined" or "Purging" or "NeedsRecovery"
            && !string.IsNullOrWhiteSpace(TombstonePath) => "resume_purge",
        InstanceLifecycleKind.Delete when State == "Purged" => "none",
        InstanceLifecycleKind.Delete => "inspect_only",
        InstanceLifecycleKind.Reconfigure when State is "Completed" or "RolledBack" => "none",
        InstanceLifecycleKind.Reconfigure => "inspect_only",
        _ => "inspect_only",
    };
}

/// <summary>
/// 生命周期 journal 存储：原子写入 + 按实例/全局枚举。
/// journal 位于 <c>&lt;Config&gt;/instance-engine/lifecycle/&lt;instanceId&gt;/&lt;planId&gt;.json</c>，
/// 属于产品运行时恢复材料，不是临时文件，不得被存储清理删除。
/// </summary>
public sealed class InstanceLifecycleJournalStore(
    IAppPaths paths,
    ILogger<InstanceLifecycleJournalStore>? logger = null)
{
    private readonly ILogger<InstanceLifecycleJournalStore> _logger =
        logger ?? NullLogger<InstanceLifecycleJournalStore>.Instance;

    public string LifecycleRoot => Path.Combine(paths.ConfigDirectory, "instance-engine", "lifecycle");

    public string GetInstanceDirectory(string instanceId) => Path.Combine(LifecycleRoot, instanceId);

    public string GetJournalPath(string instanceId, string planId) =>
        Path.Combine(GetInstanceDirectory(instanceId), $"{planId}.json");

    /// <summary>所有 journal 文件；损坏文件由调用方按 <see cref="TryRead"/> 忽略。</summary>
    public IReadOnlyList<string> EnumerateJournalFiles(string? instanceId = null)
    {
        var root = string.IsNullOrWhiteSpace(instanceId) ? LifecycleRoot : GetInstanceDirectory(instanceId);
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "无法枚举生命周期 journal：{Root}", root);
            return [];
        }
    }

    public string? FindJournalPath(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId) || planId.Contains(Path.DirectorySeparatorChar) ||
            planId.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        if (!Directory.Exists(LifecycleRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(LifecycleRoot, $"{planId}.json", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<Result<InstanceLifecycleRecord>> ReadAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        var path = FindJournalPath(planId);
        if (path is null)
        {
            return Result<InstanceLifecycleRecord>.Fail(
                "instance.lifecycle.journal_missing",
                "未找到该计划的生命周期 journal。");
        }

        var record = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        return record is null
            ? Result<InstanceLifecycleRecord>.Fail(
                "instance.lifecycle.journal_invalid",
                "生命周期 journal 无法解析；未知或损坏的 journal 只能人工检查。")
            : Result<InstanceLifecycleRecord>.Ok(record);
    }

    public async Task<IReadOnlyList<InstanceLifecycleRecord>> ListAsync(
        string? instanceId = null,
        CancellationToken cancellationToken = default)
    {
        var records = new List<InstanceLifecycleRecord>();
        foreach (var path in EnumerateJournalFiles(instanceId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records
            .OrderByDescending(record => record.UpdatedAt)
            .ThenBy(record => record.PlanId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>从 journal 文件路径读取未知形状：字段缺失时保持 null，不猜测。</summary>
    public async Task<InstanceLifecycleRecord?> ReadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var planId = GetString(root, "PlanId");
            var instanceId = GetString(root, "InstanceId");
            if (string.IsNullOrWhiteSpace(planId) || string.IsNullOrWhiteSpace(instanceId))
            {
                return null;
            }

            var kind = DetectKind(root);
            var state = NormalizeState(GetString(root, "State") ?? "Unknown");
            return new InstanceLifecycleRecord(
                planId,
                instanceId,
                kind,
                state,
                GetTime(root, "CreatedAt"),
                GetTime(root, "UpdatedAt"),
                path,
                GetString(root, "RecordDirectory") ?? GetString(root, "SourceRecordDirectory"),
                GetString(root, "GameDirectory") ?? GetString(root, "TargetGameDirectory"),
                GetString(root, "StagingPath"),
                GetString(root, "TombstonePath"),
                GetString(root, "RetiredSourcePath"),
                GetString(root, "DetachedGameDataPath"),
                GetString(root, "ErrorCode"),
                GetString(root, "ErrorDetail") ?? GetString(root, "ErrorMessage"),
                GetInt(root, "SchemaVersion"));
        }
        catch (Exception exception) when (exception is IOException or JsonException or
                                              UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "无法读取生命周期 journal：{Path}", path);
            return null;
        }
    }

    /// <summary>原子写入 journal（临时文件 + 替换），返回落盘路径。</summary>
    public async Task<Result<string>> WriteAsync<T>(
        string instanceId,
        string planId,
        T journal,
        CancellationToken cancellationToken = default)
    {
        var directory = GetInstanceDirectory(instanceId);
        var path = Path.Combine(directory, $"{planId}.json");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(journal, EclJson.Indented),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return Result<string>.Ok(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "无法写入生命周期 journal：{Path}", path);
            return Result<string>.Fail(
                "instance.lifecycle.journal_write_failed",
                "无法写入生命周期 journal，操作已中止。",
                exception.Message);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static InstanceLifecycleKind DetectKind(JsonElement root)
    {
        var explicitKind = GetString(root, "Kind");
        if (!string.IsNullOrWhiteSpace(explicitKind) &&
            Enum.TryParse<InstanceLifecycleKind>(explicitKind, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        if (TryGetProperty(root, "SourceRootId", out _) && TryGetProperty(root, "TargetRootId", out _))
        {
            return InstanceLifecycleKind.Move;
        }

        if (TryGetProperty(root, "TombstonePath", out _) || TryGetProperty(root, "DetachedGameDataPath", out _))
        {
            return InstanceLifecycleKind.Delete;
        }

        return TryGetProperty(root, "TargetRecordDirectory", out _)
            ? InstanceLifecycleKind.Move
            : InstanceLifecycleKind.Reconfigure;
    }

    /// <summary>EclJson 使用 camelCase 落盘；读取时按属性名忽略大小写，兼容旧/新 journal。</summary>
    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>枚举状态落盘为 camelCase（如 needsRecovery），统一成 PascalCase 供比较与展示。</summary>
    private static string NormalizeState(string state) =>
        state.Length == 0 || char.IsUpper(state[0])
            ? state
            : string.Concat(char.ToUpperInvariant(state[0]).ToString(), state.AsSpan(1).ToString());

    private static string? GetString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static DateTimeOffset GetTime(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var time)
            ? time
            : DateTimeOffset.MinValue;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 残留临时文件无害。
        }
    }
}
