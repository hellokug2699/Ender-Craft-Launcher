// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecl.Additions;
using Ecl.Core;
using Ecl.Deployment;
using Ecl.TestAddition;
using Ecl.Tests.Deployment;
using Xunit;

namespace Ecl.Tests.Additions;

/// <summary>ADR 0032：目录、依赖、安装事务、按需加载与卸载边界。</summary>
public sealed class AdditionTests
{
    [Fact]
    public void CatalogParserRejectsUnknownFieldsInsecurePackageAndDuplicates()
    {
        var valid = AdditionsTestData.CreateCatalogJson(AdditionsTestData.CreateEntry("ecl.alpha"));
        var parsed = AdditionCatalogParser.Parse(valid);
        Assert.True(parsed.IsSuccess, parsed.Error?.ToString());
        Assert.Single(parsed.Value!.Additions);

        var unknown = valid.Replace("\"additions\":", "\"unknownField\": true, \"additions\":", StringComparison.Ordinal);
        Assert.Equal("additions.catalog_invalid_json", AdditionCatalogParser.Parse(unknown).Error?.Code);

        var insecure = valid.Replace("https://example.invalid/", "http://example.invalid/", StringComparison.Ordinal);
        Assert.Equal("additions.catalog_package_invalid", AdditionCatalogParser.Parse(insecure).Error?.Code);

        var duplicate = AdditionCatalogParser.Serialize(
            new AdditionCatalog(
                AdditionCatalogParser.CurrentSchemaVersion,
                ReleaseVersion.Parse("1.0.0"),
                [
                    AdditionsTestData.CreateEntry("ecl.alpha"),
                    AdditionsTestData.CreateEntry("ecl.alpha"),
                ]));
        Assert.Equal("additions.catalog_duplicate_id", AdditionCatalogParser.Parse(duplicate).Error?.Code);

        var dataWithoutSchema = AdditionCatalogParser.Serialize(
            new AdditionCatalog(
                AdditionCatalogParser.CurrentSchemaVersion,
                ReleaseVersion.Parse("1.0.0"),
                [AdditionsTestData.CreateEntry("ecl.data", type: AdditionType.Data) with { DataSchemaVersion = null }]));
        Assert.Equal("additions.catalog_data_schema_missing", AdditionCatalogParser.Parse(dataWithoutSchema).Error?.Code);
    }

    [Fact]
    public void PlannerResolvesDependenciesInDependencyFirstOrder()
    {
        var catalog = AdditionsTestData.CreateCatalog(
            AdditionsTestData.CreateEntry("ecl.feature", dependencies: [Dep("ecl.data")]),
            AdditionsTestData.CreateEntry("ecl.data", type: AdditionType.Data, dataSchemaVersion: 1));

        var plan = new AdditionPlanner().Plan(
            catalog,
            [],
            [AdditionId.Parse("ecl.feature")],
            ReleaseVersion.Parse("1.0.0"),
            "windows");

        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.Equal(["ecl.data", "ecl.feature"], plan.Value!.Install.Select(item => item.Entry.Id.Value));
    }

