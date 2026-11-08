// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Java;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Java;

/// <summary>
/// Java 管理领域（java-management.md / ADR 0014）：
/// 用户登记与移除状态、逐运行时验证、候选顺序组合、路径安全与托管运行时卸载边界。
/// </summary>
public sealed class JavaRuntimeManagementTests
{
    [Fact]
    public void RegistryPersistsImportedAndRemovedPaths()
    {
        using var root = new TempDirectory();
        var paths = new AppPaths(root.Path);
        var executable = Path.Combine(root.Path, "jdk21", "bin", "java.exe");

        var registry = new JavaRuntimeRegistry(paths, NullLogger<JavaRuntimeRegistry>.Instance);
        Assert.True(registry.RegisterImported(executable));
        Assert.True(registry.IsImported(executable));

        // 重新打开时从磁盘恢复用户登记。
        var reloaded = new JavaRuntimeRegistry(paths, NullLogger<JavaRuntimeRegistry>.Instance);
        Assert.True(reloaded.IsImported(executable));
        Assert.False(reloaded.IsRemoved(executable));

        // 移除同时取消登记并记录排除，扫描不会把它自动带回。
        Assert.True(reloaded.MarkRemoved(executable));
        Assert.False(reloaded.IsImported(executable));
        Assert.True(reloaded.IsRemoved(executable));

        // 用户重新添加同一路径时清除排除记录。
        Assert.True(reloaded.RegisterImported(executable));
        Assert.True(reloaded.IsImported(executable));
        Assert.False(reloaded.IsRemoved(executable));
    }

