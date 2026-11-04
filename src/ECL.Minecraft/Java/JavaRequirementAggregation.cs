// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Loaders;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Java;

/// <summary>
/// 把 Minecraft、Loader、Mod 与实例约束合并成有效 Java 要求。
/// 每条要求保留来源和证据质量；未知要求与不兼容要求必须区分。
/// </summary>
public static class JavaRequirementAggregation
{
    public static EffectiveJavaRequirements Resolve(
        GameInstance instance,
        MinecraftVersion? version,
        IEnumerable<JavaRequirementSource>? additionalSources = null)
    {
        var sources = new List<JavaRequirementSource>();
        var gameVersion = instance.GameVersion;
        var inferred = JavaRequirements.TryInferMinimumMajor(gameVersion);
        var declared = version?.JavaVersion?.MajorVersion ?? 0;
        var component = version?.JavaVersion?.Component;
        var isVanilla = string.IsNullOrWhiteSpace(instance.Loader) ||
                        string.Equals(instance.Loader, LoaderKinds.Vanilla, StringComparison.OrdinalIgnoreCase);

        if (declared > 0)
        {
            if (!isVanilla && inferred is { } baseMajor && declared > baseMajor)
            {
                // 合并后的版本 JSON 明确抬高了要求，通常是 Loader 覆盖了原版元数据。
                sources.Add(new JavaRequirementSource(
                    JavaRequirementKind.Minecraft,
                    $"minecraft:{gameVersion}",
                    JavaRequirementEvidence.InferredFromGameVersion,
                    $"Minecraft {gameVersion} 的基础要求",
                    baseMajor));
                sources.Add(new JavaRequirementSource(
                    JavaRequirementKind.Loader,
                    $"loader:{instance.Loader}:{instance.LoaderVersion ?? version?.Id ?? gameVersion}",
                    JavaRequirementEvidence.LoaderMetadata,
                    $"{instance.Loader} 元数据声明的运行时要求",
                    declared,
                    Component: component));
            }
            else
            {
                sources.Add(new JavaRequirementSource(
                    isVanilla ? JavaRequirementKind.Minecraft : JavaRequirementKind.Loader,
                    isVanilla
                        ? $"minecraft:{version?.Id ?? gameVersion}"
                        : $"loader:{instance.Loader}:{instance.LoaderVersion ?? version?.Id ?? gameVersion}",
                    JavaRequirementEvidence.DeclaredByVersionMetadata,
                    $"{(isVanilla ? "Minecraft" : instance.Loader)} 版本元数据声明的 Java 要求",
                    declared,
                    Component: component));
            }
        }
        else if (inferred is { } inferredMajor)
        {
            sources.Add(new JavaRequirementSource(
                JavaRequirementKind.Minecraft,
                $"minecraft:{gameVersion}",
                JavaRequirementEvidence.InferredFromGameVersion,
                $"根据 Minecraft {gameVersion} 推断的 Java 要求",
                inferredMajor,
                Component: component));
        }
        else
        {
            sources.Add(new JavaRequirementSource(
                JavaRequirementKind.Unknown,
                $"minecraft:{gameVersion}",
                JavaRequirementEvidence.Unknown,
                $"无法从版本元数据或版本号确定 Minecraft {gameVersion} 的 Java 要求"));
        }

        if (additionalSources is not null)
        {
            sources.AddRange(additionalSources.Where(source => source is not null));
        }

        return EffectiveJavaRequirements.Merge(sources);
    }
}
