// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Workspace.ViewModels;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Java;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// Java 方块呈现（B2 步骤 3）：颜色/符号必须映射领域层的真实兼容性结论；
/// 「已忽略警告」只改变呈现，不把不兼容伪装成兼容。
/// </summary>
public sealed class JavaStatusPresentationTests
{
    private static readonly JavaRequirement Requirement = new(21);
    private static readonly JavaVersion Version = new(21, 0, 2, "21.0.2");

    private static ILocalizer Localizer() => new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    private static JavaRuntime Runtime(JavaVersion? version = null) => RuntimeWith(version ?? Version);

    private static JavaRuntime RuntimeWithoutVersion() => RuntimeWith(null);

    private static JavaRuntime RuntimeWith(JavaVersion? version) => new(
        @"C:\Java\jdk-21",
        @"C:\Java\jdk-21\bin\java.exe",
        "Oracle",
        "x64",
        version);

    private static JavaSelectionResult Selection(
        JavaRuntime? runtime,
        JavaCompatibilityStatus? status,
        bool isBlocked = false,
        JavaRequirement? requirement = null) => new(
        runtime,
        requirement ?? Requirement,
        CompatibilityCheckEnabled: true,
        status is null ? null : new JavaCompatibilityResult(status.Value, requirement ?? Requirement, runtime?.Version),
        HasAnyDetectedJava: runtime is not null,
        ExplicitPath: null,
        IsExplicitOverride: runtime is not null,
        Warnings: [],
        MemoryMb: 4096,
        IsBlocked: isBlocked);

    [Fact]
    public void CompatibleRuntimeShowsPurpleState()
    {
        var tile = JavaStatusPresentation.Describe(
            Selection(Runtime(), JavaCompatibilityStatus.Compatible),
            suppressCompatibilityWarnings: false,
            Localizer());

        Assert.Equal(JavaCompatibilityBadge.Compatible, tile.Badge);
        Assert.Equal("Java 21", tile.Title);
        Assert.Equal("Oracle 21.0.2", tile.Detail);
        Assert.Equal("符合此实例的要求", tile.StateText);
    }

    [Fact]
    public void IncompatibleRuntimeShowsRedStateWithRequirement()
    {
        var tile = JavaStatusPresentation.Describe(
            Selection(Runtime(new JavaVersion(17, 0, 1, "17.0.1")), JavaCompatibilityStatus.TooOld),
            suppressCompatibilityWarnings: false,
            Localizer());

        Assert.Equal(JavaCompatibilityBadge.Incompatible, tile.Badge);
        Assert.Contains("需要 Java 21+", tile.StateText);
        Assert.DoesNotContain("已忽略", tile.StateText);
    }

    [Fact]
    public void SuppressedWarningsTurnOnlyThePresentationYellow()
    {
        var tile = JavaStatusPresentation.Describe(
            Selection(Runtime(new JavaVersion(17, 0, 1, "17.0.1")), JavaCompatibilityStatus.TooOld),
            suppressCompatibilityWarnings: true,
            Localizer());

        Assert.Equal(JavaCompatibilityBadge.IncompatibleAcknowledged, tile.Badge);
        Assert.Contains("已忽略常规警告", tile.StateText);
    }

    [Theory]
    [InlineData(JavaCompatibilityStatus.Unknown)]
    [InlineData(JavaCompatibilityStatus.UnknownRequirement)]
    public void UnknownCompatibilityIsNeverShownAsCompatible(JavaCompatibilityStatus status)
    {
        var tile = JavaStatusPresentation.Describe(
            Selection(Runtime(), status),
            suppressCompatibilityWarnings: false,
            Localizer());

        Assert.Equal(JavaCompatibilityBadge.Unknown, tile.Badge);
        Assert.Equal("兼容性尚未确定", tile.StateText);
        Assert.NotEqual(JavaCompatibilityBadge.Compatible, tile.Badge);
    }

    [Fact]
    public void MissingRuntimeHasItsOwnState()
    {
        var tile = JavaStatusPresentation.Describe(
            Selection(runtime: null, status: null),
            suppressCompatibilityWarnings: false,
            Localizer());

        Assert.Equal(JavaCompatibilityBadge.Unknown, tile.Badge);
        Assert.Equal("未检测到 Java", tile.Title);
        Assert.NotEmpty(tile.Detail);
        Assert.NotEqual(JavaCompatibilityBadge.Compatible, tile.Badge);
    }

    [Fact]
    public void BlockedSelectionIsAlwaysIncompatible()
    {
        var tile = JavaStatusPresentation.Describe(
            Selection(Runtime(), JavaCompatibilityStatus.UnsupportedArchitecture, isBlocked: true),
            suppressCompatibilityWarnings: false,
            Localizer());

        Assert.Equal(JavaCompatibilityBadge.Incompatible, tile.Badge);
    }

    [Fact]
    public void RuntimeWithoutVersionFallsBackToDisplayName()
    {
        var runtime = RuntimeWithoutVersion();

        var tile = JavaStatusPresentation.Describe(
            Selection(runtime, status: null),
            suppressCompatibilityWarnings: false,
            Localizer());

        Assert.Equal(runtime.DisplayName, tile.Title);
        Assert.Equal(runtime.DisplayName, tile.Detail);
        Assert.Equal(JavaCompatibilityBadge.Unknown, tile.Badge);
    }
}
