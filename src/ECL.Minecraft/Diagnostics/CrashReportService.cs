// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;

namespace Ecl.Minecraft.Diagnostics;

/// <summary>
/// 从实例的 crash-reports / latest.log 中提取崩溃描述，并按 Mod ID/名称出现次数排序嫌疑。
/// 只读文件，不启动游戏；解析失败时返回可读错误。
/// </summary>
public sealed class CrashReportService(
    IInstanceService instances,
    IModService mods,
    IAppPaths paths) : ICrashReportService
{
    private const int MaxReportChars = 1_500_000;

    private static readonly HashSet<string> PlatformModIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft",
        "java",
        "forge",
        "neoforge",
        "fabricloader",
        "fabric-loader",
        "fabric",
        "fml",
        "cleanroom",
        "optifine",
        "javafml",
        "lowcodefml",
    };

    public async Task<Result<CrashAnalysis?>> AnalyzeLatestAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<CrashAnalysis?>.Fail("instance.not_found", "实例不存在。");
        }

        var gameDirectory = InstancePaths.GetGameDirectory(paths, instance);
        var source = FindNewestCrashReport(gameDirectory)
                     ?? FindCrashInLatestLog(gameDirectory);
        if (source is null)
        {
            return Result<CrashAnalysis?>.Ok(null);
        }

        try
        {
            var text = await ReadCappedAsync(source, cancellationToken).ConfigureAwait(false);
            var analysis = Parse(source, text, await ReadModNamesAsync(instance.Id, cancellationToken).ConfigureAwait(false));
            return Result<CrashAnalysis?>.Ok(analysis);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<CrashAnalysis?>.Fail(
                "crash.read_failed",
                $"读取崩溃报告失败：{exception.Message}");
        }
    }

    public static CrashAnalysis Parse(
        string sourcePath,
        string text,
        IReadOnlyDictionary<string, string>? knownMods = null)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var description = FirstValue(lines, "Description:") ?? "未知崩溃";
        var minecraft = FirstValue(lines, "Minecraft Version:");
        var java = FirstValue(lines, "Java Version:");

        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (knownMods is not null)
        {
            foreach (var pair in knownMods)
            {
                AddCandidate(candidates, pair.Key, pair.Value);
            }
        }

        ParseModList(lines, candidates);

        var suspects = candidates
            .Where(pair => !PlatformModIds.Contains(pair.Key))
            .Select(pair =>
            {
                var occurrences = CountOccurrences(text, pair.Key, pair.Value);
                if (occurrences == 0)
                {
                    return null;
                }

                var evidence = FindEvidence(lines, pair.Key, pair.Value);
                var stackBonus = evidence.Count(line =>
                    line.Contains(" at ", StringComparison.Ordinal) ||
                    line.Contains("Caused by", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Error", StringComparison.OrdinalIgnoreCase));
                return new CrashSuspect(pair.Key, pair.Value, occurrences + stackBonus * 3, evidence);
            })
            .Where(suspect => suspect is not null)
            .Select(suspect => suspect!)
            .OrderByDescending(suspect => suspect.Score)
            .ThenBy(suspect => suspect.ModId, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        // 摘要保持与语言无关；可疑 Mod 由 UI/CLI 按当前语言渲染。
        var summary = description;

        var keyLines = lines
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .Where(line =>
                line.Contains("Description:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Caused by", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("--", StringComparison.Ordinal) ||
                line.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToArray();

        return new CrashAnalysis(sourcePath, summary, description, minecraft, java, suspects, keyLines);
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadModNamesAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var scan = await mods.ScanAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (!scan.IsSuccess)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return scan.Value!.Mods
                .Where(mod => mod.Metadata is not null)
                .GroupBy(mod => mod.Metadata!.ModId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Metadata!.Name,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? FindNewestCrashReport(string gameDirectory)
    {
        var directory = Path.Combine(gameDirectory, "crash-reports");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string? FindCrashInLatestLog(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, "logs", "latest.log");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            if (text.Length > MaxReportChars)
            {
                text = text[^MaxReportChars..];
            }

            return text.Contains("Minecraft Crash Report", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string> ReadCappedAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length <= MaxReportChars)
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[MaxReportChars];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string? FirstValue(IReadOnlyList<string> lines, string key)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[key.Length..].Trim();
            }
        }

        return null;
    }

    private static void ParseModList(IReadOnlyList<string> lines, IDictionary<string, string> candidates)
    {
        var inModList = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals("Fabric Mods:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Mod List:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Loaded Mods:", StringComparison.OrdinalIgnoreCase))
            {
                inModList = true;
                continue;
            }

            if (!inModList)
            {
                continue;
            }

            if (trimmed.Length == 0)
            {
                inModList = false;
                continue;
            }

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var id = trimmed[..separator].Trim();
            var rest = trimmed[(separator + 1)..].Trim();
            if (id.Length == 0 || id.Contains(' '))
            {
                continue;
            }

            var name = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            AddCandidate(candidates, id, name ?? id);
        }
    }

    private static void AddCandidate(IDictionary<string, string> candidates, string id, string? name)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length < 3 || PlatformModIds.Contains(id))
        {
            return;
        }

        candidates.TryAdd(id, string.IsNullOrWhiteSpace(name) ? id : name);
    }

    private static int CountOccurrences(string text, string id, string? name)
    {
        var count = Count(text, id);
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
        {
            count += Count(text, name);
        }

        return count;
    }

    private static int Count(string text, string value)
    {
        if (value.Length < 2)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static IReadOnlyList<string> FindEvidence(IReadOnlyList<string> lines, string id, string? name)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (!line.Contains(id, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(name) || !line.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || result.Contains(trimmed, StringComparer.Ordinal))
            {
                continue;
            }

            result.Add(trimmed.Length > 300 ? trimmed[..300] : trimmed);
            if (result.Count >= 3)
            {
                break;
            }
        }

        return result;
    }
}
