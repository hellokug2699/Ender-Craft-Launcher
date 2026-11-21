// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Auth;
using Ecl.Configuration.Settings;
using Ecl.Core;
using Ecl.Java;
using Ecl.Minecraft.Eula;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Launch;
using Ecl.Minecraft.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

/// <summary>
/// ADR 0043 §6：启动前安全检查发现未接受风险时，必须暂停启动并把披露与选择交给交互层。
/// GUI 走风险确认回调（继续/取消），CLI 走显式标志；两种路径都不得静默放行，
/// 也不得把“没说不能”当成用户的接受。
/// </summary>
public sealed class LaunchSecurityConsentTests
{
    private const string InstanceName = "Gate";

    [Fact]
    public async Task AcceptingThroughTheInteractivePromptRecordsTheRiskAndContinues()
    {
        using var environment = new TestEnvironment();
        var security = new FakeSecurity(ThreatLevel.Suspicious);
        var processLauncher = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IThreatProtectionService>(security);
            services.AddSingleton<IProcessLauncher>(processLauncher);
        });
        var instance = await PrepareLaunchableInstanceAsync(environment, provider);

        var prompts = new List<SecurityRiskConsentRequest>();
        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(
            instance.Id,
            dryRun: false,
            acceptJavaRisk: false,
            confirmSecurityRisk: (request, _) =>
            {
                prompts.Add(request);
                return Task.FromResult(true);
            });

        Assert.True(report.Success, FailureMessage(report));
        Assert.Equal(1, processLauncher.Calls);
        var prompt = Assert.Single(prompts);
        Assert.Equal(instance.Id, prompt.InstanceId);
        Assert.Equal(InstanceName, prompt.InstanceName);
        Assert.Single(prompt.Findings);
        Assert.Equal("mods/evil.jar", prompt.Findings[0].RelativePath);

        Assert.Single(security.Accepted);
        Assert.Equal("mods/evil.jar", security.Accepted[0][0].RelativePath);
        // 接受只记录决定，不会把结论改成 Clean。
        Assert.Contains(
            report.Steps,
            step => step.StepId == "security" && step.Status == LaunchStepStatus.Warning);
    }

    [Fact]
    public async Task DecliningThePromptStopsTheLaunchAndRecordsNothing()
    {
        using var environment = new TestEnvironment();
        var security = new FakeSecurity(ThreatLevel.HighRisk);
        var processLauncher = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IThreatProtectionService>(security);
            services.AddSingleton<IProcessLauncher>(processLauncher);
        });
        var instance = await PrepareLaunchableInstanceAsync(environment, provider);

        var prompted = 0;
        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(
            instance.Id,
            dryRun: false,
            acceptJavaRisk: false,
            confirmSecurityRisk: (_, _) =>
            {
                prompted++;
                return Task.FromResult(false);
            });

        Assert.False(report.Success);
        Assert.Equal(1, prompted);
        Assert.Equal(0, processLauncher.Calls);
        Assert.Empty(security.Accepted);

        var step = Assert.Single(report.Steps, item => item.StepId == "security");
        Assert.Equal(LaunchStepStatus.Failed, step.Status);
        // 拒绝后必须告诉用户去哪里处理，而不是只丢一条失败。
        Assert.Contains("安全中心", step.Message);
        Assert.DoesNotContain("--accept-security-risk", step.Message);
    }

    [Fact]
    public async Task WithoutAnInteractiveChannelTheFailureNamesTheExplicitFlag()
    {
        using var environment = new TestEnvironment();
        var security = new FakeSecurity(ThreatLevel.ConfirmedThreat);
        var processLauncher = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IThreatProtectionService>(security);
            services.AddSingleton<IProcessLauncher>(processLauncher);
        });
        var instance = await PrepareLaunchableInstanceAsync(environment, provider);

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(instance.Id, dryRun: false);

        Assert.False(report.Success);
        Assert.Equal(0, processLauncher.Calls);
        Assert.Empty(security.Accepted);
        var step = Assert.Single(report.Steps, item => item.StepId == "security");
        Assert.Equal(LaunchStepStatus.Failed, step.Status);
        Assert.Contains("--accept-security-risk", step.Message);
        Assert.Contains("安全中心", step.Message);
    }

    [Fact]
    public async Task ExplicitAcceptanceSkipsThePromptButStillRecordsTheRisk()
    {
        using var environment = new TestEnvironment();
        var security = new FakeSecurity(ThreatLevel.Suspicious);
        var processLauncher = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IThreatProtectionService>(security);
            services.AddSingleton<IProcessLauncher>(processLauncher);
        });
        var instance = await PrepareLaunchableInstanceAsync(environment, provider);

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(
            instance.Id,
            dryRun: false,
            acceptJavaRisk: false,
            acceptSecurityRisk: true,
            confirmSecurityRisk: (_, _) => throw new InvalidOperationException("显式接受时不应再弹窗。"));

        Assert.True(report.Success, FailureMessage(report));
        Assert.Equal(1, processLauncher.Calls);
        Assert.Single(security.Accepted);
    }

    [Fact]
    public async Task AFailingConsentChannelIsTreatedAsNotAccepted()
    {
        using var environment = new TestEnvironment();
        var security = new FakeSecurity(ThreatLevel.Suspicious);
        var processLauncher = new FakeProcessLauncher();
        await using var provider = environment.CreateServices(services =>
        {
            services.AddSingleton<IThreatProtectionService>(security);
            services.AddSingleton<IProcessLauncher>(processLauncher);
        });
        var instance = await PrepareLaunchableInstanceAsync(environment, provider);

        var report = await provider.GetRequiredService<ILaunchService>().LaunchAsync(
            instance.Id,
            dryRun: false,
            acceptJavaRisk: false,
            confirmSecurityRisk: (_, _) => throw new InvalidOperationException("弹窗宿主不可用。"));

        // 交互失败不等于用户接受：必须保持未启动且不写接受记录。
        Assert.False(report.Success);
        Assert.Equal(0, processLauncher.Calls);
        Assert.Empty(security.Accepted);
    }

    private static string FailureMessage(LaunchReport report) => string.Join(
        " | ",
        report.Steps.Select(step => $"{step.StepId}:{step.Status}:{step.Message}"));

    /// <summary>准备好可通过所有前置步骤的实例（假 Java、版本文件、资源索引与 EULA）。</summary>
    private static async Task<GameInstance> PrepareLaunchableInstanceAsync(
        TestEnvironment environment,
        IServiceProvider provider)
    {
        var created = await provider.GetRequiredService<IInstanceService>()
            .CreateAsync(InstanceName, "1.21.1");
        Assert.True(created.IsSuccess, created.Error?.ToString());
        var instance = created.Value!;
        await provider.GetRequiredService<IAccountService>().AddOfflineAsync("Player");

        var fakeJavaHome = Path.Combine(environment.Root, "fakejava");
        var fakeJavaBin = Path.Combine(fakeJavaHome, "bin");
        Directory.CreateDirectory(fakeJavaBin);
        var fakeJava = Path.Combine(fakeJavaBin, OperatingSystem.IsWindows() ? "java.exe" : "java");
        await File.WriteAllTextAsync(fakeJava, string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(fakeJavaHome, "release"),
            "JAVA_VERSION=\"21.0.1\"\nIMPLEMENTOR=\"ECL Test Runtime\"\nOS_ARCH=\"x86_64\"\n");

        var settings = provider.GetRequiredService<ISettingsService>();
        Assert.True(settings.TrySetRaw("java.defaultPath", fakeJava, null, out var javaError), javaError);
        Assert.True(provider.GetRequiredService<IEulaService>().Accept().IsSuccess);

        var versionDirectory = Path.Combine(environment.Paths.VersionsDirectory, instance.VersionId);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(versionDirectory, $"{instance.VersionId}.json"),
            """
            {
              "id": "1.21.1",
              "type": "release",
              "mainClass": "net.minecraft.client.main.Main",
              "assetIndex": { "id": "1.21" },
              "libraries": []
            }
            """);
        await File.WriteAllBytesAsync(
            Path.Combine(versionDirectory, $"{instance.VersionId}.jar"),
            []);

        var assetIndexDirectory = Path.Combine(environment.Paths.AssetsDirectory, "indexes");
        Directory.CreateDirectory(assetIndexDirectory);
        await File.WriteAllTextAsync(Path.Combine(assetIndexDirectory, "1.21.json"), """{ "objects": {} }""");
        return instance;
    }

    private sealed class FakeProcessLauncher : IProcessLauncher
    {
        public int Calls { get; private set; }

        public int Start(
            LaunchCommand command,
            string workingDirectory,
            bool showConsole,
            string instanceId,
            string? sessionId = null)
        {
            Calls++;
            return 4242;
        }
    }

    /// <summary>启动门控替身：在用户接受前始终拦截，并记录接受调用。</summary>
    private sealed class FakeSecurity(ThreatLevel level) : IThreatProtectionService
    {
        private readonly ThreatFinding _finding = new(
            "finding-1",
            "mods/evil.jar",
            "sha-1",
            level,
            [new ThreatEvidence("file.executable", "2026-11-03", level, "可执行内容")],
            "请确认来源");

        public List<IReadOnlyList<ThreatFinding>> Accepted { get; } = [];

        public Task<Result<LaunchSecurityDecision>> EvaluateLaunchAsync(
            string instanceId,
            IReadOnlyList<string>? launchArguments = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ThreatFinding> findings = [_finding];
            var decision = new LaunchSecurityDecision(
                false,
                true,
                findings,
                "检测到 1 项需要确认的风险；本次启动已暂停。",
                false);
            return Task.FromResult(Result<LaunchSecurityDecision>.Ok(decision));
        }

        public Task<Result> AcceptRiskAsync(
            string instanceId,
            IReadOnlyList<ThreatFinding> findings,
            CancellationToken cancellationToken = default)
        {
            Accepted.Add(findings);
            return Task.FromResult(Result.Ok());
        }

        public Task<Result<ThreatScanReport>> ScanAsync(
            string instanceId,
            SecurityScanOptions? options = null,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<ThreatScanReport?>> GetLatestScanAsync(
            string instanceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<QuarantineRecord>> QuarantineAsync(
            string instanceId,
            string relativePath,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> RestoreQuarantineAsync(
            string instanceId,
            string quarantineId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<QuarantineRecord>>> ListQuarantineAsync(
            string instanceId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
