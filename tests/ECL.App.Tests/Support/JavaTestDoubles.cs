// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.Java;

namespace Ecl.App.Tests.Support;

/// <summary>
/// Java 管理页测试用的可替换实现：不执行真实 Java 进程、不删除真实目录。
/// 只模拟领域接口的返回，测试仍验证页面自己的状态机、持久化与文案。
/// </summary>
internal sealed class FakeJavaLocator : IJavaLocator
{
    public List<JavaRuntime> Discovered { get; } = [];

    public Dictionary<string, JavaRuntime> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int RefreshCalls { get; private set; }

    public int FromPathCalls { get; private set; }

    public bool FailRefresh { get; set; }

    public Task<IReadOnlyList<JavaRuntime>> FindAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JavaRuntime>>([.. Discovered]);

    public Task<IReadOnlyList<JavaRuntime>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCalls++;
        return FailRefresh
            ? Task.FromException<IReadOnlyList<JavaRuntime>>(new IOException("scan failed"))
            : Task.FromResult<IReadOnlyList<JavaRuntime>>([.. Discovered]);
    }

    public JavaRuntime? FromPath(string path)
    {
        FromPathCalls++;
        return ByPath.GetValueOrDefault(path);
    }

    public void InvalidateCache()
    {
    }
}

internal sealed class FakeJavaRuntimeValidator : IJavaRuntimeValidator
{
    public Dictionary<string, JavaRuntimeValidation> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    public JavaRuntimeValidation Default { get; set; } = new(
        JavaRuntimeValidationState.Available,
        new JavaVersion(21, 0, 1, "21.0.1"),
        "Eclipse Adoptium",
        "amd64",
        JavaRuntimeValidationFailure.None,
        null,
        TimeSpan.FromMilliseconds(12));

    public List<string> ValidatedPaths { get; } = [];

    public Task<JavaRuntimeValidation> ValidateAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ValidatedPaths.Add(runtime.ExecutablePath);
        return Task.FromResult(Results.GetValueOrDefault(runtime.ExecutablePath, Default));
    }
}

internal sealed class FakeJavaRuntimeUninstallService : IJavaRuntimeUninstallService
{
    public Func<JavaRuntime, JavaUninstallPlan> PlanFactory { get; set; } = runtime =>
        new JavaUninstallPlan(runtime, IsManaged: true, runtime.HomePath, []);

    public JavaUninstallResult Result { get; set; } = JavaUninstallResult.Ok;

    public List<string> PlannedPaths { get; } = [];

    public List<string> UninstalledPaths { get; } = [];

    public Task<JavaUninstallPlan> CreatePlanAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        PlannedPaths.Add(runtime.ExecutablePath);
        return Task.FromResult(PlanFactory(runtime));
    }

    public Task<JavaUninstallResult> UninstallAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        UninstalledPaths.Add(runtime.ExecutablePath);
        return Task.FromResult(Result);
    }
}

internal static class JavaTestRuntimes
{
    public static JavaRuntime Create(
        string home,
        string version = "21.0.1",
        string vendor = "Eclipse Adoptium",
        string architecture = "amd64",
        bool managed = false)
    {
        var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return new JavaRuntime(
            home,
            Path.Combine(home, "bin", executableName),
            vendor,
            architecture,
            JavaVersion.TryParse(version),
            managed ? JavaRuntimeSource.EclManaged : JavaRuntimeSource.SystemLocation,
            managed);
    }
}
