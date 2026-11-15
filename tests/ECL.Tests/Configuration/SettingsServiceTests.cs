// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Configuration;

public sealed class SettingsServiceTests
{
    [Fact]
    public void UsesDeclaredDefaults()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();

        Assert.Equal(4096, settings.Get<int>("java.defaultMemoryMb"));
        Assert.Equal("g1", settings.Get<string>("java.jvmPreset"));
        Assert.Equal(8, settings.Get<int>("network.maxConcurrentDownloads"));
        Assert.Equal("official-first", settings.Get<string>("network.downloadSource"));
        // 界面语言默认“跟随系统”（settings-interface-preferences.md §5），
        // 启动时再按操作系统首选语言解析为 zh-CN / en-US。
        Assert.Equal("system", settings.Get<string>("launcher.language"));
        Assert.Equal(100, settings.Get<int>("launcher.uiScale"));
        Assert.Equal("standard", settings.Get<string>("launcher.fontSize"));
        Assert.True(settings.Get<bool>("launcher.animationsEnabled"));
        Assert.Equal(SettingsValueSource.Default, settings.GetSource("java.defaultMemoryMb"));
    }

    [Theory]
    [InlineData(null, "en-US")]
    [InlineData("", "en-US")]
    [InlineData("system", "en-US")]
    [InlineData("SYSTEM", "en-US")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("fr-FR", "fr-FR")]
    public void ResolveLanguageFollowsTheSystemOnlyWhenUnsetOrSystem(string? configured, string expected)
    {
        // 未设置 / 空 / system 都表示“跟随系统首选语言”；显式保存的区域性原样返回，
        // 支不支持由本地化器决定（不支持的名称会回退到 ECL 默认语言）。
        Assert.Equal(
            expected,
            SettingsBootstrap.ResolveLanguage(configured, CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void GlobalValuesPersistAndReportSource()
    {
        using var environment = new TestEnvironment();

        using (var provider = environment.CreateServices())
        {
            var settings = provider.GetRequiredService<ISettingsService>();
            Assert.True(settings.TrySetRaw("java.defaultMemoryMb", "8192", null, out var error), error);
            Assert.Equal(8192, settings.Get<int>("java.defaultMemoryMb"));
            Assert.Equal(SettingsValueSource.Global, settings.GetSource("java.defaultMemoryMb"));
        }

        using var secondProvider = environment.CreateServices();
        var reloaded = secondProvider.GetRequiredService<ISettingsService>();
        Assert.Equal(8192, reloaded.Get<int>("java.defaultMemoryMb"));
    }

    [Fact]
    public void RejectsInvalidValues()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();

        Assert.False(settings.TrySetRaw("java.defaultMemoryMb", "not-a-number", null, out var parseError));
        Assert.False(string.IsNullOrWhiteSpace(parseError));

        Assert.False(settings.TrySetRaw("java.defaultMemoryMb", "512", null, out var rangeError));
        Assert.False(string.IsNullOrWhiteSpace(rangeError));

        Assert.False(settings.TrySetRaw("network.proxyMode", "bogus", null, out var choiceError));
        Assert.False(string.IsNullOrWhiteSpace(choiceError));

        // 界面偏好（settings-interface-preferences.md §3–§6）：缩放范围/步进、字体族与字号档位
        // 都由元数据校验，CLI 也不能写入 83%、未知字体或未登记的档位。
        Assert.False(settings.TrySetRaw("launcher.uiScale", "83", null, out _));
        Assert.False(settings.TrySetRaw("launcher.uiScale", "155", null, out _));
        Assert.True(settings.TrySetRaw("launcher.uiScale", "80", null, out var scaleError), scaleError);
        Assert.True(settings.TrySetRaw("launcher.uiScale", "150", null, out scaleError), scaleError);
        Assert.False(settings.TrySetRaw("launcher.interfaceFont", "comic-sans", null, out _));
        Assert.False(settings.TrySetRaw("launcher.fontSize", "tiny", null, out _));
        Assert.False(settings.TrySetRaw("launcher.animationsEnabled", "maybe", null, out _));
        Assert.True(settings.TrySetRaw("launcher.language", "system", null, out var languageError), languageError);
    }

    [Fact]
    public void EnforcesOptionScopes()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();

        Assert.False(settings.TrySetRaw("java.defaultMemoryMb", "2048", "instance-a", out var globalError));
        Assert.False(string.IsNullOrWhiteSpace(globalError));

        Assert.False(settings.TrySetRaw("instance.java.memoryMb", "2048", null, out var instanceError));
        Assert.False(string.IsNullOrWhiteSpace(instanceError));
    }

    [Fact]
    public void InstanceOverridesAreIsolatedAndCanBeCleared()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();

        Assert.True(settings.TrySetRaw("instance.java.memoryMb", "8192", "instance-a", out var error), error);
        Assert.True(settings.TrySetRaw("instance.java.memoryMb", "6144", "instance-b", out var secondError), secondError);

        Assert.Equal(8192, settings.Get<int?>("instance.java.memoryMb", "instance-a"));
        Assert.Equal(6144, settings.Get<int?>("instance.java.memoryMb", "instance-b"));
        Assert.Null(settings.Get<int?>("instance.java.memoryMb", "instance-c"));
        Assert.Equal(SettingsValueSource.Instance, settings.GetSource("instance.java.memoryMb", "instance-a"));
        Assert.Equal(SettingsValueSource.Default, settings.GetSource("instance.java.memoryMb", "instance-c"));

        Assert.Null(settings.Get<string?>("instance.java.jvmPreset", "instance-a"));
        Assert.True(settings.TrySetRaw("instance.java.jvmPreset", "zgc", "instance-a", out var presetError), presetError);
        Assert.Equal("zgc", settings.Get<string?>("instance.java.jvmPreset", "instance-a"));
        Assert.True(settings.TrySetRaw("instance.java.jvmPreset", string.Empty, "instance-a", out var clearPresetError), clearPresetError);
        Assert.Null(settings.Get<string?>("instance.java.jvmPreset", "instance-a"));

        Assert.True(settings.TrySetRaw("instance.java.memoryMb", string.Empty, "instance-a", out var clearError), clearError);
        Assert.Null(settings.Get<int?>("instance.java.memoryMb", "instance-a"));
        Assert.Equal(SettingsValueSource.Default, settings.GetSource("instance.java.memoryMb", "instance-a"));
    }

    [Fact]
    public void VisibilityFollowsDependencies()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();
        var definition = settings.GetDefinition("launcher.updateChannel");

        Assert.True(settings.IsVisible(definition));
        Assert.True(settings.TrySetRaw("launcher.checkUpdates", "false", null, out var error), error);
        Assert.False(settings.IsVisible(definition));
    }

    [Fact]
    public void InternalAcknowledgementIsHiddenAndDeprecatedCompatibilitySwitchIsGone()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();
        var settings = provider.GetRequiredService<ISettingsService>();

        Assert.False(settings.IsVisible(settings.GetDefinition("launcher.java.incompatiblePromptAcknowledged")));
        Assert.DoesNotContain(
            settings.GetDefinitions(),
            definition => definition.Key == "instance.java.disableCompatibilityCheck");
    }

    [Fact]
    public void BootstrapReadsLanguageBeforeContainerIsBuilt()
    {
        using var environment = new TestEnvironment();
        using (var provider = environment.CreateServices())
        {
            var settings = provider.GetRequiredService<ISettingsService>();
            Assert.True(settings.TrySetRaw("launcher.language", "en-US", null, out var error), error);
        }

        Assert.Equal("en-US", SettingsBootstrap.ReadGlobalString(environment.Paths, "launcher.language"));
    }
}
