// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;
using Ecl.Minecraft.Engine.Inventory;
using Ecl.Minecraft.Instances;

namespace Ecl.Minecraft.Mods;

/// <summary>
/// 光影支持检测结果（Public <c>docs/shader-support/rules.json</c> 的 resultStates）。
/// 只有 <see cref="Supported"/> 可以隐藏“缺少光影支持”的提示；其余状态必须如实呈现。
/// </summary>
public enum ShaderSupportState
{
    Supported,
    Missing,
    Incomplete,
    Incompatible,
    Conflict,
    Unknown,
}

/// <summary>
/// 一次光影支持检测的结果。页面只把 <see cref="State"/> 映射为提示文案，
/// 不自行推断兼容性。
/// </summary>
public sealed record ShaderSupportResult(
    ShaderSupportState State,
    IReadOnlyList<string> Providers,
    string? Detail = null);

public interface IShaderSupportService
{
    /// <summary>
    /// 按 <c>docs/shader-support/</c> 规则检测当前实例是否具备已知的光影包支持。
    /// 扫描失败返回错误，由页面显示失败状态，不降级为“支持”。
    /// </summary>
    Task<Result<ShaderSupportResult>> DetectAsync(
        string instanceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 光影支持检测（shaders.md §2、Public <c>docs/shader-support/README.md</c>）。
/// <para>
/// 判断依据只有：实例的 Minecraft 版本与加载器、已启用模组的真实元数据（平台、
/// <c>minecraft</c> 版本范围、必需依赖）、以及实例记录里的 OptiFine 构建。
/// 不按文件名匹配，不把性能优化模组当成光影支持，不用项目页面的宽泛声明代替具体文件证据。
/// </para>
/// </summary>
public static class ShaderSupportDetector
{
    public const string IrisModId = "iris";
    public const string OculusModId = "oculus";
    public const string OptiFineProvider = "optifine";

    /// <summary>Oculus 目前声明支持的 Minecraft 版本是离散集合，不能按“以上版本”外推。</summary>
    private static readonly string[] OculusMinecraftVersions =
        ["1.16.5", "1.18.2", "1.19", "1.19.2", "1.19.4", "1.20", "1.20.1"];

    /// <summary>不是光影组件本身、也不应作为依赖判定对象的加载器/运行时 id。</summary>
    private static readonly string[] NonProviderDependencyIds =
        ["minecraft", "java", "forge", "neoforge", "fabricloader", "fabric-loader", "cleanroom", "iris", "oculus"];

    public static ShaderSupportResult Evaluate(GameInstance instance, IReadOnlyList<ModEntry> mods)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mods);

        var enabled = mods.Where(mod => mod.Enabled && mod.Metadata is not null).ToArray();
        var iris = Find(enabled, IrisModId);
        var oculus = Find(enabled, OculusModId);
        var optiFine = !string.IsNullOrWhiteSpace(instance.OptiFineVersion);

        var providers = new List<string>();
        if (iris is not null)
        {
            providers.Add(IrisModId);
        }

        if (oculus is not null)
        {
            providers.Add(OculusModId);
        }

        if (optiFine)
        {
            providers.Add(OptiFineProvider);
        }

        if (providers.Count == 0)
        {
            return new ShaderSupportResult(ShaderSupportState.Missing, []);
        }

        // 规则库冲突：OptiFine 与 Iris/Oculus 互斥（Iris 文档明确 OptiFine 不兼容）。
        if (providers.Count > 1)
        {
            return new ShaderSupportResult(
                ShaderSupportState.Conflict,
                providers,
                string.Join(",", providers));
        }

        var available = BuildAvailableVersions(enabled);
        var verdict = iris is not null
            ? EvaluateIris(instance, iris.Metadata!, available)
            : oculus is not null
                ? EvaluateOculus(instance, oculus.Metadata!, available)
                : new ShaderSupportResult(ShaderSupportState.Unknown, providers, OptiFineProvider);

        return verdict with { Providers = providers };
    }