    [Fact]
    public void PlannerRejectsCyclesMissingDependenciesAndCoreApiMismatch()
    {
        var cycle = AdditionsTestData.CreateCatalog(
            AdditionsTestData.CreateEntry("ecl.a", dependencies: [Dep("ecl.b")]),
            AdditionsTestData.CreateEntry("ecl.b", dependencies: [Dep("ecl.a")]));
        var planner = new AdditionPlanner();
        Assert.Equal(
            "additions.dependency_cycle",
            planner.Plan(cycle, [], [AdditionId.Parse("ecl.a")], ReleaseVersion.Parse("1.0.0"), "windows").Error?.Code);

        var missing = AdditionsTestData.CreateCatalog(
            AdditionsTestData.CreateEntry("ecl.a", dependencies: [Dep("ecl.absent")]));
        Assert.Equal(
            "additions.dependency_missing",
            planner.Plan(missing, [], [AdditionId.Parse("ecl.a")], ReleaseVersion.Parse("1.0.0"), "windows").Error?.Code);

        var incompatible = AdditionsTestData.CreateCatalog(
            AdditionsTestData.CreateEntry("ecl.a", coreMin: "2.0.0", coreMax: "3.0.0"));
        Assert.Equal(
            "additions.incompatible_core_api",
            planner.Plan(incompatible, [], [AdditionId.Parse("ecl.a")], ReleaseVersion.Parse("1.0.0"), "windows").Error?.Code);

        var wrongPlatform = AdditionsTestData.CreateCatalog(
            AdditionsTestData.CreateEntry("ecl.a", platforms: ["linux"]));
        Assert.Equal(
            "additions.platform_unsupported",
            planner.Plan(wrongPlatform, [], [AdditionId.Parse("ecl.a")], ReleaseVersion.Parse("1.0.0"), "windows").Error?.Code);
    }

    [Fact]
    public void PlannerKeepsInstalledDependencyThatSatisfiesRangeAndDetectsUpdates()
    {
        var catalog = AdditionsTestData.CreateCatalog(
            AdditionsTestData.CreateEntry("ecl.feature", version: "1.1.0", dependencies: [Dep("ecl.data", "1.0.0")]),
            AdditionsTestData.CreateEntry("ecl.data", type: AdditionType.Data, version: "1.0.5", dataSchemaVersion: 1));
        var installed = new[]
        {
            AdditionsTestData.CreateInstalled("ecl.data", "1.0.0", AdditionType.Data),
        };

        var plan = new AdditionPlanner().Plan(
            catalog,
            installed,
            [AdditionId.Parse("ecl.feature")],
            ReleaseVersion.Parse("1.0.0"),
            "windows");

        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.Equal(["ecl.feature"], plan.Value!.Install.Select(item => item.Entry.Id.Value));
        Assert.Empty(plan.Value.Update);
    }

    [Fact]
    public async Task PackageIsVerifiedAndInstalledTransactionally()
    {
        using var fixture = new AdditionFixture();
        var package = AdditionsTestData.CreatePackage(fixture.PackageDirectory, "ecl.demo", "1.0.0");
        var installer = fixture.CreateInstaller();

        var installed = await installer.InstallAsync(package.Entry);

        Assert.True(installed.IsSuccess, installed.Error?.ToString());
        var record = installed.Value!;
        Assert.True(record.Enabled);
        Assert.True(Directory.Exists(fixture.Installer.GetVersionDirectory(record.Id, record.Version)));
        var state = fixture.StateStore.Load();
        Assert.False(state.IsCorrupt);
        Assert.Single(state.Document.Additions);
        Assert.True(File.Exists(Path.Combine(record.Directory, "addition.json")));
    }

    [Fact]
    public async Task TamperedPackageAndUnexpectedFileAreRejectedWithoutStateChange()
    {
        using var fixture = new AdditionFixture();
        var package = AdditionsTestData.CreatePackage(fixture.PackageDirectory, "ecl.demo", "1.0.0");
        // 保持 zip 长度不变地篡改内容，验证安装器校验的是哈希而不是仅看大小。
        var bytes = await File.ReadAllBytesAsync(package.ZipPath);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(package.ZipPath, bytes);

        var installer = fixture.CreateInstaller();
        var tampered = await installer.InstallAsync(package.Entry);
        Assert.Equal("additions.package_hash_mismatch", tampered.Error?.Code);
        Assert.Empty(fixture.StateStore.Load().Document.Additions);

        var clean = AdditionsTestData.CreatePackage(fixture.PackageDirectory, "ecl.demo2", "1.0.0", extraFiles: new Dictionary<string, byte[]> { ["extra.dll"] = [1, 2, 3] });
        var unexpected = await installer.InstallAsync(clean.Entry);
        Assert.Equal("additions.package_unexpected_file", unexpected.Error?.Code);
        Assert.Empty(fixture.StateStore.Load().Document.Additions);
    }

