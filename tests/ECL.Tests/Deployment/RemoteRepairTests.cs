// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.Core;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

/// <summary>ADR 0034 §3：本地材料不足时，从受信任更新源获取当前版本完整包完成 Repair Mode。</summary>
public sealed class RemoteRepairTests
{
    [Fact]
    public async Task RemoteRepairRestoresDamagedFileWithoutTouchingUserData()
    {
        using var fixture = new RepairFixture();
        var userData = Path.Combine(fixture.Root, "user-data", "saves", "world");
        Directory.CreateDirectory(userData);
        await File.WriteAllTextAsync(Path.Combine(userData, "level.dat"), "keep");
        File.Delete(Path.Combine(fixture.VersionDirectory, "ECL.dll"));

        var service = new SelfRepairService(
            cache: new RecoveryCache(Path.Combine(fixture.InstallRoot, "recovery")),
            remoteSource: new FakeRepairSource(fixture.Package));

        var result = await service.RepairAsync(fixture.InstallRoot);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(result.Value!.Success, string.Join("；", result.Value.ResidualIssues));
        Assert.Contains("ECL.dll", result.Value.RestoredFiles);
        Assert.Equal("dll-binary", await File.ReadAllTextAsync(Path.Combine(fixture.VersionDirectory, "ECL.dll")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(userData, "level.dat")));
        Assert.True(result.Value.After!.IsHealthy);
    }

    [Fact]
    public async Task RemoteRepairRejectsWrongVersionAndTamperedPackage()
    {
        using var fixture = new RepairFixture();
        File.Delete(Path.Combine(fixture.VersionDirectory, "ECL.dll"));

        var wrongVersion = new FakeRepairSource(
            fixture.Package with
            {
                Manifest = fixture.Package.Manifest with { Version = ReleaseVersion.Parse("1.0.1") },
            })
        {
            ReturnRegardlessOfVersion = true,
        };
        var wrong = await new RemoteRepairService(wrongVersion).RepairAsync(
            fixture.InstallRoot,
            ["ECL.dll"]);
        Assert.Equal("repair.version_unavailable", wrong.Error?.Code);

        var tampered = fixture.CreateTamperedPackage();
        var bad = await new RemoteRepairService(new FakeRepairSource(tampered)).RepairAsync(
            fixture.InstallRoot,
            ["ECL.dll"]);
        Assert.False(bad.IsSuccess);
        Assert.Equal("update.file_hash_mismatch", bad.Error?.Code);
        Assert.False(File.Exists(Path.Combine(fixture.VersionDirectory, "ECL.dll")));
    }

    [Fact]
    public async Task RemoteRepairRefusesUntrustedInstall()
    {
        using var fixture = new RepairFixture();
        File.Delete(Path.Combine(fixture.InstallRoot, SelfIntegrityVerifier.TrustFileName));
        File.Delete(Path.Combine(fixture.VersionDirectory, "ECL.dll"));

        var result = await new RemoteRepairService(new FakeRepairSource(fixture.Package))
            .RepairAsync(fixture.InstallRoot, ["ECL.dll"]);

        Assert.False(result.IsSuccess);
        Assert.Equal("integrity.repair_untrusted", result.Error?.Code);
    }

    private sealed class FakeRepairSource(RepairPackage package) : IRepairPackageSource
    {
        public bool ReturnRegardlessOfVersion { get; init; }

        public Task<Result<RepairPackage>> AcquireAsync(
            ReleaseVersion version,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                ReturnRegardlessOfVersion || version.CompareTo(package.Manifest.Version) == 0
                    ? Result<RepairPackage>.Ok(package)
                    : Result<RepairPackage>.Fail(
                        "repair.version_unavailable",
                        "更新源没有提供当前版本的修复包。"));
    }

    private sealed class RepairFixture : IDisposable
    {
        private static readonly (string Path, string Content)[] Files =
        [
            ("ECL.exe", "app-binary"),
            ("ECL.dll", "dll-binary"),
        ];

        private readonly string _privateKey;
        private readonly string _keyId;
        private readonly byte[] _manifestBytes;

        public RepairFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "ecl-remote-repair-tests", Guid.NewGuid().ToString("N"));
            InstallRoot = Path.Combine(Root, "install");
            var layout = new UpdateLayout(InstallRoot);
            layout.EnsureCreated();
            VersionDirectory = layout.GetVersionDirectory(ReleaseVersion.Parse("1.0.0"));
            Directory.CreateDirectory(VersionDirectory);
            foreach (var (path, content) in Files)
            {
                File.WriteAllText(Path.Combine(VersionDirectory, path), content);
            }

            var manifest = DeploymentTestData.CreateManifest(
                "1.0.0",
                "ECL-1.0.0.zip",
                1024,
                new string('a', 64),
                Files.Select(file => new UpdateFile(
                    file.Path,
                    Encoding.UTF8.GetByteCount(file.Content),
                    UpdateHash.ComputeSha256(Encoding.UTF8.GetBytes(file.Content)))).ToArray());
            _manifestBytes = DeploymentTestData.Serialize(manifest);
            var keys = DeploymentTestData.CreateKeys();
            _privateKey = keys.PrivateKeyPem;
            _keyId = keys.KeyId;
            var signature = DeploymentTestData.Sign(_manifestBytes, _privateKey, _keyId);

            File.WriteAllBytes(Path.Combine(VersionDirectory, SelfIntegrityVerifier.ManifestFileName), _manifestBytes);
            File.WriteAllText(Path.Combine(VersionDirectory, SelfIntegrityVerifier.SignatureFileName), signature);
            File.WriteAllBytes(Path.Combine(InstallRoot, SelfIntegrityVerifier.ManifestFileName), _manifestBytes);
            File.WriteAllText(Path.Combine(InstallRoot, SelfIntegrityVerifier.SignatureFileName), signature);
            File.WriteAllText(Path.Combine(InstallRoot, SelfIntegrityVerifier.TrustFileName), keys.TrustJson);

            var write = layout.WriteCurrentAsync(new CurrentVersion
            {
                Version = "1.0.0",
                Executable = "ECL.exe",
                ParsedVersion = ReleaseVersion.Parse("1.0.0"),
                InstalledAt = DateTimeOffset.UtcNow,
            });
            Assert.True(write.GetAwaiter().GetResult().IsSuccess);

            var packagePath = Path.Combine(Root, "ECL-1.0.0.zip");
            DeploymentTestData.CreatePackage(
                packagePath,
                Files.ToDictionary(file => file.Path, file => Encoding.UTF8.GetBytes(file.Content)));
            Package = new RepairPackage(manifest, packagePath, OwnsFile: false);
        }

        public string Root { get; }

        public string InstallRoot { get; }

        public string VersionDirectory { get; }

        public RepairPackage Package { get; }

        public RepairPackage CreateTamperedPackage()
        {
            var path = Path.Combine(Root, "tampered.zip");
            DeploymentTestData.CreatePackage(
                path,
                Files.ToDictionary(
                    file => file.Path,
                    file => Encoding.UTF8.GetBytes(file.Path == "ECL.dll" ? "tampered!!" : file.Content)));
            return new RepairPackage(Package.Manifest, path, OwnsFile: false);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
