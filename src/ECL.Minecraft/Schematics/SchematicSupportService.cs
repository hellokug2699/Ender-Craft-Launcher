// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Engine.Inventory;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;

namespace Ecl.Minecraft.Schematics;

/// <summary>
/// 投影支持状态（schematics.md §4）。只有 <see cref="Supported"/> 表示已经用真实
/// 组件元数据证明当前实例具备至少一种格式的加载能力；其余状态必须如实呈现。
/// </summary>
public enum SchematicSupportState
{
    Supported,
    Missing,
    Incomplete,
    Incompatible,
    Conflict,
    Unknown,
}

/// <summary>单个投影格式的支持判断。</summary>
public sealed record SchematicFormatSupport(
    SchematicFormat Format,
    SchematicSupportState State,
    IReadOnlyList<string> Providers,
    string? Detail = null);

/// <summary>一次投影支持检测结果；页面按格式展示，不自行推断兼容性。</summary>
public sealed record SchematicSupportResult(
    SchematicSupportState State,
    IReadOnlyList<SchematicFormatSupport> Formats,
    IReadOnlyList<string> Providers);

public interface ISchematicSupportService
{
    /// <summary>
    /// 按实例的真实 Minecraft 版本、加载器、已安装组件与其文件元数据检测投影能力。
    /// 扫描失败返回错误，由页面显示失败状态，不降级为“支持”。
    /// </summary>
    Task<Result<SchematicSupportResult>> DetectAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 投影支持检测（Public <c>docs/schematic-support/README.md</c>）。
/// <para>
/// 依据只有：实例的 Minecraft 版本与加载器、已启用模组的真实文件元数据（平台、
/// <c>minecraft</c> 版本范围、必需依赖）。不按文件名猜测组件，不把“扩展名可识别”
/// 当成“游戏内能加载”，也不为未验证的组合伪造支持。
/// </para>
/// </summary>
public static class SchematicSupportDetector
{
    public const string LitematicaModId = "litematica";
    public const string ForgematicaModId = "forgematica";
    public const string WorldEditModId = "worldedit";
    public const string CreateModId = "create";
    public const string AxiomModId = "axiom";

    /// <summary>不是投影组件本身、也不应作为依赖判定对象的加载器/运行时 id。</summary>
    private static readonly string[] NonProviderDependencyIds =
    [
        "minecraft", "java", "forge", "neoforge", "fabricloader", "fabric-loader", "quiltloader",
        "cleanroom",
    ];

    private sealed record ProviderRule(string ModId, SchematicFormat[] Formats);

    /// <summary>
    /// 已核实的投影提供者。规则只做“组件 → 它能处理的格式”映射；加载器、Minecraft 版本与
    /// 必需依赖一律以具体安装文件的元数据为准，避免把项目页的宽泛声明当成每个文件的证明。
    /// </summary>
    private static readonly ProviderRule[] Rules =
    [
        new(LitematicaModId, [SchematicFormat.Litematica]),
        new(ForgematicaModId, [SchematicFormat.Litematica]),
        new(WorldEditModId, [SchematicFormat.SpongeSchematic, SchematicFormat.MceSchematic]),
        new(CreateModId, [SchematicFormat.NbtStructure]),
        new(AxiomModId, [SchematicFormat.AxiomBlueprint]),
    ];

    public static SchematicSupportResult Evaluate(GameInstance instance, IReadOnlyList<ModEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mods);

        var enabled = mods.Where(mod => mod.Enabled && mod.Metadata is not null).ToArray();
        var available = BuildAvailableVersions(enabled);
        var installed = new List<string>();
        var verdicts = new Dictionary<SchematicFormat, List<ProviderVerdict>>();

        foreach (var rule in Rules)
        {
            var mod = Find(enabled, rule.ModId);
            if (mod is null)
            {
                continue;
            }

            installed.Add(rule.ModId);
            var verdict = EvaluateProvider(instance, mod.Metadata!, available, rule.ModId);
            foreach (var format in rule.Formats)
            {
                if (!verdicts.TryGetValue(format, out var list))
                {
                    list = [];
                    verdicts[format] = list;
                }

                list.Add(verdict);
            }
        }

        var formats = Enum.GetValues<SchematicFormat>()
            .Select(format => BuildFormatSupport(format, verdicts.GetValueOrDefault(format)))
            .ToArray();

        var state = Combine(formats);
        return new SchematicSupportResult(state, formats, installed);
    }

    private sealed record ProviderVerdict(string ModId, SchematicSupportState State, string? Detail);