    [Fact]
    public async Task UpdateKeepsPreviousVersionAndRollbackRestoresIt()
    {
        using var fixture = new AdditionFixture();
        var first = AdditionsTestData.CreatePackage(fixture.PackageDirectory, "ecl.demo", "1.0.0");
        var second = AdditionsTestData.CreatePackage(fixture.PackageDirectory, "ecl.demo", "1.0.1");
        var installer = fixture.CreateInstaller();

        Assert.True((await installer.InstallAsync(first.Entry)).IsSuccess);
        var updated = await installer.InstallAsync(second.Entry);
        Assert.True(updated.IsSuccess, updated.Error?.ToString());
        Assert.Equal("1.0.0", updated.Value!.PreviousVersion!.ToString());
        Assert.True(Directory.Exists(installer.GetVersionDirectory(first.Entry.Id, first.Entry.Version)));

        var rolledBack = await installer.RollbackAsync(second.Entry.Id);
        Assert.True(rolledBack.IsSuccess, rolledBack.Error?.ToString());
        Assert.Equal("1.0.0", rolledBack.Value!.Version.ToString());
        Assert.Equal("1.0.1", rolledBack.Value.PreviousVersion!.ToString());
    }

    [Fact]
    public async Task UninstallRefusesDependentsAndKeepsUserData()
    {
        using var fixture = new AdditionFixture();
        var data = AdditionsTestData.CreatePackage(
            fixture.PackageDirectory,
            "ecl.data",
            "1.0.0",
            dataSchemaVersion: 1);
        var feature = AdditionsTestData.CreatePackage(
            fixture.PackageDirectory,
            "ecl.feature",
            "1.0.0",
            dependencies: [Dep("ecl.data")]);
        var installer = fixture.CreateInstaller();

        Assert.True((await installer.InstallAsync(data.Entry)).IsSuccess);
        Assert.True((await installer.InstallAsync(feature.Entry)).IsSuccess);

        var refused = await installer.UninstallAsync(data.Entry.Id);
        Assert.Equal("additions.dependency_in_use", refused.Error?.Code);

        var userData = installer.DataDirectory(data.Entry.Id);
        Directory.CreateDirectory(userData);
        await File.WriteAllTextAsync(Path.Combine(userData, "keep.txt"), "keep");

        Assert.True((await installer.UninstallAsync(feature.Entry.Id)).IsSuccess);
        Assert.True((await installer.UninstallAsync(data.Entry.Id)).IsSuccess);
        Assert.True(File.Exists(Path.Combine(userData, "keep.txt")));
        Assert.False(Directory.Exists(installer.GetAdditionPackageDirectory(data.Entry.Id)));
    }

    [Fact]
    public async Task RuntimeLoadsModuleOnDemandIntoIsolatedDataDirectory()
    {
        using var fixture = new AdditionFixture();
        var package = AdditionsTestData.CreatePackage(fixture.PackageDirectory, "ecl.runtime", "1.0.0");
        var installer = fixture.CreateInstaller();
        Assert.True((await installer.InstallAsync(package.Entry)).IsSuccess);
        var installedState = fixture.StateStore.Load();
        Assert.True(
            installedState.Document.Additions.Count == 1,
            installedState.Error ?? File.ReadAllText(fixture.StateStore.Path));
        var runtime = new AdditionRuntime(
            fixture.StateStore,
            fixture.AdditionsRoot,
            ReleaseVersion.Parse("1.0.0"));
        var activated = await runtime.ActivateAsync(package.Entry.Id);

        Assert.True(activated.IsSuccess, activated.Error?.ToString());
        var dataDirectory = installer.DataDirectory(package.Entry.Id);
        Assert.True(File.Exists(Path.Combine(dataDirectory, "initialized.txt")));
        Assert.Contains(package.Entry.Id.Value, await File.ReadAllTextAsync(Path.Combine(dataDirectory, "initialized.txt")));
        Assert.Contains(runtime.Snapshot(), item => item.Id == package.Entry.Id && item.State == AdditionLoadState.Loaded);
        Assert.True((await runtime.DeactivateAsync(package.Entry.Id)).IsSuccess);
    }

