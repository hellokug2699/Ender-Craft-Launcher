// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

/// <summary>ADR 0034：自身完整性校验、本地恢复材料与 Repair Mode 的边界。</summary>
public sealed class SelfIntegrityTests
{
    [Fact]
    public async Task SignedInstallVerifiesAllFiles()
    {
        using var fixture = new IntegrityFixture();
        var result = await new SelfIntegrityVerifier().VerifyAsync(fixture.InstallRoot);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var report = result.Value!;
        Assert.True(report.ContentIntact);
        Assert.True(report.VersionMatchesManifest);
        Assert.Equal(SelfIntegrityTrustStatus.Verified, report.Trust);
        Assert.True(report.IsHealthy, report.Describe());
        Assert.All(report.Files, file => Assert.Equal(SelfIntegrityFileStatus.Ok, file.Status));
    }

    [Fact]
    public async Task MissingFileIsDistinguishedFromHashMismatch()
    {
        using var fixture = new IntegrityFixture();
        File.Delete(Path.Combine(fixture.VersionDirectory, "ECL.dll"));
        var missing = (await new SelfIntegrityVerifier().VerifyAsync(fixture.InstallRoot)).Value!;

        Assert.Contains(missing.Files, file => file.Path == "ECL.dll" && file.Status == SelfIntegrityFileStatus.Missing);
        Assert.False(missing.ContentIntact);

        File.WriteAllText(Path.Combine(fixture.VersionDirectory, "ECL.dll"), "tampered!!");
        var corrupt = (await new SelfIntegrityVerifier().VerifyAsync(fixture.InstallRoot)).Value!;
        Assert.Contains(corrupt.Files, file => file.Path == "ECL.dll" && file.Status == SelfIntegrityFileStatus.HashMismatch);
    }

    [Fact]
    public async Task LightweightCheckOnlyDetectsSizeChanges()
    {
        using var fixture = new IntegrityFixture();
        // 长度不变的内容替换：轻量检查通过，全量哈希检查失败。
        File.WriteAllText(Path.Combine(fixture.VersionDirectory, "ECL.dll"), new string('x', "dll-binary".Length));

        var light = (await new SelfIntegrityVerifier().VerifyAsync(fixture.InstallRoot, verifyHashes: false)).Value!;
        var full = (await new SelfIntegrityVerifier().VerifyAsync(fixture.InstallRoot, verifyHashes: true)).Value!;

        Assert.True(light.ContentIntact);
        Assert.False(full.ContentIntact);
    }

    [Fact]
    public async Task RepairRestoresDamagedFilesFromVerifiedCache()
    {
        using var fixture = new IntegrityFixture();
        var current = (await fixture.Layout.ReadCurrentAsync()).Value!;
        var cache = new RecoveryCache(Path.Combine(fixture.InstallRoot, "recovery"));
        Assert.True((await cache.CaptureAsync(fixture.InstallRoot, current)).IsSuccess);

        File.Delete(Path.Combine(fixture.VersionDirectory, "ECL.dll"));
        File.WriteAllText(Path.Combine(fixture.VersionDirectory, "ECL.exe"), "broken");

        var repair = await new SelfRepairService(cache: cache).RepairAsync(fixture.InstallRoot);

        Assert.True(repair.IsSuccess, repair.Error?.ToString());
        Assert.True(repair.Value!.Success, string.Join("；", repair.Value.ResidualIssues));
        Assert.Contains("ECL.dll", repair.Value.RestoredFiles);
        Assert.Contains("ECL.exe", repair.Value.RestoredFiles);
        Assert.Equal("dll-binary", File.ReadAllText(Path.Combine(fixture.VersionDirectory, "ECL.dll")));
        Assert.True(repair.Value.After!.IsHealthy);
    }

    [Fact]
    public async Task RepairWithoutCacheFailsExplicitlyInsteadOfUsingUnverifiedFiles()
    {
        using var fixture = new IntegrityFixture();
        File.Delete(Path.Combine(fixture.VersionDirectory, "ECL.dll"));

        var repair = await new SelfRepairService(
                cache: new RecoveryCache(Path.Combine(fixture.InstallRoot, "recovery")))
            .RepairAsync(fixture.InstallRoot);

        Assert.False(repair.IsSuccess);
        Assert.Equal("integrity.repair_unavailable", repair.Error!.Code);
        Assert.False(File.Exists(Path.Combine(fixture.VersionDirectory, "ECL.dll")));
    }

    [Fact]
    public async Task InvalidSignaturePreventsRepairAndIsReportedSeparately()
    {
        using var fixture = new IntegrityFixture();
        var current = (await fixture.Layout.ReadCurrentAsync()).Value!;
        var cache = new RecoveryCache(Path.Combine(fixture.InstallRoot, "recovery"));
        Assert.True((await cache.CaptureAsync(fixture.InstallRoot, current)).IsSuccess);
        File.Delete(Path.Combine(fixture.VersionDirectory, "ECL.dll"));
        File.WriteAllText(Path.Combine(fixture.VersionDirectory, "update-manifest.sig"), "not-json");

        var verification = (await new SelfIntegrityVerifier().VerifyAsync(fixture.InstallRoot)).Value!;
        Assert.Equal(SelfIntegrityTrustStatus.SignatureInvalid, verification.Trust);
        Assert.False(verification.ContentIntact);

        var repair = await new SelfRepairService(cache: cache).RepairAsync(fixture.InstallRoot);
        Assert.False(repair.IsSuccess);
        Assert.Equal("integrity.repair_untrusted", repair.Error!.Code);
    }

