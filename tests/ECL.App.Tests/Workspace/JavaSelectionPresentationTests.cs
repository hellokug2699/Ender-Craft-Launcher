// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IO;
using Ecl.App.ViewModels;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Java;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// Java 选择结果的呈现规则只有一份实现：主页启动面板与实例工作区必须给出同一套文案，
/// 判断顺序（未知 / 兼容 / 不兼容）不得在页面里重复实现。
/// </summary>
public sealed class JavaSelectionPresentationTests
{
    private static readonly ILocalizer Localizer =
        new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN"));

    private static JavaSelectionResult Selection(
        JavaRuntime? runtime,
        bool acknowledged = false,
        IReadOnlyList<string>? warnings = null) =>
        new(
            Runtime: runtime,
            Requirement: null,
            CompatibilityCheckEnabled: true,
            Compatibility: null,
            HasAnyDetectedJava: runtime is not null,
            ExplicitPath: runtime?.ExecutablePath,
            IsExplicitOverride: runtime is not null,
            Warnings: warnings ?? [],
            MemoryMb: 4096,
            IsCompatibilityWarningAcknowledged: acknowledged);

    private static JavaRuntime Runtime(string folder, string vendor = "Oracle") =>
        new(
            HomePath: Path.Combine(@"C:\Java", folder),
            ExecutablePath: Path.Combine(@"C:\Java", folder, "bin", "java.exe"),
            Vendor: vendor,
            Architecture: "x64",
            Version: null);

    [Fact]
    public void MissingRuntimeUsesNotFoundText()
    {
        var text = JavaSelectionPresentation.Describe(Selection(null), Localizer);

        Assert.Equal(Localizer.Get("instances.java.notFound"), text);
    }

    [Fact]
    public void CompatibleRuntimeUsesCompatibleText()
    {
        var runtime = Runtime("jdk-21");

        var text = JavaSelectionPresentation.Describe(Selection(runtime), Localizer);

        Assert.Equal(Localizer.Get("instances.java.compatible", runtime.DisplayName), text);
    }

    [Fact]
    public void WarningsAreAppendedToTheSameSentence()
    {
        var runtime = Runtime("jdk-21");

        var text = JavaSelectionPresentation.Describe(
            Selection(runtime, warnings: ["警告一", "警告二"]),
            Localizer);

        Assert.StartsWith(Localizer.Get("instances.java.compatible", runtime.DisplayName), text);
        Assert.EndsWith("警告一 警告二", text);
    }

    [Fact]
    public void RuntimeLabelKeepsVersionVendorAndFolder()
    {
        var label = JavaSelectionPresentation.FormatRuntime(Runtime("jdk-21"));

        Assert.Equal("? · Oracle · jdk-21", label);
    }
}