    [Fact]
    public async Task RuntimeRefusesDisabledAndIncompatibleAdditions()
    {
        using var fixture = new AdditionFixture();
        var package = AdditionsTestData.CreatePackage(fixture.PackageDirectory, "ecl.runtime", "1.0.0");
        var installer = fixture.CreateInstaller();
        Assert.True((await installer.InstallAsync(package.Entry)).IsSuccess);
        Assert.True((await installer.SetEnabledAsync(package.Entry.Id, false)).IsSuccess);

        var runtime = new AdditionRuntime(
            fixture.StateStore,
            fixture.AdditionsRoot,
            ReleaseVersion.Parse("1.0.0"));
        Assert.Equal("additions.disabled", (await runtime.ActivateAsync(package.Entry.Id)).Error?.Code);

        Assert.True((await installer.SetEnabledAsync(package.Entry.Id, true)).IsSuccess);
        var incompatible = AdditionsTestData.CreatePackage(
            fixture.PackageDirectory,
            "ecl.newer",
            "1.0.0",
            coreMin: "2.0.0",
            coreMax: "3.0.0");
        Assert.True((await installer.InstallAsync(incompatible.Entry)).IsSuccess);
        Assert.Equal(
            "additions.incompatible_core_api",
            (await runtime.ActivateAsync(incompatible.Entry.Id)).Error?.Code);
    }

    [Fact]
    public async Task CatalogServiceVerifiesSignatureAndFallsBackToCacheOffline()
    {
        using var directory = new TempDirectory();
        var (payload, trustJson) = AdditionsTestData.SignCatalog(
            AdditionsTestData.CreateCatalogJson(AdditionsTestData.CreateEntry("ecl.alpha")));
        var trust = UpdateTrustStore.FromJson(trustJson).Value!;
        var store = new AdditionCatalogStore(Path.Combine(directory.Path, "catalog"));
        var source = new FakeCatalogSource { Payload = payload };
        var service = new AdditionCatalogService(source, store, trust);

        var online = await service.GetAsync();
        Assert.True(online.IsSuccess, online.Error?.ToString());
        Assert.False(online.Value!.FromCache);
        Assert.Single(online.Value.Catalog.Additions);

        source.Fail = true;
        var offline = await service.GetAsync();
        Assert.True(offline.IsSuccess, offline.Error?.ToString());
        Assert.True(offline.Value!.FromCache);
        Assert.Contains(offline.Value.Warnings, warning => warning.Contains("缓存", StringComparison.Ordinal));

        var tampered = await service.GetAsync(allowCache: false);
        Assert.False(tampered.IsSuccess);
    }

    [Fact]
    public async Task CatalogServiceRejectsTamperedCatalogAndUnavailableCache()
    {
        using var directory = new TempDirectory();
        var (payload, trustJson) = AdditionsTestData.SignCatalog(
            AdditionsTestData.CreateCatalogJson(AdditionsTestData.CreateEntry("ecl.alpha")));
        var trust = UpdateTrustStore.FromJson(trustJson).Value!;
        var store = new AdditionCatalogStore(Path.Combine(directory.Path, "catalog"));
        var tampered = payload with { CatalogJson = payload.CatalogJson.Replace("ecl.alpha", "ecl.evil", StringComparison.Ordinal) };
        var service = new AdditionCatalogService(new FakeCatalogSource { Payload = tampered }, store, trust);

        var result = await service.GetAsync(allowCache: false);

        Assert.False(result.IsSuccess);
        var empty = new AdditionCatalogService(
            new FakeCatalogSource { Fail = true },
            new AdditionCatalogStore(Path.Combine(directory.Path, "empty")),
            trust);
        Assert.Equal("additions.catalog_unavailable", (await empty.GetAsync()).Error?.Code);
    }

