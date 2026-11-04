// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Java;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class EffectiveJavaRequirementsTests
{
    [Fact]
    public void MergesHighestMinimumAndKeepsEverySource()
    {
        var requirements = EffectiveJavaRequirements.Merge(
        [
            Source(JavaRequirementKind.Minecraft, "minecraft:1.20.1", 17),
            Source(JavaRequirementKind.Mod, "mod:example.jar", 21),
        ]);

        Assert.Equal(21, requirements.MinimumMajor);
        Assert.False(requirements.HasConflicts);
        Assert.Empty(requirements.Conflicts);
        Assert.Equal(2, requirements.Sources.Count);
        Assert.Contains("Java 21+", requirements.Describe(), StringComparison.Ordinal);
        Assert.Contains("mod:example.jar", requirements.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void DetectsConflictInsteadOfChoosingTheHighestVersion()
    {
        var requirements = EffectiveJavaRequirements.Merge(
        [
            new JavaRequirementSource(
                JavaRequirementKind.Mod,
                "mod:needs-25",
                JavaRequirementEvidence.ModClassFileLowerBound,
                "需要 Java 25",
                MinimumMajor: 25),
            new JavaRequirementSource(
                JavaRequirementKind.Instance,
                "instance:max-21",
                JavaRequirementEvidence.ExplicitInstanceConstraint,
                "最多 Java 21",
                MaximumMajor: 21),
        ]);

        Assert.True(requirements.HasConflicts);
        Assert.True(requirements.EvaluateStatus(Runtime("25.0.1", "x86_64")) ==
                    JavaCompatibilityStatus.RequirementsConflict);
        Assert.Contains(requirements.Conflicts, conflict => conflict.Sources.Count >= 2);
    }

    [Fact]
    public void UnknownRequirementIsNotIncompatibility()
    {
        var requirements = EffectiveJavaRequirements.Merge(
        [
            new JavaRequirementSource(
                JavaRequirementKind.Unknown,
                "minecraft:custom",
                JavaRequirementEvidence.Unknown,
                "无法确定要求"),
        ]);

        Assert.True(requirements.IsUnknown);
        Assert.Equal(
            JavaCompatibilityStatus.UnknownRequirement,
            requirements.EvaluateStatus(Runtime("17.0.15", "x86_64")));
        Assert.False(requirements.IsSatisfiedBy(Runtime("17.0.15", "x86_64")));
    }

    [Fact]
    public void FingerprintChangesWhenEffectiveRequirementChanges()
    {
        var first = EffectiveJavaRequirements.Merge([Source(JavaRequirementKind.Minecraft, "mc", 17)]);
        var second = EffectiveJavaRequirements.Merge([Source(JavaRequirementKind.Minecraft, "mc", 21)]);
        var same = EffectiveJavaRequirements.Merge([Source(JavaRequirementKind.Minecraft, "mc", 17)]);

        Assert.Equal(first.Fingerprint(), same.Fingerprint());
        Assert.NotEqual(first.Fingerprint(), second.Fingerprint());
    }

    [Fact]
    public void ComponentOnlyComesFromTheSourceThatSetsTheEffectiveMinimum()
    {
        var requirements = EffectiveJavaRequirements.Merge(
        [
            Source(JavaRequirementKind.Minecraft, "minecraft:1.20.1", 17, component: "java-runtime-gamma"),
            Source(JavaRequirementKind.Mod, "mod:example.jar", 25),
        ]);

        Assert.Equal(25, requirements.MinimumMajor);
        Assert.Null(requirements.Component);
    }

    [Fact]
    public void RejectsKnown32BitRuntime()
    {
        var requirements = EffectiveJavaRequirements.Merge([Source(JavaRequirementKind.Minecraft, "mc", 17)]);

        Assert.Equal(
            JavaCompatibilityStatus.UnsupportedArchitecture,
            requirements.EvaluateStatus(Runtime("17.0.15", "x86")));
    }

    [Fact]
    public void LegacyAcknowledgementHelperUsesTheSameEffectiveRiskIdentity()
    {
        var runtime = Runtime("8.0.412", "x86_64");
        var requirements = EffectiveJavaRequirements.Merge(
        [
            Source(JavaRequirementKind.Minecraft, "minecraft:1.20.1", 17),
        ]);

        Assert.Equal(
            JavaCompatibilityAcknowledgement.Create(runtime, new JavaRequirement(17)),
            JavaRiskIdentity.Create(runtime, requirements));
    }

    private static JavaRequirementSource Source(
        JavaRequirementKind kind,
        string id,
        int minimum,
        string? component = null) =>
        new(kind, id, JavaRequirementEvidence.DeclaredByVersionMetadata, $"{id} 要求 Java {minimum}", minimum, Component: component);

    private static JavaRuntime Runtime(string version, string architecture) =>
        new(
            Path.Combine(Path.GetTempPath(), $"java-{version}-{architecture}"),
            Path.Combine(Path.GetTempPath(), $"java-{version}-{architecture}", "bin", "java"),
            "Test",
            architecture,
            JavaVersion.TryParse(version));
}