    private static ProviderVerdict EvaluateProvider(
        GameInstance instance,
        ModMetadata metadata,
        IReadOnlyDictionary<string, string> available,
        string providerId)
    {
        var minecraft = ModCompatibility.EvaluateMinecraft(instance, metadata);
        var platform = ModCompatibility.EvaluateLoader(instance.Loader, metadata);
        if (minecraft == CompatibilityState.Incompatible || platform == CompatibilityState.Incompatible)
        {
            return new ProviderVerdict(providerId, SchematicSupportState.Incompatible, "artifact-mismatch");
        }

        if (minecraft != CompatibilityState.Compatible || platform != CompatibilityState.Compatible)
        {
            return new ProviderVerdict(providerId, SchematicSupportState.Unknown, "artifact-unverified");
        }

        var missing = FindMissingMandatoryDependency(metadata, available);
        return missing is null
            ? new ProviderVerdict(providerId, SchematicSupportState.Supported, null)
            : new ProviderVerdict(providerId, SchematicSupportState.Incomplete, missing);
    }

    private static SchematicFormatSupport BuildFormatSupport(
        SchematicFormat format,
        IReadOnlyList<ProviderVerdict>? verdicts)
    {
        if (verdicts is null || verdicts.Count == 0)
        {
            return new SchematicFormatSupport(format, SchematicSupportState.Missing, []);
        }

        var providers = verdicts.Select(verdict => verdict.ModId).Distinct(StringComparer.Ordinal).ToArray();
        var supported = verdicts.Where(verdict => verdict.State == SchematicSupportState.Supported).ToArray();
        if (supported.Length >= 2)
        {
            // 多个组件同时声称支持同一格式：ECL 无法证明哪个会实际生效，按冲突呈现。
            return new SchematicFormatSupport(
                format,
                SchematicSupportState.Conflict,
                providers,
                string.Join(",", supported.Select(verdict => verdict.ModId)));
        }

        if (supported.Length == 1)
        {
            return new SchematicFormatSupport(format, SchematicSupportState.Supported, providers);
        }

        var incomplete = verdicts.FirstOrDefault(verdict => verdict.State == SchematicSupportState.Incomplete);
        if (incomplete is not null)
        {
            return new SchematicFormatSupport(
                format,
                SchematicSupportState.Incomplete,
                providers,
                incomplete.Detail);
        }

        var unknown = verdicts.FirstOrDefault(verdict => verdict.State == SchematicSupportState.Unknown);
        if (unknown is not null)
        {
            return new SchematicFormatSupport(
                format,
                SchematicSupportState.Unknown,
                providers,
                unknown.Detail);
        }

        return new SchematicFormatSupport(
            format,
            SchematicSupportState.Incompatible,
            providers,
            verdicts[0].Detail);
    }

    private static SchematicSupportState Combine(IReadOnlyList<SchematicFormatSupport> formats)
    {
        if (formats.Any(format => format.State == SchematicSupportState.Supported))
        {
            return SchematicSupportState.Supported;
        }

        if (formats.Any(format => format.State == SchematicSupportState.Conflict))
        {
            return SchematicSupportState.Conflict;
        }

        if (formats.Any(format => format.State == SchematicSupportState.Incomplete))
        {
            return SchematicSupportState.Incomplete;
        }

        if (formats.Any(format => format.State == SchematicSupportState.Unknown))
        {
            return SchematicSupportState.Unknown;
        }

        if (formats.Any(format => format.State == SchematicSupportState.Incompatible))
        {
            return SchematicSupportState.Incompatible;
        }

        return SchematicSupportState.Missing;
    }

    /// <summary>返回第一个缺失或不满足版本范围的必需依赖 mod id；全部满足时返回 <c>null</c>。</summary>
    private static string? FindMissingMandatoryDependency(
        ModMetadata metadata,
        IReadOnlyDictionary<string, string> available)
    {
        foreach (var dependency in metadata.Dependencies)
        {
            if (!dependency.Mandatory ||
                NonProviderDependencyIds.Contains(dependency.ModId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!available.TryGetValue(dependency.ModId, out var version))
            {
                return dependency.ModId;
            }

            if (ModVersionRange.Matches(version, dependency.VersionRange) == false)
            {
                return dependency.ModId;
            }
        }

        return null;
    }

    private static Dictionary<string, string> BuildAvailableVersions(IEnumerable<ModEntry> mods)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            var metadata = mod.Metadata;
            if (metadata is null)
            {
                continue;
            }

            versions.TryAdd(metadata.ModId, metadata.Version);
            foreach (var provided in metadata.ProvidedIds)
            {
                versions.TryAdd(provided, metadata.Version);
            }
        }

        return versions;
    }

    private static ModEntry? Find(IEnumerable<ModEntry> mods, string modId) =>
        mods.FirstOrDefault(mod =>
            string.Equals(mod.Metadata?.ModId, modId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>读取实例与已安装模组，交给 <see cref="SchematicSupportDetector"/> 判定。</summary>
public sealed class SchematicSupportService(
    IInstanceService instances,
    IModService mods) : ISchematicSupportService
{
    public async Task<Result<SchematicSupportResult>> DetectAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<SchematicSupportResult>.Fail("instance.not_found", "实例不存在。");
        }

        var scan = await mods.ScanAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
        {
            return Result<SchematicSupportResult>.Fail(scan.Error!);
        }

        return Result<SchematicSupportResult>.Ok(SchematicSupportDetector.Evaluate(instance, scan.Value!.Mods));
    }
}
