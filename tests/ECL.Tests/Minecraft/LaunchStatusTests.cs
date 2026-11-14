// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// 主页启动状态（home.md §3.4）：按实例归属、ECL 关闭后不丢失、下一次启动开始时重置。
/// 这里只锁定存储契约与启动服务的记录时机，不重复测试启动流水线本身。
/// </summary>
public sealed class LaunchStatusTests
{
    [Fact]
    public void MissingRecordReadsAsNull()
    {
        using var environment = new TestEnvironment();
        var store = CreateStore(environment);

        Assert.Null(store.Get("missing-instance"));
    }

    [Fact]
    public void StartedRecordIsRunningWithNoSessionOrSteps()
    {
        using var environment = new TestEnvironment();
        var store = CreateStore(environment);

        store.RecordStarted("instance-a");
        var snapshot = store.Get("instance-a");

        Assert.NotNull(snapshot);
        Assert.Equal("instance-a", snapshot.InstanceId);
        Assert.Equal(LaunchAttemptState.Running, snapshot.State);
        Assert.Null(snapshot.CompletedAtUtc);
        Assert.Null(snapshot.SessionId);
        Assert.Empty(snapshot.Steps);
    }

    [Fact]
    public void CompletedFailureKeepsSessionFailureSummaryAndSteps()
    {
        using var environment = new TestEnvironment();
        var store = CreateStore(environment);

        store.RecordStarted("instance-a");
        store.RecordCompleted("instance-a", new LaunchReport(
            Success: false,
            DryRun: false,
            CommandLine: "java -Xmx2048M",
            ProcessId: null,
            Steps:
            [
                new LaunchStepResult("instance", LaunchStepStatus.Success, "ok"),
                new LaunchStepResult("files", LaunchStepStatus.Failed, "缺少资源"),
            ],
            Duration: TimeSpan.FromSeconds(3),
            SessionId: "session-1"));

        var snapshot = store.Get("instance-a");

        Assert.NotNull(snapshot);
        Assert.Equal(LaunchAttemptState.Failed, snapshot.State);
        Assert.NotNull(snapshot.CompletedAtUtc);
        Assert.Equal("session-1", snapshot.SessionId);
        Assert.Equal("缺少资源", snapshot.FailureSummary);
        Assert.Equal(["instance", "files"], snapshot.Steps.Select(step => step.StepId));
        Assert.Equal(LaunchStepStatus.Failed, snapshot.Steps[1].Status);
        Assert.Equal(TimeSpan.FromSeconds(3), snapshot.Duration);
    }

    [Fact]
    public void StartingAgainClearsPreviousFailureAndSession()
    {
        using var environment = new TestEnvironment();
        var store = CreateStore(environment);

        store.RecordCompleted("instance-a", FailedReport(sessionId: "session-1"));
        store.RecordStarted("instance-a");

        var snapshot = store.Get("instance-a");

        Assert.NotNull(snapshot);
        Assert.Equal(LaunchAttemptState.Running, snapshot.State);
        Assert.Null(snapshot.SessionId);
        Assert.Null(snapshot.FailureSummary);
        Assert.Empty(snapshot.Steps);
    }

    [Fact]
    public void RecordsStayPerInstance()
    {
        using var environment = new TestEnvironment();
        var store = CreateStore(environment);

        store.RecordCompleted("instance-a", FailedReport(sessionId: "session-a"));
        store.RecordCompleted("instance-b", new LaunchReport(
            Success: true,
            DryRun: false,
            CommandLine: null,
            ProcessId: 42,
            Steps: [new LaunchStepResult("start", LaunchStepStatus.Success, "started")],
            Duration: TimeSpan.FromSeconds(1),
            SessionId: "session-b"));

        Assert.Equal("session-a", store.Get("instance-a")!.SessionId);
        Assert.Equal(LaunchAttemptState.Failed, store.Get("instance-a")!.State);
        Assert.Equal("session-b", store.Get("instance-b")!.SessionId);
        Assert.Equal(LaunchAttemptState.Succeeded, store.Get("instance-b")!.State);
    }