    [Fact]
    public async Task OfficialCatalogVerifiesAgainstPublishedTrust()
    {
        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is null)
        {
            return;
        }

        var catalogPath = Path.Combine(repositoryRoot, "additions", "catalog.json");
        var signaturePath = Path.Combine(repositoryRoot, "additions", "catalog.json.sig");
        var trustPath = Path.Combine(repositoryRoot, "build", "additions-trust.json");
        if (!File.Exists(catalogPath) || !File.Exists(signaturePath) || !File.Exists(trustPath))
        {
            return;
        }

        var catalog = await File.ReadAllTextAsync(catalogPath);
        var signature = await File.ReadAllTextAsync(signaturePath);
        var trust = await UpdateTrustStore.FromFileAsync(trustPath);
        Assert.True(trust.IsSuccess, trust.Error?.ToString());

        var verified = new UpdateSignatureVerifier(trust.Value!).Verify(
            Encoding.UTF8.GetBytes(catalog),
            Encoding.UTF8.GetBytes(signature));
        Assert.True(verified.IsSuccess, verified.Error?.ToString());
        Assert.True(AdditionCatalogParser.Parse(catalog).IsSuccess);
        Assert.Contains("ecl-additions", trust.Value!.KeyIds);
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ECL.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static AdditionDependency Dep(string id, string min = "1.0.0") =>
        new(AdditionId.Parse(id), ReleaseVersion.Parse(min));

    private sealed class FakeCatalogSource : IAdditionCatalogSource
    {
        public AdditionCatalogPayload? Payload { get; init; }

        public bool Fail { get; set; }

        public Task<Result<AdditionCatalogPayload>> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Fail || Payload is null
                    ? Result<AdditionCatalogPayload>.Fail("additions.catalog_source_unreachable", "offline")
                    : Result<AdditionCatalogPayload>.Ok(Payload));
    }

    private sealed class AdditionFixture : IDisposable
    {
        public AdditionFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "ecl-addition-tests", Guid.NewGuid().ToString("N"));
            PackageDirectory = Path.Combine(Root, "packages");
            AdditionsRoot = Path.Combine(Root, "additions");
            Directory.CreateDirectory(PackageDirectory);
            StateStore = new AdditionStateStore(Path.Combine(AdditionsRoot, "installed.json"));
            Installer = new AdditionInstaller(
                new LocalAdditionPackageProvider(PackageDirectory),
                StateStore,
                AdditionsRoot);
        }

        public string Root { get; }

        public string PackageDirectory { get; }

        public string AdditionsRoot { get; }

        public AdditionStateStore StateStore { get; }

        public AdditionInstaller Installer { get; }

        public AdditionInstaller CreateInstaller() => Installer;

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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecl-addition-tests", Guid.NewGuid().ToString("N"));
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

internal static class AdditionsTestData
{
    public static AdditionEntry CreateEntry(
        string id,
        string version = "1.0.0",
        AdditionType type = AdditionType.Feature,
        IReadOnlyList<AdditionDependency>? dependencies = null,
        string coreMin = "1.0.0",
        string coreMax = "9.9.9",
        IReadOnlyList<string>? platforms = null,
        int? dataSchemaVersion = null,
        string sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        return new AdditionEntry(
            AdditionId.Parse(id),
            type,
            id,
            "test addition",
            ReleaseVersion.Parse(version),
            new AdditionPackage($"https://example.invalid/{id}-{version}.zip", 1024, sha256),
            new AdditionCoreApiRange(ReleaseVersion.Parse(coreMin), ReleaseVersion.Parse(coreMax)),
            dependencies ?? [],
            [],
            dataSchemaVersion ?? (type == AdditionType.Data ? 1 : null),
            platforms ?? [],
            Withdrawn: false);
    }

