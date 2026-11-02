// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Ecl.Deployment;

namespace Ecl.Additions;

/// <summary>
/// 稳定且全局唯一的 Addition ID，例如 <c>ecl.vanilla-toolkit</c>。只允许小写字母、数字、
/// 点与连字符；它用于目录名与依赖引用，不接受路径分隔符（ADR 0032 §6）。
/// </summary>
public readonly partial struct AdditionId : IEquatable<AdditionId>
{
    public AdditionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IdRegex().IsMatch(value))
        {
            throw new ArgumentException($"Addition ID 无效：{value}", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static bool TryParse([NotNullWhen(true)] string? value, out AdditionId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value) || !IdRegex().IsMatch(value))
        {
            return false;
        }

        id = new AdditionId(value);
        return true;
    }

    public static AdditionId Parse(string value) =>
        TryParse(value, out var id) ? id : throw new FormatException($"Addition ID 无效：{value}");

    public bool Equals(AdditionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is AdditionId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public static bool operator ==(AdditionId left, AdditionId right) => left.Equals(right);

    public static bool operator !=(AdditionId left, AdditionId right) => !left.Equals(right);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$")]
    private static partial Regex IdRegex();
}

public enum AdditionType
{
    Feature,
    Data,
}

/// <summary>扩展的生命周期状态；Installed、Enabled、Loaded 是不同概念（ADR 0032 §7）。</summary>
public enum AdditionState
{
    Available,
    Downloading,
    Installing,
    Installed,
    Enabled,
    UpdateAvailable,
    Incompatible,
    Broken,
}

public sealed record AdditionPackage(string Url, long Size, string Sha256, string? ReleaseTag = null);

public sealed record AdditionCoreApiRange(ReleaseVersion Min, ReleaseVersion Max)
{
    public bool Contains(ReleaseVersion version) => version.CompareTo(Min) >= 0 && version.CompareTo(Max) <= 0;
}

public sealed record AdditionDependency(
    AdditionId Id,
    ReleaseVersion MinVersion,
    ReleaseVersion? MaxVersion = null,
    bool Optional = false)
{
    public bool IsSatisfiedBy(ReleaseVersion version) =>
        version.CompareTo(MinVersion) >= 0 && (MaxVersion is null || version.CompareTo(MaxVersion) <= 0);
}

/// <summary>官方目录中的一条扩展。目录本身由发布者签名，条目中的哈希用于包校验。</summary>
public sealed record AdditionEntry(
    AdditionId Id,
    AdditionType Type,
    string Name,
    string Description,
    ReleaseVersion Version,
    AdditionPackage Package,
    AdditionCoreApiRange CoreApi,
    IReadOnlyList<AdditionDependency> Dependencies,
    IReadOnlyList<AdditionId> ConflictsWith,
    int? DataSchemaVersion,
    IReadOnlyList<string> Platforms,
    bool Withdrawn)
{
    public bool SupportsPlatform(string platform) =>
        Platforms.Count == 0 || Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);
}

public sealed record AdditionCatalog(
    int SchemaVersion,
    ReleaseVersion CoreApiVersion,
    IReadOnlyList<AdditionEntry> Additions);

/// <summary>安装包内的自描述清单；包内容必须在安装前逐文件校验。</summary>
public sealed record AdditionPackageManifest(
    int SchemaVersion,
    AdditionId Id,
    ReleaseVersion Version,
    string EntryAssembly,
    IReadOnlyList<AdditionPackageFile> Files,
    int? DataSchemaVersion = null);

public sealed record AdditionPackageFile(string Path, long Size, string Sha256);

public sealed record InstalledAdditionFile(string Path, long Size, string Sha256);

/// <summary>
/// 一个已安装扩展的本地记录。它保存安装来源、入口程序集与上一版本，便于回滚与按需加载；
/// 不保存任何用户数据路径，卸载不会触碰用户数据目录（ADR 0032 §7）。
/// </summary>
public sealed record InstalledAddition(
    AdditionId Id,
    AdditionType Type,
    ReleaseVersion Version,
    string Directory,
    string EntryAssembly,
    bool Enabled,
    int? DataSchemaVersion,
    IReadOnlyList<InstalledAdditionFile> Files,
    DateTimeOffset InstalledAt,
    ReleaseVersion? PreviousVersion = null)
{
    /// <summary>安装时声明的必需依赖 ID，用于卸载前检查引用关系。</summary>
    public IReadOnlyList<AdditionId> Dependencies { get; init; } = [];

    /// <summary>安装时声明的 Core API 兼容范围，用于按需加载前复核。</summary>
    public ReleaseVersion? CoreApiMin { get; init; }

    public ReleaseVersion? CoreApiMax { get; init; }
}

public sealed class InstalledAdditionsDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<InstalledAddition> Additions { get; set; } = [];
}

/// <summary>安装计划预览：安装、更新、依赖变化与警告必须在下载前可见（ADR 0032 §6）。</summary>
public sealed record PlannedAddition(AdditionEntry Entry, string Reason);

public sealed record AdditionInstallPlan(
    IReadOnlyList<PlannedAddition> Install,
    IReadOnlyList<PlannedAddition> Update,
    IReadOnlyList<AdditionId> Remove,
    IReadOnlyList<string> Warnings)
{
    /// <summary>按依赖优先顺序排列的安装/更新项；执行时必须使用此顺序，不能把 Install 与 Update 简单拼接。</summary>
    public IReadOnlyList<PlannedAddition> Ordered { get; init; } = [];

    public IReadOnlyList<PlannedAddition> All => Ordered.Count > 0
        ? Ordered
        : Install.Concat(Update).ToArray();

    public bool IsEmpty => Install.Count == 0 && Update.Count == 0 && Remove.Count == 0;
}

/// <summary>受控能力接口：扩展只能通过 ECL 定义的领域接口提供能力，不能注入任意 UI。</summary>
public interface IAdditionModule
{
    ValueTask InitializeAsync(AdditionHostContext context, CancellationToken cancellationToken);

    ValueTask ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>宿主提供给扩展的最小上下文；不暴露启动器内部对象、ViewModel 或 UI 线程。</summary>
public sealed record AdditionHostContext(
    AdditionId Id,
    ReleaseVersion AdditionVersion,
    ReleaseVersion CoreApiVersion,
    string DataDirectory);
