// SPDX-License-Identifier: GPL-3.0-or-later

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
        Assert.Contains(settings.Get<string>("launcher.language"), new[] { "zh-CN", "en-US" });
        Assert.Equal(SettingsValueSource.Default, settings.GetSource("java.defaultMemoryMb"));
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
