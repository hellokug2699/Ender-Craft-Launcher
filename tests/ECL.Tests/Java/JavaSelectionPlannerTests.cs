// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Java;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaSelectionPlannerTests
{
    [Fact]
    public void SelectsFirstCompatibleCandidateAndSkipsIncompatibleOnes()
    {
        var java8 = Runtime("8.0.412", "x86_64");
        var java21 = Runtime("21.0.7", "x86_64");
        var java17 = Runtime("17.0.15", "x86_64");

        var decision = JavaSelectionPlanner.Select(
            [java8, java21, java17],
            Requirements(17),
            configuredPriorityCount: 3);

        Assert.Same(java21, decision.Runtime);
        Assert.Equal(JavaSelectionReason.GlobalPriority, decision.Reason);
        Assert.Equal(JavaCandidateStatus.TooOld, decision.Candidates[0].Status);
        Assert.Equal(JavaCandidateStatus.Selected, decision.Candidates[1].Status);
        Assert.Contains(decision.RejectedCandidates, candidate => ReferenceEquals(candidate.Runtime, java8));
    }

    [Fact]
    public void AlwaysRejects32BitCandidatesEvenWhenTheyAreFirst()
    {
        var java17x86 = Runtime("17.0.15", "x86");
        var java17x64 = Runtime("17.0.15", "x86_64");

        var decision = JavaSelectionPlanner.Select(
            [java17x86, java17x64],
            Requirements(17));

        Assert.Same(java17x64, decision.Runtime);
        Assert.Equal(JavaCandidateStatus.Unsupported32Bit, decision.Candidates[0].Status);
    }

    [Fact]
    public void ExplicitOverrideIsNeverSilentlyReplacedAndCarriesRiskIdentity()
    {
        var java8 = Runtime("8.0.412", "x86_64");
        var java17 = Runtime("17.0.15", "x86_64");
        var requirements = Requirements(17);

        var decision = JavaSelectionPlanner.Select(
            [java17, java8],
            requirements,
            explicitRuntime: java8);

        Assert.Same(java8, decision.Runtime);
        Assert.True(decision.IsExplicitOverride);
        Assert.True(decision.RequiresRiskAcknowledgement);
        Assert.False(decision.IsBlocked);
        Assert.Equal(JavaCompatibilityStatus.TooOld, requirements.EvaluateStatus(java8));
        Assert.False(string.IsNullOrWhiteSpace(decision.RiskIdentity));
    }

    [Fact]
    public void Explicit32BitOverrideIsBlockedEvenWithRiskAcceptance()
    {
        var java17x86 = Runtime("17.0.15", "x86");

        var decision = JavaSelectionPlanner.Select(
            [],
            Requirements(17),
            explicitRuntime: java17x86);

        Assert.True(decision.IsBlocked);
        Assert.False(decision.RequiresRiskAcknowledgement);
        Assert.Equal(JavaSelectionFailure.ExplicitRuntimeUnsupported, decision.Failure);
        Assert.Same(java17x86, decision.Runtime);
        Assert.Contains("32", decision.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void RequirementConflictBlocksAllSelection()
    {
        var requirements = EffectiveJavaRequirements.Merge(
        [
            new JavaRequirementSource(
                JavaRequirementKind.Minecraft,
                "minecraft",
                JavaRequirementEvidence.DeclaredByVersionMetadata,
                "至少 Java 25",
                MinimumMajor: 25),
            new JavaRequirementSource(
                JavaRequirementKind.Instance,
                "instance",
                JavaRequirementEvidence.ExplicitInstanceConstraint,
                "最多 Java 21",
                MaximumMajor: 21),
        ]);

        var decision = JavaSelectionPlanner.Select(
            [Runtime("25.0.1", "x86_64")],
            requirements);

        Assert.True(decision.IsBlocked);
        Assert.Null(decision.Runtime);
        Assert.Equal(JavaSelectionFailure.RequirementsConflict, decision.Failure);
    }

    [Fact]
    public void UnknownRequirementSelectsByPriorityInsteadOfClaimingIncompatibility()
    {
        var java8 = Runtime("8.0.412", "x86_64");
        var java21 = Runtime("21.0.7", "x86_64");
        var requirements = EffectiveJavaRequirements.Merge(
        [
            new JavaRequirementSource(
                JavaRequirementKind.Unknown,
                "minecraft:custom",
                JavaRequirementEvidence.Unknown,
                "无法确定要求"),
        ]);

        var decision = JavaSelectionPlanner.Select([java8, java21], requirements);

        Assert.Same(java8, decision.Runtime);
        Assert.False(decision.IsBlocked);
    }

    [Fact]
    public void NoCompatibleCandidateReportsRejectedReasons()
    {
        var java8 = Runtime("8.0.412", "x86_64");
        var java17x86 = Runtime("17.0.15", "x86");

        var decision = JavaSelectionPlanner.Select([java17x86, java8], Requirements(21));

        Assert.Null(decision.Runtime);
        Assert.Equal(JavaSelectionFailure.NoCompatibleCandidate, decision.Failure);
        Assert.Equal(2, decision.RejectedCandidates.Count);
        Assert.Contains(decision.RejectedCandidates, candidate =>
            candidate.Status == JavaCandidateStatus.Unsupported32Bit);
        Assert.Contains(decision.RejectedCandidates, candidate =>
            candidate.Status == JavaCandidateStatus.TooOld);
    }

    [Fact]
    public void RiskIdentityChangesWithJavaPathVersionAndRequirements()
    {
        var java8 = Runtime("8.0.412", "x86_64");
        var java8OtherPath = java8 with { ExecutablePath = java8.ExecutablePath + ".other" };
        var java8Updated = java8 with { Version = JavaVersion.TryParse("8.0.999") };

        var token = JavaRiskIdentity.Create(java8, Requirements(17));
        Assert.Equal(token, JavaRiskIdentity.Create(java8, Requirements(17)));
        Assert.NotEqual(token, JavaRiskIdentity.Create(java8OtherPath, Requirements(17)));
        Assert.NotEqual(token, JavaRiskIdentity.Create(java8Updated, Requirements(17)));
        Assert.NotEqual(token, JavaRiskIdentity.Create(java8, Requirements(21)));
    }

    [Fact]
    public void PriorityOrderingKeepsParentDirectoryGroupAndConfiguredCount()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecl-java-priority", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var java8 = RuntimeAt(root, "jdk8", "8.0.412");
            var java17 = RuntimeAt(root, "jdk17", "17.0.15");
            var other = Runtime("21.0.7", "x86_64");

            var ordered = JavaPriorityOrdering.Order([other, java8, java17], [root]);

            Assert.Equal(2, ordered.ConfiguredCount);
            Assert.Same(java8, ordered.Runtimes[0]);
            Assert.Same(java17, ordered.Runtimes[1]);
            Assert.Same(other, ordered.Runtimes[2]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EffectiveJavaRequirements Requirements(int minimum) =>
        EffectiveJavaRequirements.Merge(
        [
            new JavaRequirementSource(
                JavaRequirementKind.Minecraft,
                "minecraft:test",
                JavaRequirementEvidence.DeclaredByVersionMetadata,
                $"要求 Java {minimum}",
                minimum),
        ]);

    private static JavaRuntime Runtime(string version, string architecture)
    {
        var home = Path.Combine(Path.GetTempPath(), $"java-{version}-{architecture}", Guid.NewGuid().ToString("N"));
        return new JavaRuntime(
            home,
            Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"),
            "Test",
            architecture,
            JavaVersion.TryParse(version));
    }

    private static JavaRuntime RuntimeAt(string root, string name, string version)
    {
        var home = Path.Combine(root, name);
        return new JavaRuntime(
            home,
            Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"),
            "Test",
            "x86_64",
            JavaVersion.TryParse(version));
    }
}