    [Fact]
    public void CorruptRegistryStateIsToleratedAndRebuilt()
    {
        using var root = new TempDirectory();
        var paths = new AppPaths(root.Path);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "java-runtimes.json"), "not-json");

        var registry = new JavaRuntimeRegistry(paths, NullLogger<JavaRuntimeRegistry>.Instance);
        Assert.Empty(registry.ImportedPaths);
        Assert.Empty(registry.RemovedPaths);

        var executable = Path.Combine(root.Path, "jdk", "bin", "java.exe");
        Assert.True(registry.RegisterImported(executable));
        var json = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(paths.ConfigDirectory, "java-runtimes.json")));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(new JavaRuntimeRegistry(paths, NullLogger<JavaRuntimeRegistry>.Instance)
            .IsImported(executable));
    }

    [Fact]
    public void ParsesJavaVersionOutputFromRealPropertyDumps()
    {
        const string modern = """
            Property settings:
                java.home = /opt/java/21
                java.version = 21.0.4
                java.vendor = Eclipse Adoptium
                os.arch = amd64

            openjdk version "21.0.4" 2024-07-16 LTS
            """;

        var parsed = JavaVersionOutput.TryParse(modern);

        Assert.NotNull(parsed);
        Assert.Equal(21, parsed!.Version!.Major);
        Assert.Equal("Eclipse Adoptium", parsed.Vendor);
        Assert.Equal("amd64", parsed.Architecture);

        // Java 8 只打印 -version 行，没有 properties；仍能解析版本。
        var legacy = JavaVersionOutput.TryParse("java version \"1.8.0_402\"\nJava(TM) SE Runtime Environment");
        Assert.NotNull(legacy);
        Assert.Equal(8, legacy!.Version!.Major);

        // os.arch 缺失时用 sun.arch.data.model 判断 32 位。
        var thirtyTwo = JavaVersionOutput.TryParse(
            "Property settings:\n    java.version = 1.8.0_402\n    sun.arch.data.model = 32\n");
        Assert.NotNull(thirtyTwo);
        Assert.Equal("x86", thirtyTwo!.Architecture);

        Assert.Null(JavaVersionOutput.TryParse("no java output here"));
        Assert.Null(JavaVersionOutput.TryParse(string.Empty));
    }

    [Theory]
    [InlineData("java-runtime-gamma", true)]
    [InlineData("java-runtime-gamma/nested", false)]
    public void OnlyDirectChildrenOfTheRuntimesDirectoryCountAsManaged(string relative, bool expected)
    {
        using var root = new TempDirectory();
        var runtimes = Path.Combine(root.Path, "Runtimes");
        var home = Path.Combine(runtimes, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, JavaRuntimePath.IsDirectChild(home, runtimes));
        Assert.True(JavaRuntimePath.IsUnder(Path.Combine(home, "bin", "java.exe"), home));
    }

    [Fact]
    public async Task ManagedRuntimeCanBeUninstalledOnlyWhenItIsInsideTheRuntimesDirectory()
    {
        using var root = new TempDirectory();
        var paths = new AppPaths(root.Path);
        var home = CreateManagedRuntime(paths, "java-runtime-gamma");
        var runtime = CreateRuntime(home);
        var service = new JavaRuntimeUninstallService(
            paths,
            [],
            NullLogger<JavaRuntimeUninstallService>.Instance);

        var plan = await service.CreatePlanAsync(runtime);
        Assert.True(plan.IsManaged);
        Assert.True(plan.CanUninstall);

        var result = await service.UninstallAsync(runtime);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public async Task ExternalRuntimeIsNeverDeletedAndReportsTheRealReason()
    {
        using var root = new TempDirectory();
        var paths = new AppPaths(root.Path);
        var home = Path.Combine(root.Path, "external-jdk");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", "java.exe"), string.Empty);
        var runtime = CreateRuntime(home);
        var service = new JavaRuntimeUninstallService(
            paths,
            [],
            NullLogger<JavaRuntimeUninstallService>.Instance);

        var plan = await service.CreatePlanAsync(runtime);
        var result = await service.UninstallAsync(runtime);

        Assert.False(plan.IsManaged);
        Assert.Contains(plan.Blockers, blocker => blocker.Kind == JavaUninstallBlockerKind.NotManaged);
        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public async Task UsageProvidersBlockUninstallAndFailuresAreTreatedAsUnknown()
    {
        using var root = new TempDirectory();
        var paths = new AppPaths(root.Path);
        var home = CreateManagedRuntime(paths, "java-runtime-delta");
        var runtime = CreateRuntime(home);

        var pinned = new JavaRuntimeUninstallService(
            paths,
            [new StubUsageProvider(runtime, new JavaUninstallBlocker(
                JavaUninstallBlockerKind.InstancePinned,
                "My Instance (abc123)"))],
            NullLogger<JavaRuntimeUninstallService>.Instance);
        var plan = await pinned.CreatePlanAsync(runtime);
        var result = await pinned.UninstallAsync(runtime);

        Assert.False(plan.CanUninstall);
        Assert.Contains(plan.Blockers, blocker =>
            blocker.Kind == JavaUninstallBlockerKind.InstancePinned &&
            blocker.Detail.Contains("My Instance", StringComparison.Ordinal));
        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(home));

        // 探测失败不能被当成“没有引用”。
        var failing = new JavaRuntimeUninstallService(
            paths,
            [new ThrowingUsageProvider()],
            NullLogger<JavaRuntimeUninstallService>.Instance);
        var unknownPlan = await failing.CreatePlanAsync(runtime);
        Assert.Contains(unknownPlan.Blockers, blocker => blocker.Kind == JavaUninstallBlockerKind.UsageUnknown);
        Assert.False(unknownPlan.CanUninstall);
    }

    [Fact]
    public void CatalogMergesDiscoveryAndRegistrationWhileRemovedPathsStayHidden()
    {
        using var root = new TempDirectory();
        var runtimes = Path.Combine(root.Path, "Runtimes");
        var managed = CreateManagedRuntime(new AppPaths(root.Path), "java-runtime-gamma");
        var discoveredA = CreateRuntime(Path.Combine(root.Path, "jdk-a"));
        var discoveredB = CreateRuntime(Path.Combine(root.Path, "jdk-b"));
        var imported = CreateRuntime(Path.Combine(root.Path, "imported-jdk"));

        var entries = JavaRuntimeCatalog.Compose(
            [discoveredA, discoveredB],
            [imported, discoveredA],
            [discoveredB.ExecutablePath],
            [imported.ExecutablePath],
            runtimes);

        // 用户优先级在前，其余按发现顺序；同一可执行文件不重复。
        Assert.Equal(
            [imported.ExecutablePath, discoveredA.ExecutablePath],
            entries.Select(entry => entry.Runtime.ExecutablePath));
        Assert.Equal(JavaRuntimeOrigin.UserImported, entries[0].Origin);
        // 同一路径既被发现又被用户登记时，来源按“已登记”呈现（用户显式添加过）。
        Assert.Equal(JavaRuntimeOrigin.UserImported, entries[1].Origin);

        var managedEntry = JavaRuntimeCatalog.Compose(
            [CreateRuntime(managed)],
            [],
            [],
            [],
            runtimes);
        Assert.Equal(JavaRuntimeOrigin.EclManaged, managedEntry.Single().Origin);
    }

    private static JavaRuntime CreateRuntime(string home)
    {
        var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return new JavaRuntime(
            home,
            Path.Combine(home, "bin", executableName),
            "Eclipse Adoptium",
            "amd64",
            JavaVersion.TryParse("21.0.1"),
            JavaRuntimeSource.SystemLocation);
    }

    private static string CreateManagedRuntime(AppPaths paths, string component)
    {
        var home = Path.Combine(paths.RuntimesDirectory, component);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), string.Empty);
        return home;
    }

    private sealed class StubUsageProvider(JavaRuntime expected, JavaUninstallBlocker blocker)
        : IJavaRuntimeUsageProvider
    {
        public Task<IReadOnlyList<JavaUninstallBlocker>> FindBlockersAsync(
            JavaRuntime runtime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JavaUninstallBlocker>>(
                JavaRuntimePath.Same(runtime.ExecutablePath, expected.ExecutablePath) ? [blocker] : []);
    }

    private sealed class ThrowingUsageProvider : IJavaRuntimeUsageProvider
    {
        public Task<IReadOnlyList<JavaUninstallBlocker>> FindBlockersAsync(
            JavaRuntime runtime,
            CancellationToken cancellationToken = default) =>
            throw new IOException("probe failed");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ecl-java-domain-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // 测试清理失败不影响结果。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }
}