    [Fact]
    public async Task CacheRefusesUntrustedSource()
    {
        using var fixture = new IntegrityFixture();
        File.Delete(Path.Combine(fixture.InstallRoot, SelfIntegrityVerifier.TrustFileName));
        var current = (await fixture.Layout.ReadCurrentAsync()).Value!;

        var capture = await new RecoveryCache(Path.Combine(fixture.InstallRoot, "recovery"))
            .CaptureAsync(fixture.InstallRoot, current);

        Assert.False(capture.IsSuccess);
        Assert.Equal("integrity.cache_source_untrusted", capture.Error!.Code);
    }

    [Fact]
    public async Task RepairDoesNotTouchWorkspaceOrUserData()
    {
        using var fixture = new IntegrityFixture();
        var current = (await fixture.Layout.ReadCurrentAsync()).Value!;
        var cache = new RecoveryCache(Path.Combine(fixture.InstallRoot, "recovery"));
        Assert.True((await cache.CaptureAsync(fixture.InstallRoot, current)).IsSuccess);

        var userData = Path.Combine(fixture.Root, "user-data");
        Directory.CreateDirectory(Path.Combine(userData, "instances", "demo", "minecraft", "saves", "world"));
        File.WriteAllText(Path.Combine(userData, "instances", "demo", "minecraft", "saves", "world", "level.dat"), "keep");
        File.WriteAllText(Path.Combine(userData, "accounts.json"), "{}");
        var before = Snapshot(userData);

        File.Delete(Path.Combine(fixture.VersionDirectory, "ECL.dll"));
        var repair = await new SelfRepairService(cache: cache).RepairAsync(fixture.InstallRoot);

        Assert.True(repair.Value!.Success);
        Assert.Equal(before, Snapshot(userData));
    }

    [Fact]
    public void RecoveryCacheKeepsBoundedVersionCount()
    {
        using var directory = new TempDirectory();
        var cache = new RecoveryCache(Path.Combine(directory.Path, "recovery"), maxVersions: 1);
        var older = Path.Combine(cache.VersionsDirectory, "1.0.0");
        var newer = Path.Combine(cache.VersionsDirectory, "1.0.1");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        Directory.SetCreationTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        Directory.SetCreationTimeUtc(newer, DateTime.UtcNow);

        var removed = cache.Prune();

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(older));
        Assert.True(Directory.Exists(newer));
    }

    private static string Snapshot(string root) => string.Join(
        "\n",
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{Path.GetRelativePath(root, path)}|{new FileInfo(path).Length}|{UpdateHash.ComputeSha256(File.ReadAllBytes(path))}"));

    private sealed class IntegrityFixture : IDisposable
    {
        private static readonly (string Path, string Content)[] Files =
        [
            ("ECL.exe", "app-binary"),
            ("ECL.dll", "dll-binary"),
            ("sub/module.dll", "module-binary"),
        ];

        public IntegrityFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "ecl-integrity-tests", Guid.NewGuid().ToString("N"));
            InstallRoot = Path.Combine(Root, "install");
            Layout = new UpdateLayout(InstallRoot);
            Layout.EnsureCreated();
            VersionDirectory = Layout.GetVersionDirectory(ReleaseVersion.Parse("1.0.0"));
            Directory.CreateDirectory(VersionDirectory);
            foreach (var (path, content) in Files)
            {
                var full = Path.Combine(VersionDirectory, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
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
            var manifestBytes = DeploymentTestData.Serialize(manifest);
            var (privateKey, _, trustJson, keyId) = DeploymentTestData.CreateKeys();
            var signature = DeploymentTestData.Sign(manifestBytes, privateKey, keyId);

            File.WriteAllBytes(Path.Combine(VersionDirectory, SelfIntegrityVerifier.ManifestFileName), manifestBytes);
            File.WriteAllText(Path.Combine(VersionDirectory, SelfIntegrityVerifier.SignatureFileName), signature);
            File.WriteAllBytes(Path.Combine(InstallRoot, SelfIntegrityVerifier.ManifestFileName), manifestBytes);
            File.WriteAllText(Path.Combine(InstallRoot, SelfIntegrityVerifier.SignatureFileName), signature);
            File.WriteAllText(Path.Combine(InstallRoot, SelfIntegrityVerifier.TrustFileName), trustJson);

            var write = Layout.WriteCurrentAsync(new CurrentVersion
            {
                Version = "1.0.0",
                Executable = "ECL.exe",
                ParsedVersion = ReleaseVersion.Parse("1.0.0"),
                InstalledAt = DateTimeOffset.UtcNow,
            });
            if (!write.GetAwaiter().GetResult().IsSuccess)
            {
                throw new InvalidOperationException("无法写入测试 current.json。");
            }
        }

        public string Root { get; }

        public string InstallRoot { get; }

        public UpdateLayout Layout { get; }

        public string VersionDirectory { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecl-integrity-tests", Guid.NewGuid().ToString("N"));
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
            }
        }
    }
}
