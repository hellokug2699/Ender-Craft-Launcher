// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Versions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Java;

public sealed class JavaSelectionServiceTests
{
    [Fact]
    public async Task SelectsCompatibleRuntimeAndDetectsExplicitMismatch()
    {
        using var environment = new TestEnvironment();
        var java8 = Runtime(environment, "8.0.412");
        var java17 = Runtime(environment, "17.0.15");
        var fakeLocator = new FakeJavaLocator([java8, java17]);

        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IJavaLocator>(fakeLocator));

        var instance = await CreateVersionAsync(provider, environment, "1.20.1");
        var version = (await provider.GetRequiredService<IVersionResolver>().ResolveAsync(instance.VersionId)).Value!;
        var selection = provider.GetRequiredService<IJavaSelectionService>();
        var settings = provider.GetRequiredService<ISettingsService>();

        var automatic = await selection.ResolveAsync(instance, version);
        Assert.Equal(17, automatic.Runtime?.Version?.Major);
        Assert.True(automatic.Compatibility?.IsCompatible);
        Assert.True(automatic.CompatibilityCheckEnabled);
        Assert.False(automatic.IsExplicitOverride);

        Assert.True(settings.TrySetRaw(
            "instance.java.executablePath",
            java8.ExecutablePath,
            instance.Id,
            out var pathError), pathError);

        var explicitSelection = await selection.ResolveAsync(instance, version);
        Assert.Equal(8, explicitSelection.Runtime?.Version?.Major);
        Assert.Equal(JavaCompatibilityStatus.TooOld, explicitSelection.Compatibility?.Status);
        Assert.True(explicitSelection.CompatibilityCheckEnabled);
        Assert.True(explicitSelection.IsExplicitOverride);
        Assert.False(explicitSelection.IsCompatibilityWarningAcknowledged);

        var acknowledgementToken = JavaCompatibilityAcknowledgement.Create(java8, explicitSelection.Requirement!);
        Assert.True(settings.TrySetRaw(
            "launcher.java.incompatiblePromptAcknowledged",
            acknowledgementToken,
            null,
            out var acknowledgementError), acknowledgementError);

        var acknowledged = await selection.ResolveAsync(instance, version);
        Assert.True(acknowledged.IsCompatibilityWarningAcknowledged);

        // 风险组合改变后必须重新确认：切换到另一条 Java 路径。
        var java11 = Runtime(environment, "11.0.27");
        fakeLocator = new FakeJavaLocator([java8, java11, java17]);
        // 本测试中的 locator 由 DI 单例提供，改用独立实例验证不同 Java 的确认状态。
        await using var changedJavaProvider = environment.CreateServices(services =>
            services.AddSingleton<IJavaLocator>(fakeLocator));
        var changedJavaInstance = await CreateVersionAsync(
            changedJavaProvider, environment, "1.20.1");
        var changedJavaVersion = (await changedJavaProvider.GetRequiredService<IVersionResolver>()
            .ResolveAsync(changedJavaInstance.VersionId)).Value!;
        var changedJavaSettings = changedJavaProvider.GetRequiredService<ISettingsService>();
        Assert.True(changedJavaSettings.TrySetRaw(
            "instance.java.executablePath", java11.ExecutablePath, changedJavaInstance.Id, out var changedPathError),
            changedPathError);
        Assert.True(changedJavaSettings.TrySetRaw(
            "launcher.java.incompatiblePromptAcknowledged",
            JavaCompatibilityAcknowledgement.Create(java8, explicitSelection.Requirement!),
            null,
            out var oldAckError), oldAckError);
        var changedJavaSelection = await changedJavaProvider.GetRequiredService<IJavaSelectionService>()
            .ResolveAsync(changedJavaInstance, changedJavaVersion);
        Assert.False(changedJavaSelection.IsCompatibilityWarningAcknowledged);