    [Fact]
    public void DryRunReportsAreNotRecorded()
    {
        using var environment = new TestEnvironment();
        var store = CreateStore(environment);

        store.RecordCompleted("instance-a", new LaunchReport(
            Success: true,
            DryRun: true,
            CommandLine: "java",
            ProcessId: null,
            Steps: [new LaunchStepResult("start", LaunchStepStatus.Skipped, "dry run")],
            Duration: TimeSpan.Zero));

        Assert.Null(store.Get("instance-a"));
    }

    [Fact]
    public void UnsafeInstanceIdIsRejected()
    {
        using var environment = new TestEnvironment();
        var store = CreateStore(environment);

        store.RecordStarted("../escape");
        store.RecordCompleted("../escape", FailedReport(sessionId: null));

        Assert.Null(store.Get("../escape"));

        var statusDirectory = Path.Combine(environment.Paths.LogsDirectory, "launch-status");
        Assert.False(Directory.Exists(statusDirectory) && Directory.EnumerateFiles(statusDirectory).Any());
    }

    [Fact]
    public void CorruptRecordReadsAsNull()
    {
        using var environment = new TestEnvironment();
        var store = CreateStore(environment);
        var directory = Path.Combine(environment.Paths.LogsDirectory, "launch-status");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "instance-a.json"), "{ not json");

        Assert.Null(store.Get("instance-a"));
    }

    [Fact]
    public async Task RealLaunchRecordsSucceededSnapshotWithSession()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var instance = await CreateInstanceAsync(provider);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");
        ConfigureFakeJava(environment, provider);
        Assert.True(provider.GetRequiredService<IEulaService>().Accept().IsSuccess);
        WriteVersionFiles(environment, instance.VersionId);

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: false);
        var snapshot = provider.GetRequiredService<ILaunchStatusStore>().Get(instance.Id);

        Assert.True(report.Success);
        Assert.Equal(1, fake.Calls);
        Assert.NotNull(snapshot);
        Assert.Equal(LaunchAttemptState.Succeeded, snapshot.State);
        Assert.Equal(report.SessionId, snapshot.SessionId);
        Assert.NotNull(snapshot.SessionId);
        Assert.Contains(snapshot.Steps, step => step.StepId == "start" && step.Status == LaunchStepStatus.Success);
    }

    [Fact]
    public async Task FailedLaunchRecordsFailureWithSteps()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var instance = await CreateInstanceAsync(provider);

        // 没有账户：启动在进入步骤前就被拒绝，仍必须记录为一次失败的启动尝试。
        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: false);
        var snapshot = provider.GetRequiredService<ILaunchStatusStore>().Get(instance.Id);

        Assert.False(report.Success);
        Assert.Equal(0, fake.Calls);
        Assert.NotNull(snapshot);
        Assert.Equal(LaunchAttemptState.Failed, snapshot.State);
        Assert.Equal("account", Assert.Single(snapshot.Steps).StepId);
        Assert.Null(snapshot.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.FailureSummary));
    }

    [Fact]
    public async Task DryRunDoesNotTouchRecordedStatus()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var instance = await CreateInstanceAsync(provider);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");
        ConfigureFakeJava(environment, provider);
        Assert.True(provider.GetRequiredService<IEulaService>().Accept().IsSuccess);
        WriteVersionFiles(environment, instance.VersionId);
        var store = provider.GetRequiredService<ILaunchStatusStore>();

        var launch = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: false);
        var before = store.Get(instance.Id);
        var dryRun = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: true);
        var after = store.Get(instance.Id);

        Assert.True(launch.Success);
        Assert.True(dryRun.Success);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(before.State, after.State);
    }

    [Fact]
    public async Task StepProgressReportsStartedAndCompletedInPipelineOrder()
    {
        using var environment = new TestEnvironment();
        var fake = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services => services.AddSingleton<IProcessLauncher>(fake));

        var instance = await CreateInstanceAsync(provider);
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");
        ConfigureFakeJava(environment, provider);
        Assert.True(provider.GetRequiredService<IEulaService>().Accept().IsSuccess);
        WriteVersionFiles(environment, instance.VersionId);

        var launch = provider.GetRequiredService<ILaunchService>();
        var events = new List<LaunchStepProgress>();
        var report = await launch.LaunchAsync(
            instance.Id,
            dryRun: false,
            acceptJavaRisk: false,
            progress: new SynchronousProgress<LaunchStepProgress>(events.Add));

        Assert.True(report.Success);
        Assert.NotEmpty(events);
        Assert.Equal(launch.Steps.Count * 2, events.Count);
        Assert.Equal(
            launch.Steps,
            events.Where(item => item.Phase == LaunchStepProgressPhase.Started).Select(item => item.StepId));
        Assert.Equal(
            launch.Steps,
            events.Where(item => item.Phase == LaunchStepProgressPhase.Completed).Select(item => item.StepId));
        Assert.All(
            events.Where(item => item.Phase == LaunchStepProgressPhase.Completed),
            item => Assert.NotNull(item.Status));
        Assert.All(events, item => Assert.Equal(launch.Steps.Count, item.Total));
    }

    private static LaunchStatusStore CreateStore(TestEnvironment environment) =>
        new(environment.Paths, NullLogger<LaunchStatusStore>.Instance);

    private static void ConfigureFakeJava(TestEnvironment environment, IServiceProvider provider)
    {
        var fakeJavaHome = Path.Combine(environment.Root, "fakejava");
        var fakeJavaBin = Path.Combine(fakeJavaHome, "bin");
        Directory.CreateDirectory(fakeJavaBin);
        var fakeJava = Path.Combine(fakeJavaBin, OperatingSystem.IsWindows() ? "java.exe" : "java");
        File.WriteAllText(fakeJava, string.Empty);
        File.WriteAllText(
            Path.Combine(fakeJavaHome, "release"),
            "JAVA_VERSION=\"21.0.1\"\nIMPLEMENTOR=\"ECL Test Runtime\"\nOS_ARCH=\"x86_64\"\n");
        var settings = provider.GetRequiredService<Ecl.Configuration.Settings.ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultPath", fakeJava, null, out var error), error);
    }

    private static void WriteVersionFiles(TestEnvironment environment, string versionId)
    {
        var versionDirectory = Path.Combine(environment.Paths.VersionsDirectory, versionId);
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(
            Path.Combine(versionDirectory, $"{versionId}.json"),
            """
            {
              "id": "1.21.1",
              "type": "release",
              "mainClass": "net.minecraft.client.main.Main",
              "assetIndex": { "id": "1.21" },
              "libraries": []
            }
            """);
        File.WriteAllBytes(Path.Combine(versionDirectory, $"{versionId}.jar"), []);
        var assetIndexDirectory = Path.Combine(environment.Paths.AssetsDirectory, "indexes");
        Directory.CreateDirectory(assetIndexDirectory);
        File.WriteAllText(Path.Combine(assetIndexDirectory, "1.21.json"), """{ "objects": {} }""");
    }

    private static LaunchReport FailedReport(string? sessionId) => new(
        Success: false,
        DryRun: false,
        CommandLine: null,
        ProcessId: null,
        Steps: [new LaunchStepResult("files", LaunchStepStatus.Failed, "失败")],
        Duration: TimeSpan.FromSeconds(1),
        SessionId: sessionId);

    private static async Task<GameInstance> CreateInstanceAsync(IServiceProvider provider)
    {
        var created = await provider.GetRequiredService<IInstanceService>().CreateAsync("Test", "1.21.1");
        Assert.True(created.IsSuccess);
        return created.Value!;
    }

    /// <summary>同步进度：报告顺序就是执行顺序，避免测试依赖线程池调度。</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakeProcessLauncher : IProcessLauncher
    {
        public int Calls { get; private set; }

        public int Start(LaunchCommand command, string workingDirectory, bool showConsole, string instanceId, string? sessionId = null)
        {
            Calls++;
            return 4242;
        }
    }
}
