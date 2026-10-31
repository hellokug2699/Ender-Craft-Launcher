// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using Ecl.Minecraft.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class DiagnosticPackageTests
{
    [Fact]
    public async Task ExportsRedactedDiagnosticsWithoutAccounts()
    {
        using var environment = new TestEnvironment();
        await using var provider = environment.CreateServices();

        var created = await provider.GetRequiredService<Ecl.Minecraft.Instances.IInstanceService>()
            .CreateAsync("Diagnostics", "1.21.1");
        Assert.True(created.IsSuccess);
        var instance = created.Value!;

        var gameDirectory = environment.Paths.GetInstanceGameDirectory(instance.Id);
        Directory.CreateDirectory(Path.Combine(gameDirectory, "logs"));
        File.WriteAllText(
            Path.Combine(gameDirectory, "logs", "latest.log"),
            "accessToken=super-secret\nhttps://user:pass@example.com/path\nnormal line\n");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "crash-reports"));
        File.WriteAllText(Path.Combine(gameDirectory, "crash-reports", "crash-1.txt"), "Description: boom");

        var output = Path.Combine(environment.Root, "diag.zip");
        var result = await provider.GetRequiredService<IDiagnosticPackageService>()
            .ExportAsync(instance.Id, output);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(File.Exists(output));

        using var archive = ZipFile.OpenRead(output);
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        var logEntry = archive.GetEntry("minecraft/logs/latest.log");
        Assert.NotNull(logEntry);
        using var reader = new StreamReader(logEntry!.Open());
        var text = await reader.ReadToEndAsync();
        Assert.DoesNotContain("super-secret", text);
        Assert.DoesNotContain("user:pass", text);
        Assert.Contains("***", text);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("accounts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RedactsCommonTokenForms()
    {
        var text = DiagnosticPackageService.Redact(
            "accessToken=abc\nAuthorization: Bearer xyz\n--accessToken abc\nhttps://u:p@example.com/\n" +
            "Failed to parse into SignedJWT: N4lAb6YQ4jn34yFclZWCTo0RWsgMoo235JiV-8JMH0w");

        Assert.DoesNotContain("abc", text);
        Assert.DoesNotContain("xyz", text);
        Assert.DoesNotContain("u:p@", text);
        // 第三方（Yggdrasil）token 不是 JWT，也必须脱敏。
        Assert.DoesNotContain("N4lAb6YQ4jn34yFclZWCTo0RWsgMoo235JiV-8JMH0w", text);
        Assert.Contains("SignedJWT: ***", text);
    }
}
