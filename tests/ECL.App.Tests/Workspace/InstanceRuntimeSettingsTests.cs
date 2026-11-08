// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.App.Services;
using Ecl.App.Workspace.ViewModels;
using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Versions;
using Xunit;

namespace Ecl.App.Tests.Workspace;

/// <summary>
/// 实例工作区自有的 Java/内存/JVM 编辑状态（B2 步骤 1c）。
/// 两个实例作用域共享同一个设置服务时，读写必须按稳定 InstanceId 隔离；
/// 释放一个作用域不影响另一个。
/// </summary>
public sealed class InstanceRuntimeSettingsTests
{
    private static GameInstance Instance(string id) => new()
    {
        Id = id,
        Name = id,
        GameVersion = "1.21.1",
        VersionId = $"{id}-version",
        Loader = "fabric",
        LoaderVersion = "0.16.10",
    };

    private static InstanceRuntimeSettingsViewModel Create(ISettingsService settings, string instanceId) =>
        new(
            Instance(instanceId),
            settings,
            new FakeJavaLocator(),
            new FakeJavaSelectionService(),
            new FakeMemoryAllocationService(settings),
            new FakeJavaInstallService(),
            new FakeVersionResolver(),
            new FakeDownloadController(),
            new FakeDialogService(),
            new JsonLocalizer(CultureInfo.GetCultureInfo("zh-CN")));

    [Fact]
    public void TwoScopesSaveRuntimeSettingsIndependently()
    {
        var settings = new InMemorySettingsService();
        using var first = Create(settings, "instance-a");
        using var second = Create(settings, "instance-b");

        first.MemoryMbText = "4096";
        first.UseGlobalMemory = false;
        first.JvmArguments = "-Xmx4G";
        first.JvmPreset = "g1";
        first.SaveCommand.Execute(null);

        second.UseGlobalMemory = true;
        second.JvmArguments = "-Xmx2G";
        second.SaveCommand.Execute(null);

        Assert.Equal(4096, settings.Get<int?>("instance.java.memoryMb", "instance-a"));
        Assert.Null(settings.Get<int?>("instance.java.memoryMb", "instance-b"));
        Assert.Equal("-Xmx4G", settings.Get<string?>("instance.java.extraArguments", "instance-a"));
        Assert.Equal("-Xmx2G", settings.Get<string?>("instance.java.extraArguments", "instance-b"));
        Assert.Equal("g1", settings.Get<string?>("instance.java.jvmPreset", "instance-a"));
        Assert.True(second.IsMemoryEditable is false);
        Assert.True(first.IsMemoryEditable);
    }

    [Fact]
    public void ScopeLoadsOnlyItsOwnSavedValues()
    {
        var settings = new InMemorySettingsService();
        settings.TrySetRaw("instance.java.memoryMb", "8192", "instance-a", out _);
        settings.TrySetRaw("instance.java.extraArguments", "-XX:+UseZGC", "instance-a", out _);
        settings.TrySetRaw("instance.java.extraArguments", "-XX:+UseSerialGC", "instance-b", out _);

        using var first = Create(settings, "instance-a");
        using var second = Create(settings, "instance-b");

        Assert.Equal("8192", first.MemoryMbText);
        Assert.False(first.UseGlobalMemory);
        Assert.Equal("-XX:+UseZGC", first.JvmArguments);

        Assert.Equal(string.Empty, second.MemoryMbText);
        Assert.True(second.UseGlobalMemory);
        Assert.Equal("-XX:+UseSerialGC", second.JvmArguments);
    }

    [Fact]
    public void DisposingOneScopeLeavesTheOtherUsable()
    {
        var settings = new InMemorySettingsService();
        var first = Create(settings, "instance-a");
        using var second = Create(settings, "instance-b");

        first.Dispose();

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);

