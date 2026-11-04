// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Security;

public sealed class ThreatProtectionTests
{
    [Fact]
    public async Task ScanReportsArchiveTraversalAndScriptPayload()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var security = provider.GetRequiredService<IThreatProtectionService>();
        var created = (await instances.CreateAsync("Threat", "1.20.1")).Value!;

        var mods = Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "mods");
        Directory.CreateDirectory(mods);
        using (var archive = ZipFile.Open(Path.Combine(mods, "bad.jar"), ZipArchiveMode.Create))
        {
            var traversal = archive.CreateEntry("../outside.ps1");
            await using (var stream = traversal.Open())
            {
                await stream.WriteAsync("Write-Host pwned"u8.ToArray());
            }

            var script = archive.CreateEntry("payload.ps1");
            await using (var stream = script.Open())
            {
                await stream.WriteAsync("powershell -Command whoami"u8.ToArray());
            }
        }

        var result = await security.ScanAsync(created.Id);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.True(report.Findings.Count >= 1);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("mods/bad.jar", finding.RelativePath);
        Assert.True(finding.Level >= ThreatLevel.Suspicious);
        Assert.Contains(finding.Evidence, item => item.RuleId == "archive.path_traversal");

        var latest = await security.GetLatestScanAsync(created.Id);
        Assert.True(latest.IsSuccess);
        Assert.Equal(report.ScanId, latest.Value!.ScanId);
    }

    [Fact]
    public async Task LaunchGateBlocksThenAcceptsRiskUntilContentChanges()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var security = provider.GetRequiredService<IThreatProtectionService>();
        var created = (await instances.CreateAsync("Gate", "1.20.1")).Value!;

        var mods = Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "mods");
        Directory.CreateDirectory(mods);
        var malicious = Path.Combine(mods, "suspicious.jar");
        using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("setup.ps1");
            await using var stream = entry.Open();
            await stream.WriteAsync("cmd.exe /c calc"u8.ToArray());
        }

        var scan = await security.ScanAsync(created.Id);
        Assert.True(scan.IsSuccess, scan.Error?.ToString());

        var blocked = await security.EvaluateLaunchAsync(created.Id);
        Assert.True(blocked.IsSuccess, blocked.Error?.ToString());
        Assert.False(blocked.Value!.Allowed);
        Assert.True(blocked.Value.RequiresAcknowledgement);

        var accepted = await security.AcceptRiskAsync(created.Id, blocked.Value.Findings);
        Assert.True(accepted.IsSuccess, accepted.Error?.ToString());

        var allowed = await security.EvaluateLaunchAsync(created.Id);
        Assert.True(allowed.Value!.Allowed);
        Assert.True(allowed.Value.AlreadyAccepted);

        // 内容变化后旧接受记录不得覆盖新结论。
        using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry("second.ps1");
            await using var stream = entry.Open();
            await stream.WriteAsync("powershell"u8.ToArray());
        }

        await security.ScanAsync(created.Id);
        var reblocked = await security.EvaluateLaunchAsync(created.Id);
        Assert.False(reblocked.Value!.Allowed);
    }

    [Fact]
    public async Task QuarantineIsRecoverableThroughTransaction()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var security = provider.GetRequiredService<IThreatProtectionService>();
        var created = (await instances.CreateAsync("Quarantine", "1.20.1")).Value!;

        var mods = Path.Combine(environment.Paths.GetInstanceGameDirectory(created.Id), "mods");
        Directory.CreateDirectory(mods);
        var file = Path.Combine(mods, "suspect.jar");
        await File.WriteAllTextAsync(file, "not-a-real-jar");
        var original = await File.ReadAllBytesAsync(file);

        var quarantined = await security.QuarantineAsync(created.Id, "mods/suspect.jar");
        Assert.True(quarantined.IsSuccess, quarantined.Error?.ToString());
        Assert.False(File.Exists(file));

        var listed = await security.ListQuarantineAsync(created.Id);
        Assert.True(listed.IsSuccess);
        Assert.Single(listed.Value!);

        var restored = await security.RestoreQuarantineAsync(
            created.Id,
            quarantined.Value!.QuarantineId);
        Assert.True(restored.IsSuccess, restored.Error?.ToString());
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
        var after = await security.ListQuarantineAsync(created.Id);
        Assert.Empty(after.Value!);
    }

    [Fact]
    public async Task UnknownJavaAgentArgumentIsFlaggedAtLaunch()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var security = provider.GetRequiredService<IThreatProtectionService>();
        var created = (await instances.CreateAsync("Args", "1.20.1")).Value!;

        var decision = await security.EvaluateLaunchAsync(
            created.Id,
            ["-javaagent:C:\\temp\\unknown-agent.jar"]);
        Assert.True(decision.IsSuccess, decision.Error?.ToString());
        Assert.False(decision.Value!.Allowed);
        Assert.Contains(decision.Value.Findings, finding =>
            finding.Evidence.Any(item => item.RuleId == "launch.unknown_javaagent"));
    }

    [Fact]
    public async Task CleanInstanceWithoutScanIsNotFalselyBlocked()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();
        var instances = provider.GetRequiredService<IInstanceService>();
        var security = provider.GetRequiredService<IThreatProtectionService>();
        var created = (await instances.CreateAsync("Clean", "1.20.1")).Value!;

        var decision = await security.EvaluateLaunchAsync(created.Id);
        Assert.True(decision.IsSuccess, decision.Error?.ToString());
        Assert.True(decision.Value!.Allowed);
        Assert.Contains("尚未执行安全扫描", decision.Value.Message);
    }
}
