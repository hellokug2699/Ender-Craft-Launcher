// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace Ecl.Diagnostics;

/// <summary>诊断输入来源。游戏日志与崩溃报告会被归一到同一事件模型，便于规则统一匹配。</summary>
public enum DiagnosticSourceKind
{
    GameLog,
    CrashReport,
}

/// <summary>诊断结论严重度。数值越大越严重，便于排序；JSON 中仍以 camelCase 字符串保存。</summary>
public enum DiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Critical = 3,
}

/// <summary>
/// 结论置信度。数值越大证据越强，便于排序与汇总；JSON 中保存为 camelCase 字符串。
/// <para><see cref="DirectEvidence"/>：直接证据，规则命中的上下文足以支撑该结论。</para>
/// <para><see cref="HighlyLikely"/>：高度疑似，证据较强但仍存在其他解释。</para>
/// <para><see cref="Undetermined"/>：无法确定，不得向用户表述为“根因”。</para>
/// </summary>
public enum DiagnosticConfidence
{
    Undetermined = 0,
    HighlyLikely = 1,
    DirectEvidence = 2,
}

/// <summary>带原始行号的一行证据文本。行号始终是解析时在来源文件中的 1 基行号。</summary>
public sealed record SourceLine(int Number, string Text);

/// <summary>
/// 一条结构化日志事件：一个可识别的日志头，加上归属于它的后续堆栈行/缩进行。
/// 后续行不会被拆成独立事件，避免堆栈证据与异常头分离。
/// </summary>
public sealed record ParsedLogEvent
{
    /// <summary>来源文件名（不保存完整本机路径，降低隐私泄露面）。</summary>
    public string File { get; init; } = "";

    public DiagnosticSourceKind SourceKind { get; init; } = DiagnosticSourceKind.GameLog;

    /// <summary>事件首行的 1 基行号。</summary>
    public int Line { get; init; }

    /// <summary>事件最后一行的 1 基行号（含堆栈/缩进续行）。</summary>
    public int EndLine { get; init; }

    /// <summary>latest.log 头中的时间（只有时间，没有日期）。</summary>
    public TimeOnly? Time { get; init; }

    /// <summary>崩溃报告中可能出现的完整时间戳。</summary>
    public DateTimeOffset? Timestamp { get; init; }

    public string? Thread { get; init; }

    public string Level { get; init; } = "INFO";

    public string? Logger { get; init; }

    /// <summary>日志头中的消息（不含时间、线程、级别、logger）。</summary>
    public string Message { get; init; } = "";

    /// <summary>事件中第一次出现的异常类型；不是“最后一行的异常类”。</summary>
    public string? ExceptionType { get; init; }

    public string? ExceptionMessage { get; init; }

    /// <summary>事件中按出现顺序去重的全部异常类型，用于保留证据而不是只取最后一个。</summary>
    public IReadOnlyList<string> ExceptionTypes { get; init; } = [];

    /// <summary>事件覆盖的全部原始行（首行 + 续行），每行保留真实行号。</summary>
    public IReadOnlyList<SourceLine> Lines { get; init; } = [];

    /// <summary>
    /// 内存中的原始文本。诊断历史只保存证据摘录，不保存它，避免复制整份原始日志。
    /// </summary>
    [JsonIgnore]
    public string RawText => string.Join('\n', Lines.Select(line => line.Text));
}

/// <summary>单个日志文件的解析结果。损坏或截断只影响这个文件，不影响其他输入。</summary>
public sealed record LogParseResult
{
    public string File { get; init; } = "";

    public IReadOnlyList<ParsedLogEvent> Events { get; init; } = [];

    public int LinesRead { get; init; }

    public long BytesRead { get; init; }