        second.JvmArguments = "-Xmx1G";
        second.SaveCommand.Execute(null);
        Assert.Equal("-Xmx1G", settings.Get<string?>("instance.java.extraArguments", "instance-b"));
        Assert.Null(settings.Get<string?>("instance.java.extraArguments", "instance-a"));
    }

    [Fact]
    public void InvalidMemoryIsRejectedWithoutWriting()
    {
        var settings = new InMemorySettingsService();
        using var scope = Create(settings, "instance-a");

        scope.UseGlobalMemory = false;
        scope.MemoryMbText = "512";
        scope.SaveCommand.Execute(null);

        Assert.Null(settings.Get<int?>("instance.java.memoryMb", "instance-a"));
        Assert.False(string.IsNullOrWhiteSpace(scope.StatusMessage));
    }

    [Fact]
    public void EffectiveMemoryFollowsCurrentDraftAndGlobalInheritance()
    {
        var settings = new InMemorySettingsService();
        settings.TrySetRaw("java.defaultMemoryMb", "8192", null, out _);
        using var scope = Create(settings, "instance-a");

        Assert.True(scope.UseGlobalMemory);
        Assert.Equal(8192, scope.EffectiveMemoryMb);
        Assert.True(scope.IsMemoryAllocationValid);

        scope.UseGlobalMemory = false;
        scope.MemoryMbText = "4096";
        Assert.Equal(4096, scope.EffectiveMemoryMb);

        scope.MemoryMbText = "512";
        Assert.Equal(0, scope.EffectiveMemoryMb);
        Assert.False(scope.IsMemoryAllocationValid);
    }

    [Fact]
    public void RevertJvmClearsOnlyThisInstancesArguments()
    {
        var settings = new InMemorySettingsService();
        settings.TrySetRaw("instance.java.extraArguments", "-Xmx4G", "instance-a", out _);
        settings.TrySetRaw("instance.java.jvmPreset", "g1", "instance-a", out _);
        settings.TrySetRaw("instance.java.extraArguments", "-Xmx2G", "instance-b", out _);

        using var scope = Create(settings, "instance-a");
        Assert.True(scope.HasCustomJvmArguments);

        scope.RevertJvmCommand.Execute(null);

        Assert.Null(settings.Get<string?>("instance.java.extraArguments", "instance-a"));
        Assert.Null(settings.Get<string?>("instance.java.jvmPreset", "instance-a"));
        Assert.Equal("-Xmx2G", settings.Get<string?>("instance.java.extraArguments", "instance-b"));
        Assert.Equal(string.Empty, scope.JvmArguments);
        Assert.Equal(string.Empty, scope.JvmPreset);
        Assert.False(scope.HasCustomJvmArguments);
    }

    private sealed class InMemorySettingsService : ISettingsService
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public IReadOnlyList<OptionDefinition> GetDefinitions(OptionScope? scope = null) => [];

        public OptionDefinition GetDefinition(string key) => throw new NotSupportedException();

        public T? Get<T>(string key, string? instanceId = null)
        {
            if (!_values.TryGetValue(Key(key, instanceId), out var raw) || raw is null)
            {
                return default;
            }

            if (typeof(T) == typeof(string))
            {
                return (T)(object)raw;
            }

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return default;
            }

            if (typeof(T) == typeof(int))
            {
                return (T)(object)number;
            }

            if (typeof(T) == typeof(int?))
            {
                return (T)(object)(int?)number;
            }

            return default;
        }

        public string GetRawString(string key, string? instanceId = null) =>
            _values.TryGetValue(Key(key, instanceId), out var raw) ? raw ?? string.Empty : string.Empty;

        public SettingsValueSource GetSource(string key, string? instanceId = null) =>
            _values.ContainsKey(Key(key, instanceId)) ? SettingsValueSource.Instance : SettingsValueSource.Default;

        public bool IsVisible(OptionDefinition definition, string? instanceId = null) => true;

        public bool TrySetRaw(string key, string? rawValue, string? instanceId, out string? error)
        {
            error = null;
            _values[Key(key, instanceId)] = rawValue;
            Changed?.Invoke(this, new SettingsChangedEventArgs(key, instanceId));
            return true;
        }

        public bool TryClear(string key, string? instanceId, out string? error)
        {
            error = null;
            _values.Remove(Key(key, instanceId));
            Changed?.Invoke(this, new SettingsChangedEventArgs(key, instanceId));
            return true;
        }

        private static string Key(string key, string? instanceId) => $"{instanceId ?? "*"}::{key}";
    }

    private sealed class FakeJavaLocator : IJavaLocator
    {
        public Task<IReadOnlyList<JavaRuntime>> FindAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaRuntime>>([]);

        public JavaRuntime? FromPath(string path) => null;
    }

    private sealed class FakeJavaSelectionService : IJavaSelectionService
    {
        public Task<JavaSelectionResult> ResolveAsync(
            GameInstance instance,
            MinecraftVersion? version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new JavaSelectionResult(
                Runtime: null,
                Requirement: null,
                CompatibilityCheckEnabled: false,
                Compatibility: null,
                HasAnyDetectedJava: false,
                ExplicitPath: null,
                IsExplicitOverride: false,
                Warnings: [],
                MemoryMb: 4096,
                IsBlocked: false,
                BlockReason: null));

        public Task<EffectiveJavaRequirements> ResolveRequirementsAsync(
            GameInstance instance,
            MinecraftVersion? version,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMemoryAllocationService(ISettingsService settings) : IMemoryAllocationService
    {
        public MemoryAllocationPlan Resolve(int? instanceMemoryMb = null)
        {
            var value = instanceMemoryMb ?? settings.Get<int>("java.defaultMemoryMb");
            return new MemoryAllocationPlan(MemoryAllocationMode.Manual, value, 0, 0, true);
        }
    }

    private sealed class FakeJavaInstallService : IJavaInstallService
    {
        public Task<Result<JavaRuntime>> EnsureAsync(
            JavaRequirement requirement,
            IProgress<JavaInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<JavaRuntime>> DownloadAsync(
            JavaRequirement requirement,
            IProgress<JavaInstallProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeVersionResolver : IVersionResolver
    {
        public Task<Result<MinecraftVersion>> ResolveAsync(
            string versionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<MinecraftVersion>.Fail("not-found", "版本尚未安装。"));
    }

    private sealed class FakeDownloadController : Ecl.Download.IDownloadController
    {
        public bool IsPaused { get; private set; }

        public void Pause() => IsPaused = true;

        public void Resume() => IsPaused = false;
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool Confirm(string title, string message, string? acknowledgementToken = null) => true;

        public InstanceDeletionChoice ChooseInstanceDeletion(
            string title,
            string message,
            bool canDeleteManagedData) =>
            InstanceDeletionChoice.Cancel;

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickOpenFile(string title, string filter) => null;

        public IReadOnlyList<string> PickOpenFiles(string title, string filter) => [];

        public string? PickSaveFile(string title, string filter, string? defaultFileName = null) => null;

        public string? PickFolder(string title) => null;

        public bool OpenUrl(string url) => false;
    }
}
