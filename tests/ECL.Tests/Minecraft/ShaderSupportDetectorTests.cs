// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 光影支持检测（shaders.md §2、Public docs/shader-support/rules.json）：
/// 只看实例版本/加载器与已启用模组的真实元数据；未知一律返回 unknown，不猜测支持。
/// </summary>
public sealed class ShaderSupportDetectorTests
{
    [Fact]
    public void NoProviderIsMissing()
    {
        var result = ShaderSupportDetector.Evaluate(Instance("1.21.1", "fabric"), []);

        Assert.Equal(ShaderSupportState.Missing, result.State);
        Assert.Empty(result.Providers);
    }

    [Fact]
    public void IrisOnFabricWithMatchingArtifactIsSupported()
    {
        var result = ShaderSupportDetector.Evaluate(
            Instance("1.21.1", "fabric"),
            [Mod("iris-1.7.6.jar", "iris", ModPlatform.Fabric, ">=1.21 <1.22")]);

        Assert.Equal(ShaderSupportState.Supported, result.State);
        Assert.Equal([ShaderSupportDetector.IrisModId], result.Providers);
    }

    [Fact]
    public void IrisOnNeoForgeRequiresTheSupportedVersionFloor()
    {
        var supported = ShaderSupportDetector.Evaluate(
            Instance("1.21.1", "neoforge"),
            [Mod("iris-1.8.0.jar", "iris", ModPlatform.NeoForge, ">=1.21.1")]);
        Assert.Equal(ShaderSupportState.Supported, supported.State);

        var tooOld = ShaderSupportDetector.Evaluate(
            Instance("1.20.6", "neoforge"),
            [Mod("iris-1.7.0.jar", "iris", ModPlatform.NeoForge, ">=1.20.6")]);
        Assert.Equal(ShaderSupportState.Incompatible, tooOld.State);
        Assert.Equal("iris-version-unsupported", tooOld.Detail);
    }

    [Fact]
    public void IrisOnForgeIsIncompatible()
    {
        var result = ShaderSupportDetector.Evaluate(
            Instance("1.20.1", "forge"),
            [Mod("iris-1.6.jar", "iris", ModPlatform.Forge, ">=1.20.1")]);

        Assert.Equal(ShaderSupportState.Incompatible, result.State);
        Assert.Equal("iris-forge-unsupported", result.Detail);
    }

    [Fact]
    public void MissingOrUnparseableArtifactMetadataIsUnknownNotSupported()
    {
        var noRange = ShaderSupportDetector.Evaluate(
            Instance("1.21.1", "fabric"),
            [Mod("iris.jar", "iris", ModPlatform.Fabric, minecraftRange: null)]);
        Assert.Equal(ShaderSupportState.Unknown, noRange.State);
        Assert.Equal("iris-artifact-unverified", noRange.Detail);

        var wrongLoader = ShaderSupportDetector.Evaluate(
            Instance("1.21.1", "quilt"),
            [Mod("iris.jar", "iris", ModPlatform.Fabric, ">=1.21")]);
        Assert.Equal(ShaderSupportState.Unknown, wrongLoader.State);
    }