    /// <summary>是否因为文件大小、行数或单行长度上限而被截断。</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>崩溃报告中的一个模组条目。</summary>
public sealed record CrashReportMod(string Id, string? Name, string? Version, string Raw);

/// <summary>崩溃报告中的 <c>-- Section --</c> 区段。</summary>
public sealed record CrashReportSection(string Name, int StartLine, IReadOnlyList<SourceLine> Lines);

/// <summary>
/// 崩溃报告的结构化结果。除文件头外所有字段都可能缺失，解析器必须容错而不是抛异常。
/// </summary>
public sealed record CrashReport
{
    /// <summary>来源文件名（不保存完整本机路径）。</summary>
    public string File { get; init; } = "";

    public bool HasHeader { get; init; }

    public DateTimeOffset? Time { get; init; }

    public string? Description { get; init; }

    public int? DescriptionLine { get; init; }

    public string? MinecraftVersion { get; init; }

    public string? JavaVersion { get; init; }

    public string? OperatingSystem { get; init; }

    /// <summary>报告中第一个异常头（通常是顶层异常），不是最后一个 <c>Caused by</c>。</summary>
    public string? ExceptionType { get; init; }

    public string? ExceptionMessage { get; init; }

    public int? ExceptionLine { get; init; }

    /// <summary>异常头、<c>at</c>、<c>Caused by</c>、<c>Suppressed</c> 等堆栈相关行。</summary>
    public IReadOnlyList<SourceLine> StackTrace { get; init; } = [];

    public IReadOnlyList<CrashReportMod> Mods { get; init; } = [];

    /// <summary>模组列表区段的原始行（含区段头），供规则匹配并保留行号。</summary>
    public IReadOnlyList<SourceLine> ModListLines { get; init; } = [];

    public IReadOnlyList<CrashReportSection> Sections { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 可选的环境上下文摘要。缺失字段不会阻塞诊断；规则可自行决定是否需要环境数据。
/// </summary>
public sealed record DiagnosticEnvironment
{
    public string? MinecraftVersion { get; init; }

    public string? Loader { get; init; }

    public string? LoaderVersion { get; init; }

    public string? JavaVersion { get; init; }

    public int? JavaMajorVersion { get; init; }

    public int? ModCount { get; init; }

    public string? OperatingSystem { get; init; }

    /// <summary>模组 ID 摘要；不要在这里放完整模组元数据或文件内容。</summary>
    public IReadOnlyList<string> ModIds { get; init; } = [];

    /// <summary>面向报告的环境摘要文本。</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(MinecraftVersion))
            {
                parts.Add($"Minecraft {MinecraftVersion}");
            }

            if (!string.IsNullOrWhiteSpace(Loader))
            {
                parts.Add(string.IsNullOrWhiteSpace(LoaderVersion) ? Loader : $"{Loader} {LoaderVersion}");
            }

            if (!string.IsNullOrWhiteSpace(JavaVersion))
            {
                parts.Add($"Java {JavaVersion}");
            }
            else if (JavaMajorVersion is { } major)
            {
                parts.Add($"Java {major}");
            }

            if (ModCount is { } count)
            {
                parts.Add($"{count} 个模组");
            }
            else if (ModIds.Count > 0)
            {
                parts.Add($"{ModIds.Count} 个模组");
            }

            if (!string.IsNullOrWhiteSpace(OperatingSystem))
            {
                parts.Add(OperatingSystem);
            }

            return parts.Count == 0 ? "未提供环境信息" : string.Join(" · ", parts);
        }
    }
}

/// <summary>读取或解析单个输入文件时产生的问题。问题被收集而不是抛出，避免一个坏文件阻塞整体诊断。</summary>
public sealed record DiagnosticInputIssue(string File, string Code, string Message);

/// <summary>一批本地诊断输入。每个文件独立解析，失败项放在 <see cref="Issues"/> 中。</summary>
public sealed record DiagnosticInput
{
    public static DiagnosticInput Empty { get; } = new();

    public IReadOnlyList<ParsedLogEvent> Events { get; init; } = [];

    public IReadOnlyList<CrashReport> CrashReports { get; init; } = [];

    public IReadOnlyList<DiagnosticInputIssue> Issues { get; init; } = [];
}