        // 同一 Java 但最低版本要求变化，也必须重新确认。
        var newerInstance = await CreateVersionAsync(changedJavaProvider, environment, "1.21.1", javaMajor: 21);
        var newerVersion = (await changedJavaProvider.GetRequiredService<IVersionResolver>()
            .ResolveAsync(newerInstance.VersionId)).Value!;
        Assert.True(changedJavaSettings.TrySetRaw(
            "instance.java.executablePath", java8.ExecutablePath, newerInstance.Id, out var newerPathError),
            newerPathError);
        var newerSelection = await changedJavaProvider.GetRequiredService<IJavaSelectionService>()
            .ResolveAsync(newerInstance, newerVersion);
        Assert.False(newerSelection.IsCompatibilityWarningAcknowledged);
    }

    [Fact]
    public async Task GlobalJavaPathsArePriorityOrderedButSkipIncompatibleEntries()
    {
        using var environment = new TestEnvironment();
        var java8 = Runtime(environment, "8.0.412");
        var java17 = Runtime(environment, "17.0.15");
        var java21 = Runtime(environment, "21.0.7");
        var fakeLocator = new FakeJavaLocator([java21, java8, java17]);

        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IJavaLocator>(fakeLocator));

        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw(
            "java.defaultPath",
            string.Join(';', java8.ExecutablePath, java21.ExecutablePath, java17.ExecutablePath),
            null,
            out var pathError), pathError);

        var instance = await CreateVersionAsync(provider, environment, "1.20.1", javaMajor: 17);
        var version = (await provider.GetRequiredService<IVersionResolver>().ResolveAsync(instance.VersionId)).Value!;
        var selection = await provider.GetRequiredService<IJavaSelectionService>()
            .ResolveAsync(instance, version);

        // Java 8 是第一优先级，但不兼容 Java 17+ 的实例，因此跳过；Java 21 才是首个兼容项。
        Assert.Equal(21, selection.Runtime?.Version?.Major);
        Assert.True(selection.Compatibility?.IsCompatible);
        Assert.False(selection.IsExplicitOverride);
    }

    [Fact]
    public async Task FallsBackToAutoWhenExplicitPathIsInvalid()
    {
        using var environment = new TestEnvironment();
        var java17 = Runtime(environment, "17.0.15");
        var fakeLocator = new FakeJavaLocator([java17]);

        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IJavaLocator>(fakeLocator));

        var instance = await CreateVersionAsync(provider, environment, "1.20.1");
        var version = (await provider.GetRequiredService<IVersionResolver>().ResolveAsync(instance.VersionId)).Value!;

        Assert.True(provider.GetRequiredService<ISettingsService>().TrySetRaw(
            "instance.java.executablePath",
            Path.Combine(environment.Root, "missing-java"),
            instance.Id,
            out var error), error);

        var selection = await provider.GetRequiredService<IJavaSelectionService>()
            .ResolveAsync(instance, version);

        Assert.Equal(17, selection.Runtime?.Version?.Major);
        Assert.NotEmpty(selection.Warnings);
        Assert.False(selection.IsExplicitOverride);
    }

    [Fact]
    public async Task SkipsContainerDirectoryAndSelectsCompatibleRuntime()
    {
        using var environment = new TestEnvironment();
        var java8 = Runtime(environment, "8.0.412");
        var java17 = Runtime(environment, "17.0.15");
        var root = Path.Combine(environment.Root, "all-java");
        Directory.CreateDirectory(root);

        // 模拟“用户把包含多个 Java 的父目录填进全局设置”：
        // 直接解析这个目录会得到 17，但 1.16.5 应该优先选择 Java 8。
        var fakeLocator = new FakeJavaLocator(
            [java8, java17],
            path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ? java17 : null);

        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IJavaLocator>(fakeLocator));

        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultPath", root, null, out var pathError), pathError);

        var instance = await CreateVersionAsync(provider, environment, "1.16.5", javaMajor: 8);
        var version = (await provider.GetRequiredService<IVersionResolver>().ResolveAsync(instance.VersionId)).Value!;
        var selection = await provider.GetRequiredService<IJavaSelectionService>()
            .ResolveAsync(instance, version);

        Assert.Equal(8, selection.Runtime?.Version?.Major);
        Assert.True(selection.Compatibility?.IsCompatible);
        Assert.False(selection.IsExplicitOverride);
    }

    [Fact]
    public async Task InstanceMemoryOverridesGlobalMemory()
    {
        using var environment = new TestEnvironment();
        var java17 = Runtime(environment, "17.0.15");
        await using var provider = environment.CreateServices(services =>
            services.AddSingleton<IJavaLocator>(new FakeJavaLocator([java17])));

        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultMemoryMb", "4096", null, out var globalError), globalError);

        var instance = await CreateVersionAsync(provider, environment, "1.20.1");
        var version = (await provider.GetRequiredService<IVersionResolver>().ResolveAsync(instance.VersionId)).Value!;
        var selection = provider.GetRequiredService<IJavaSelectionService>();

        var globalOnly = await selection.ResolveAsync(instance, version);
        Assert.Equal(4096, globalOnly.MemoryMb);

        Assert.True(
            settings.TrySetRaw("instance.java.memoryMb", "6144", instance.Id, out var instanceError),
            instanceError);
        var overridden = await selection.ResolveAsync(instance, version);
        Assert.Equal(6144, overridden.MemoryMb);

        Assert.True(settings.TryClear("instance.java.memoryMb", instance.Id, out var clearError), clearError);
        Assert.Equal(4096, (await selection.ResolveAsync(instance, version)).MemoryMb);
    }

    private static async Task<GameInstance> CreateVersionAsync(
        IServiceProvider provider,
        TestEnvironment environment,
        string gameVersion,
        int javaMajor = 17)
    {
        var created = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync("Java Test", gameVersion);
        Assert.True(created.IsSuccess);
        var instance = created.Value!;

        var path = VersionFilePlanner.GetVersionJsonPath(environment.Paths, instance.VersionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $$"""
            {
              "id": "{{gameVersion}}",
              "type": "release",
              "javaVersion": { "component": "java-runtime-gamma", "majorVersion": {{javaMajor}} },
              "libraries": []
            }
            """);

        return instance;
    }

    private static JavaRuntime Runtime(TestEnvironment environment, string version)
    {
        var parsed = JavaVersion.TryParse(version)!;
        var home = Path.Combine(environment.Root, "java", version);
        return new JavaRuntime(
            home,
            Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"),
            "Test",
            "x86_64",
            parsed);
    }

    private sealed class FakeJavaLocator(
        IReadOnlyList<JavaRuntime> runtimes,
        Func<string, JavaRuntime?>? fromPath = null) : IJavaLocator
    {
        public Task<IReadOnlyList<JavaRuntime>> FindAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(runtimes);

        public JavaRuntime? FromPath(string path) =>
            fromPath?.Invoke(path) ??
            runtimes.FirstOrDefault(runtime =>
                string.Equals(runtime.ExecutablePath, path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(runtime.HomePath, path, StringComparison.OrdinalIgnoreCase));
    }
}