    [Fact]
    public void OculusNeedsARequiredDependencyThatTheArtifactDeclares()
    {
        var incomplete = ShaderSupportDetector.Evaluate(
            Instance("1.20.1", "forge"),
            [Mod("oculus.jar", "oculus", ModPlatform.Forge, "[1.20.1]", mandatoryDependencies: ["embeddium"])]);
        Assert.Equal(ShaderSupportState.Incomplete, incomplete.State);
        Assert.Equal("embeddium", incomplete.Detail);

        var supported = ShaderSupportDetector.Evaluate(
            Instance("1.20.1", "forge"),
            [
                Mod("oculus.jar", "oculus", ModPlatform.Forge, "[1.20.1]", mandatoryDependencies: ["embeddium"]),
                Mod("embeddium.jar", "embeddium", ModPlatform.Forge, "[1.20.1]"),
            ]);
        Assert.Equal(ShaderSupportState.Supported, supported.State);

        // 依赖版本不满足声明范围也算不完整。
        var mismatched = ShaderSupportDetector.Evaluate(
            Instance("1.20.1", "forge"),
            [
                Mod("oculus.jar", "oculus", ModPlatform.Forge, "[1.20.1]", mandatoryDependencies: ["embeddium"], dependencyRange: ">=0.5"),
                Mod("embeddium.jar", "embeddium", ModPlatform.Forge, "[1.20.1]", version: "0.4.0"),
            ]);
        Assert.Equal(ShaderSupportState.Incomplete, mismatched.State);
        Assert.Equal("embeddium", mismatched.Detail);
    }

    [Fact]
    public void OculusOutsideItsDiscreteVersionSetIsIncompatible()
    {
        var result = ShaderSupportDetector.Evaluate(
            Instance("1.20.2", "forge"),
            [Mod("oculus.jar", "oculus", ModPlatform.Forge, "[1.20.2]")]);

        Assert.Equal(ShaderSupportState.Incompatible, result.State);
        Assert.Equal("oculus-version-unsupported", result.Detail);
    }

    [Fact]
    public void OptiFineAloneCannotBeProvenAndStaysUnknown()
    {
        var result = ShaderSupportDetector.Evaluate(
            Instance("1.20.1", "forge", optiFineVersion: "HD_U:I6"),
            []);

        Assert.Equal(ShaderSupportState.Unknown, result.State);
        Assert.Equal([ShaderSupportDetector.OptiFineProvider], result.Providers);
    }

    [Fact]
    public void ConflictingProvidersAreReportedAsConflict()
    {
        var result = ShaderSupportDetector.Evaluate(
            Instance("1.20.1", "forge", optiFineVersion: "HD_U:I6"),
            [Mod("iris.jar", "iris", ModPlatform.Forge, "[1.20.1]")]);

        Assert.Equal(ShaderSupportState.Conflict, result.State);
        Assert.Contains(ShaderSupportDetector.IrisModId, result.Providers);
        Assert.Contains(ShaderSupportDetector.OptiFineProvider, result.Providers);
    }

    [Fact]
    public void DisabledProviderIsNotCounted()
    {
        var result = ShaderSupportDetector.Evaluate(
            Instance("1.21.1", "fabric"),
            [Mod("iris.jar", "iris", ModPlatform.Fabric, ">=1.21", enabled: false)]);

        Assert.Equal(ShaderSupportState.Missing, result.State);
    }

    private static GameInstance Instance(
        string gameVersion,
        string loader,
        string? optiFineVersion = null) =>
        new()
        {
            Id = "instance-a",
            Name = "Instance A",
            GameVersion = gameVersion,
            VersionId = gameVersion,
            Loader = loader,
            LoaderVersion = loader == "vanilla" ? null : "1.0.0",
            OptiFineVersion = optiFineVersion,
        };

    private static ModEntry Mod(
        string fileName,
        string modId,
        ModPlatform platform,
        string? minecraftRange,
        IReadOnlyList<string>? mandatoryDependencies = null,
        bool enabled = true,
        string version = "1.0.0",
        string? dependencyRange = null)
    {
        var dependencies = new List<ModDependency>();
        if (minecraftRange is not null)
        {
            dependencies.Add(new ModDependency("minecraft", Mandatory: true, minecraftRange));
        }

        foreach (var id in mandatoryDependencies ?? [])
        {
            dependencies.Add(new ModDependency(id, Mandatory: true, dependencyRange));
        }

        return new ModEntry(
            fileName,
            $@"C:\instance\mods\{fileName}",
            enabled,
            new ModMetadata(modId, modId, version, null, platform, dependencies, []),
            null,
            []);
    }
}
