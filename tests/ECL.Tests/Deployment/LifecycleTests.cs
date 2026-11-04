// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class LifecycleTests
{
    [Fact]
    public async Task InstallManifestRoundTripsAndRejectsTraversal()
    {
        using var directory = new TempDirectory();
        var root = Path.Combine(directory.Path, "install");
        CreateInstalledTree(root);
        var manifest = InstallManifestStore.Create(root, "1.0.0");
        Assert.Contains(manifest.Files, file => file.Path == "versions/1.0.0/ECL.exe");
        var written = await InstallManifestStore.WriteAsync(root, manifest);
        Assert.True(written.IsSuccess);

        var read = InstallManifestStore.Read(root);
        Assert.True(read.IsSuccess);
        Assert.True(read.Value!.IsValid);

        var path = InstallManifestStore.GetPath(root);
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("\"current.json\"", "\"../outside.json\"", StringComparison.Ordinal));
        var corrupted = InstallManifestStore.Read(root);
        Assert.False(corrupted.Value!.IsValid);
    }

    [Fact]
    public async Task UninstallerDeletesOnlyManifestProgramFilesAndKeepsUninstallerAndUserData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var root = Path.Combine(directory.Path, "install");
        CreateInstalledTree(root);
        var userData = Path.Combine(directory.Path, "user-data");
        Directory.CreateDirectory(userData);
        await File.WriteAllTextAsync(Path.Combine(userData, "world.dat"), "keep");
        var manifest = InstallManifestStore.Create(root, "1.0.0");
        Assert.True((await InstallManifestStore.WriteAsync(root, manifest)).IsSuccess);
        var registration = new UninstallRegistration(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\EnderCraftLauncher.Tests." + Guid.NewGuid().ToString("N"));
        Assert.True(registration.Register(root, "1.0.0").IsSuccess);
        Assert.True(registration.IsRegistered());

        var result = UninstallService.Run(root, registration);

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.False(File.Exists(Path.Combine(root, "current.json")));
        Assert.False(File.Exists(Path.Combine(root, "versions", "1.0.0", "ECL.exe")));
        Assert.True(File.Exists(Path.Combine(root, "ECL.Uninstaller.exe")));
        Assert.True(File.Exists(Path.Combine(userData, "world.dat")));
        Assert.False(registration.IsRegistered());
    }

    [Fact]
    public async Task InstallerIntegrityDetectsMatchMismatchAndMissingRecord()
    {
        using var directory = new TempDirectory();
        var exe = Path.Combine(directory.Path, "ECL.Installer.exe");
        await File.WriteAllBytesAsync(exe, [1, 2, 3, 4, 5]);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(exe))).ToLowerInvariant();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, InstallerIntegrity.ChecksumFileName),
            JsonSerializer.Serialize(new { file = "ECL.Installer.exe", version = "1.0.0", sha256 = hash }));

        var confirmed = await InstallerIntegrity.InspectAsync(directory.Path, exe);
        Assert.Equal(InstallerIntegrityState.Confirmed, confirmed.State);
        Assert.Equal(hash, confirmed.OfficialSha256);

        await File.WriteAllBytesAsync(exe, [9, 9, 9]);
        var mismatch = await InstallerIntegrity.InspectAsync(directory.Path, exe);
        Assert.Equal(InstallerIntegrityState.Mismatch, mismatch.State);

        File.Delete(Path.Combine(directory.Path, InstallerIntegrity.ChecksumFileName));
        var unavailable = await InstallerIntegrity.InspectAsync(directory.Path, exe);
        Assert.Equal(InstallerIntegrityState.Unavailable, unavailable.State);
    }

    [Fact]
    public void CodeSigningTrustUsesPinnedSha256AndDoesNotSilentlyTrust()
    {
        using var directory = new TempDirectory();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=ECL Test Code Signing",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var certificatePath = Path.Combine(directory.Path, "test.cer");
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Cert));
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        File.WriteAllText(
            Path.Combine(directory.Path, CodeSigningTrustService.TrustFileName),
            JsonSerializer.Serialize(new
            {
                certificate = "test.cer",
                subject = certificate.Subject,
                sha256Fingerprint = fingerprint,
            }));

        var inspected = new CodeSigningTrustService(directory.Path).Inspect();
        Assert.True(inspected.IsSuccess);
        Assert.Equal(fingerprint, inspected.Value!.Sha256Fingerprint);
        Assert.False(inspected.Value.IsTrusted);

        File.WriteAllText(
            Path.Combine(directory.Path, CodeSigningTrustService.TrustFileName),
            JsonSerializer.Serialize(new
            {
                certificate = "test.cer",
                subject = certificate.Subject,
                sha256Fingerprint = new string('0', 64),
            }));
        Assert.False(new CodeSigningTrustService(directory.Path).Inspect().IsSuccess);
    }

    private static void CreateInstalledTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "versions", "1.0.0"));
        File.WriteAllText(Path.Combine(root, "current.json"), """{"version":"1.0.0","executable":"ECL.exe"}""");
        File.WriteAllText(Path.Combine(root, "update-trust.json"), "{}");
        File.WriteAllText(Path.Combine(root, "ECL.Bootstrapper.exe"), "bootstrapper");
        File.WriteAllText(Path.Combine(root, "ECL.Uninstaller.exe"), "uninstaller");
        File.WriteAllText(Path.Combine(root, "versions", "1.0.0", "ECL.exe"), "app");
        File.WriteAllText(Path.Combine(root, "versions", "1.0.0", "ECL.dll"), "dll");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecl-lifecycle-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