    public static InstalledAddition CreateInstalled(string id, string version, AdditionType type) =>
        new(
            AdditionId.Parse(id),
            type,
            ReleaseVersion.Parse(version),
            Path.Combine(Path.GetTempPath(), "ecl-addition-installed", id, version),
            "unused.dll",
            Enabled: true,
            DataSchemaVersion: 1,
            Files: [],
            DateTimeOffset.UtcNow);

    public static AdditionCatalog CreateCatalog(params AdditionEntry[] entries) =>
        new(AdditionCatalogParser.CurrentSchemaVersion, ReleaseVersion.Parse("1.0.0"), entries);

    public static string CreateCatalogJson(params AdditionEntry[] entries) =>
        AdditionCatalogParser.Serialize(CreateCatalog(entries));

    public static (AdditionCatalogPayload Payload, string TrustJson) SignCatalog(string catalogJson)
    {
        var (privateKey, _, trustJson, keyId) = DeploymentTestData.CreateKeys();
        var signature = DeploymentTestData.Sign(Encoding.UTF8.GetBytes(catalogJson), privateKey, keyId);
        return (new AdditionCatalogPayload(catalogJson, signature), trustJson);
    }

    public static AdditionPackageResult CreatePackage(
        string packageDirectory,
        string id,
        string version,
        IReadOnlyList<AdditionDependency>? dependencies = null,
        int? dataSchemaVersion = null,
        string coreMin = "1.0.0",
        string coreMax = "9.9.9",
        IReadOnlyDictionary<string, byte[]>? extraFiles = null)
    {
        Directory.CreateDirectory(packageDirectory);
        var assemblyPath = typeof(TestAdditionModule).Assembly.Location;
        var assemblyBytes = File.ReadAllBytes(assemblyPath);
        var assemblyHash = UpdateHash.ComputeSha256(assemblyBytes);
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = AdditionCatalogParser.CurrentSchemaVersion,
            id,
            version,
            entryAssembly = "Ecl.TestAddition.dll",
            files = new[]
            {
                new { path = "Ecl.TestAddition.dll", size = assemblyBytes.Length, sha256 = assemblyHash },
            },
            dataSchemaVersion,
        });
        var zipPath = Path.Combine(packageDirectory, $"{id}-{version}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var assemblyEntry = archive.CreateEntry("Ecl.TestAddition.dll");
            using (var stream = assemblyEntry.Open())
            {
                stream.Write(assemblyBytes);
            }

            var manifestEntry = archive.CreateEntry(AdditionPackageManifestParser.FileName);
            using (var stream = manifestEntry.Open())
            {
                stream.Write(Encoding.UTF8.GetBytes(manifest));
            }

            foreach (var (name, bytes) in extraFiles ?? new Dictionary<string, byte[]>())
            {
                var extra = archive.CreateEntry(name);
                using var stream = extra.Open();
                stream.Write(bytes);
            }
        }

        var packageBytes = File.ReadAllBytes(zipPath);
        var entry = new AdditionEntry(
            AdditionId.Parse(id),
            dataSchemaVersion is null ? AdditionType.Feature : AdditionType.Data,
            id,
            "test addition",
            ReleaseVersion.Parse(version),
            new AdditionPackage(
                $"https://example.invalid/{id}-{version}.zip",
                packageBytes.Length,
                UpdateHash.ComputeSha256(packageBytes)),
            new AdditionCoreApiRange(ReleaseVersion.Parse(coreMin), ReleaseVersion.Parse(coreMax)),
            dependencies ?? [],
            [],
            dataSchemaVersion,
            [],
            Withdrawn: false);
        return new AdditionPackageResult(entry, zipPath);
    }

    internal sealed record AdditionPackageResult(AdditionEntry Entry, string ZipPath);
}