    private static ShaderSupportResult EvaluateIris(
        GameInstance instance,
        ModMetadata metadata,
        IReadOnlyDictionary<string, string> available)
    {
        var loader = instance.Loader.Trim().ToLowerInvariant();
        if (loader == "forge")
        {
            // Iris 官方声明不支持 Forge。
            return new ShaderSupportResult(ShaderSupportState.Incompatible, [IrisModId], "iris-forge-unsupported");
        }

        if (loader is not ("fabric" or "neoforge"))
        {
            return new ShaderSupportResult(ShaderSupportState.Unknown, [IrisModId], "iris-loader-unknown");
        }

        // Iris 官方支持 Fabric 1.16.5+ 与 NeoForge 1.21.1+；更早版本不猜测。
        var minimum = loader == "fabric" ? ">=1.16.5" : ">=1.21.1";
        if (ModVersionRange.Matches(instance.GameVersion, minimum) == false)
        {
            return new ShaderSupportResult(ShaderSupportState.Incompatible, [IrisModId], "iris-version-unsupported");
        }

        var minecraft = ModCompatibility.EvaluateMinecraft(instance, metadata);
        var platform = ModCompatibility.EvaluateLoader(instance.Loader, metadata);
        if (minecraft == CompatibilityState.Incompatible || platform == CompatibilityState.Incompatible)
        {
            return new ShaderSupportResult(ShaderSupportState.Incompatible, [IrisModId], "iris-artifact-mismatch");
        }

        if (minecraft != CompatibilityState.Compatible || platform != CompatibilityState.Compatible)
        {
            return new ShaderSupportResult(ShaderSupportState.Unknown, [IrisModId], "iris-artifact-unverified");
        }

        // Iris 自带 Sodium，不要求单独安装 Sodium。
        var missing = FindMissingMandatoryDependency(metadata, available);
        return missing is null
            ? new ShaderSupportResult(ShaderSupportState.Supported, [IrisModId])
            : new ShaderSupportResult(ShaderSupportState.Incomplete, [IrisModId], missing);
    }

    private static ShaderSupportResult EvaluateOculus(
        GameInstance instance,
        ModMetadata metadata,
        IReadOnlyDictionary<string, string> available)
    {
        var loader = instance.Loader.Trim().ToLowerInvariant();
        if (loader is not ("forge" or "neoforge"))
        {
            return new ShaderSupportResult(ShaderSupportState.Incompatible, [OculusModId], "oculus-loader-unsupported");
        }

        if (!OculusMinecraftVersions.Contains(instance.GameVersion, StringComparer.Ordinal))
        {
            return new ShaderSupportResult(ShaderSupportState.Incompatible, [OculusModId], "oculus-version-unsupported");
        }

        var minecraft = ModCompatibility.EvaluateMinecraft(instance, metadata);
        var platform = ModCompatibility.EvaluateLoader(instance.Loader, metadata);
        if (minecraft == CompatibilityState.Incompatible || platform == CompatibilityState.Incompatible)
        {
            return new ShaderSupportResult(ShaderSupportState.Incompatible, [OculusModId], "oculus-artifact-mismatch");
        }

        if (minecraft != CompatibilityState.Compatible || platform != CompatibilityState.Compatible)
        {
            return new ShaderSupportResult(ShaderSupportState.Unknown, [OculusModId], "oculus-artifact-unverified");
        }

        // 依赖以具体安装文件的元数据为准：不硬编码 Embeddium/Rubidium。
        var missing = FindMissingMandatoryDependency(metadata, available);
        return missing is null
            ? new ShaderSupportResult(ShaderSupportState.Supported, [OculusModId])
            : new ShaderSupportResult(ShaderSupportState.Incomplete, [OculusModId], missing);
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

/// <summary>读取实例与已安装模组，交给 <see cref="ShaderSupportDetector"/> 判定。</summary>
public sealed class ShaderSupportService(
    IInstanceService instances,
    IModService mods) : IShaderSupportService
{
    public async Task<Result<ShaderSupportResult>> DetectAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await instances.GetAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return Result<ShaderSupportResult>.Fail("instance.not_found", "实例不存在。");
        }

        var scan = await mods.ScanAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
        {
            return Result<ShaderSupportResult>.Fail(scan.Error!);
        }

        return Result<ShaderSupportResult>.Ok(ShaderSupportDetector.Evaluate(instance, scan.Value!.Mods));
    }
}
