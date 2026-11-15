// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Mods;
using Ecl.Minecraft.Schematics;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 投影支持检测（schematics.md §4）：只按真实组件元数据判断，支持/缺失/不完整/
/// 不兼容/冲突/未知都不得被猜测成“支持”。
/// </summary>
public sealed class SchematicSupportDetectorTests
{
    private static readonly GameInstance FabricInstance = new()
    {
        Id = "instance-1",
        Name = "Test",
        GameVersion = "1.20.1",
        VersionId = "1.20.1",
        Loader = "fabric",
        LoaderVersion = "0.15.0",
    };

    [Fact]
    public void SupportsLitematicaWhenArtifactAndDependenciesMatch()
    {
        var result = SchematicSupportDetector.Evaluate(
            FabricInstance,
            [
                Mod("litematica", "0.19.0", ModPlatform.Fabric, ">=1.20", ("malilib", "0.17.0")),
                Mod("malilib", "0.17.0", ModPlatform.Fabric, ">=1.20"),
            ]);

        Assert.Equal(SchematicSupportState.Supported, result.State);
        Assert.Equal(SchematicSupportState.Supported, Format(result, SchematicFormat.Litematica).State);
        Assert.Equal(SchematicSupportState.Missing, Format(result, SchematicFormat.SpongeSchematic).State);
        Assert.Contains("litematica", result.Providers);
    }

    [Fact]
    public void ReportsIncompleteWhenMandatoryDependencyIsMissing()
    {
        var result = SchematicSupportDetector.Evaluate(
            FabricInstance,
            [Mod("litematica", "0.19.0", ModPlatform.Fabric, ">=1.20", ("malilib", "0.17.0"))]);

        var format = Format(result, SchematicFormat.Litematica);
        Assert.Equal(SchematicSupportState.Incomplete, format.State);
        Assert.Equal("malilib", format.Detail);
        Assert.Equal(SchematicSupportState.Incomplete, result.State);
    }

    [Fact]
    public void ReportsIncompatibleWhenArtifactDeclaresAnotherLoader()
    {
        var result = SchematicSupportDetector.Evaluate(
            FabricInstance,
            [Mod("litematica", "0.19.0", ModPlatform.Forge, ">=1.20")]);

        Assert.Equal(SchematicSupportState.Incompatible, Format(result, SchematicFormat.Litematica).State);
        Assert.Equal(SchematicSupportState.Incompatible, result.State);
    }

    [Fact]
    public void ReportsIncompatibleWhenArtifactDeclaresAnotherMinecraftVersion()
    {
        var result = SchematicSupportDetector.Evaluate(
            FabricInstance,
            [Mod("worldedit", "7.3.0", ModPlatform.Fabric, ">=1.21")]);

        Assert.Equal(SchematicSupportState.Incompatible, Format(result, SchematicFormat.SpongeSchematic).State);
    }

    [Fact]
    public void ReportsUnknownWhenArtifactHasNoVerifiableMinecraftRange()
    {
        var result = SchematicSupportDetector.Evaluate(
            FabricInstance,
            [Mod("worldedit", "7.3.0", ModPlatform.Fabric, minecraftRange: null)]);

        Assert.Equal(SchematicSupportState.Unknown, Format(result, SchematicFormat.SpongeSchematic).State);
        Assert.Equal("artifact-unverified", Format(result, SchematicFormat.SpongeSchematic).Detail);
    }

    [Fact]
    public void ReportsMissingWhenNoKnownProviderIsInstalled()
    {
        var result = SchematicSupportDetector.Evaluate(FabricInstance, []);

        Assert.Equal(SchematicSupportState.Missing, result.State);
        Assert.All(result.Formats, format => Assert.Equal(SchematicSupportState.Missing, format.State));
        Assert.Empty(result.Providers);
    }

    [Fact]
    public void IgnoresDisabledProviders()
    {
        var result = SchematicSupportDetector.Evaluate(
            FabricInstance,
            [Mod("litematica", "0.19.0", ModPlatform.Fabric, ">=1.20", enabled: false)]);

        Assert.Equal(SchematicSupportState.Missing, result.State);
    }

    [Fact]
    public void MapsWorldEditToSpongeAndMceFormatSupport()
    {
        var result = SchematicSupportDetector.Evaluate(
            FabricInstance,
            [Mod("worldedit", "7.3.0", ModPlatform.Fabric, ">=1.19")]);

        Assert.Equal(SchematicSupportState.Supported, Format(result, SchematicFormat.SpongeSchematic).State);
        Assert.Equal(SchematicSupportState.Supported, Format(result, SchematicFormat.MceSchematic).State);
        Assert.Equal(SchematicSupportState.Missing, Format(result, SchematicFormat.Litematica).State);
        Assert.Equal(SchematicSupportState.Supported, result.State);
    }

    [Fact]
    public void ReportsConflictWhenTwoCompatibleProvidersClaimTheSameFormat()
    {
        var result = SchematicSupportDetector.Evaluate(
            FabricInstance,
            [
                Mod("litematica", "0.19.0", ModPlatform.Fabric, ">=1.20"),
                Mod("forgematica", "1.0.0", ModPlatform.Fabric, ">=1.20"),
            ]);

        var format = Format(result, SchematicFormat.Litematica);
        Assert.Equal(SchematicSupportState.Conflict, format.State);
        Assert.Equal(["forgematica", "litematica"], format.Providers.Order());
    }

    [Fact]
    public void AxiomRequiresItsDeclaredDependency()
    {
        var axiom = Mod("axiom", "1.0.0", ModPlatform.Fabric, ">=1.20", ("fabric-api", "0.90.0"));
        var incomplete = SchematicSupportDetector.Evaluate(FabricInstance, [axiom]);
        Assert.Equal(SchematicSupportState.Incomplete, Format(incomplete, SchematicFormat.AxiomBlueprint).State);

        var supported = SchematicSupportDetector.Evaluate(
            FabricInstance,
            [axiom, Mod("fabric-api", "0.92.0", ModPlatform.Fabric, ">=1.20")]);
        Assert.Equal(SchematicSupportState.Supported, Format(supported, SchematicFormat.AxiomBlueprint).State);
    }

    private static SchematicFormatSupport Format(SchematicSupportResult result, SchematicFormat format) =>
        result.Formats.Single(item => item.Format == format);

    private static ModEntry Mod(
        string modId,
        string version,
        ModPlatform platform,
        string? minecraftRange,
        (string Id, string Version) dependency = default,
        bool enabled = true)
    {
        var dependencies = new List<ModDependency>();
        if (minecraftRange is not null)
        {
            dependencies.Add(new ModDependency("minecraft", true, minecraftRange));
        }

        if (dependency.Id is not null)
        {
            dependencies.Add(new ModDependency(dependency.Id, true, $">={dependency.Version}"));
        }

        var metadata = new ModMetadata(
            modId,
            modId,
            version,
            null,
            platform,
            dependencies,
            []);
        return new ModEntry($"{modId}.jar", $"/mods/{modId}.jar", enabled, metadata, null, []);
    }
}
