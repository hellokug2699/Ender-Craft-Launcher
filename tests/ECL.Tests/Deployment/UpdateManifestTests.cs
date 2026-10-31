// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Ecl.Deployment;
using Xunit;

namespace Ecl.Tests.Deployment;

public sealed class UpdateManifestTests
{
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void ParsesAndRoundTripsAValidManifest()
    {
        var manifest = DeploymentTestData.CreateManifest(
            "1.0.1",
            "ECL-1.0.1-win-x64.zip",
            123,
            Sha,
            [new UpdateFile("ECL.exe", 10, new string('b', 64))]);

        var parsed = UpdateManifestParser.Parse(DeploymentTestData.Serialize(manifest));

        Assert.True(parsed.IsSuccess, parsed.Error?.ToString());
        var value = parsed.Value!;
        Assert.Equal("ECL", value.Product);
        Assert.Equal(UpdateChannel.Stable, value.Channel);
        Assert.Equal("1.0.1", value.Version.ToString());
        Assert.Equal(UpdateArchitecture.X64, value.Architecture);
        Assert.Equal("ECL-1.0.1-win-x64.zip", value.Package.FileName);
        Assert.Equal("ECL.exe", Assert.Single(value.Files).Path);
    }

    [Fact]
    public void RejectsUnsupportedSchemaAndUnknownFields()
    {
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "ECL.zip", 1, Sha);
        var json = Encoding.UTF8.GetString(DeploymentTestData.Serialize(manifest));

        var wrongSchema = UpdateManifestParser.Parse(
            Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")));
        Assert.Equal("update.schema_unsupported", unwrap(wrongSchema).Error?.Code);

        var unknown = UpdateManifestParser.Parse(
            Encoding.UTF8.GetBytes(json.Replace("{", "{\"unknown\":true,", StringComparison.Ordinal)));
        Assert.Equal("update.manifest_invalid_json", unwrap(unknown).Error?.Code);
    }

    [Fact]
    public void RejectsUnsafeFileAndRemovalPaths()
    {
        var unsafeFile = DeploymentTestData.CreateManifest(
            "1.0.1",
            "ECL.zip",
            1,
            Sha,
            [new UpdateFile("../evil.dll", 1, Sha)]);
        Assert.Equal(
            "update.files_invalid",
            unwrap(UpdateManifestParser.Parse(DeploymentTestData.Serialize(unsafeFile))).Error?.Code);

        var absolute = DeploymentTestData.CreateManifest(
            "1.0.1",
            "ECL.zip",
            1,
            Sha,
            [new UpdateFile("C:/Windows/evil.dll", 1, Sha)]);
        Assert.Equal(
            "update.files_invalid",
            unwrap(UpdateManifestParser.Parse(DeploymentTestData.Serialize(absolute))).Error?.Code);

        var removed = DeploymentTestData.CreateManifest("1.0.1", "ECL.zip", 1, Sha) with
        {
            RemovedFiles = ["../outside.dll"],
        };
        Assert.Equal(
            "update.removed_invalid",
            unwrap(UpdateManifestParser.Parse(DeploymentTestData.Serialize(removed))).Error?.Code);
    }

    [Fact]
    public void RejectsDuplicateAndConflictingEntries()
    {
        var duplicate = DeploymentTestData.CreateManifest(
            "1.0.1",
            "ECL.zip",
            1,
            Sha,
            [
                new UpdateFile("mods/a.jar", 1, Sha),
                new UpdateFile("MODS/A.jar", 1, Sha),
            ]);
        Assert.Equal(
            "update.files_invalid",
            unwrap(UpdateManifestParser.Parse(DeploymentTestData.Serialize(duplicate))).Error?.Code);

        var conflict = DeploymentTestData.CreateManifest(
            "1.0.1",
            "ECL.zip",
            1,
            Sha,
            [new UpdateFile("a.jar", 1, Sha)]) with
        {
            RemovedFiles = ["A.JAR"],
        };
        Assert.Equal(
            "update.removed_invalid",
            unwrap(UpdateManifestParser.Parse(DeploymentTestData.Serialize(conflict))).Error?.Code);
    }

    [Fact]
    public void RejectsInvalidProductChannelPlatformArchitectureAndVersions()
    {
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "ECL.zip", 1, Sha);
        var json = Encoding.UTF8.GetString(DeploymentTestData.Serialize(manifest));

        Assert.Equal(
            "update.product_mismatch",
            unwrap(UpdateManifestParser.Parse(
                Encoding.UTF8.GetBytes(json.Replace("\"product\":\"ECL\"", "\"product\":\"Other\"")))).Error?.Code);
        Assert.Equal(
            "update.channel_invalid",
            unwrap(UpdateManifestParser.Parse(
                Encoding.UTF8.GetBytes(json.Replace("\"channel\":\"stable\"", "\"channel\":\"nightly\"")))).Error?.Code);
        Assert.Equal(
            "update.platform_unsupported",
            unwrap(UpdateManifestParser.Parse(
                Encoding.UTF8.GetBytes(json.Replace("\"platform\":\"windows\"", "\"platform\":\"linux\"")))).Error?.Code);
        Assert.Equal(
            "update.architecture_unsupported",
            unwrap(UpdateManifestParser.Parse(
                Encoding.UTF8.GetBytes(json.Replace("\"architecture\":\"x64\"", "\"architecture\":\"x86\"")))).Error?.Code);
        Assert.Equal(
            "update.version_invalid",
            unwrap(UpdateManifestParser.Parse(
                Encoding.UTF8.GetBytes(json.Replace("\"version\":\"1.0.1\"", "\"version\":\"bad\"")))).Error?.Code);
        Assert.Equal(
            "update.min_updater_invalid",
            unwrap(UpdateManifestParser.Parse(
                Encoding.UTF8.GetBytes(
                    json.Replace("\"minimumUpdaterVersion\":\"1.0.0\"", "\"minimumUpdaterVersion\":\"bad\"")))).Error?.Code);
    }

    [Fact]
    public void RejectsInvalidPackageHash()
    {
        var manifest = DeploymentTestData.CreateManifest("1.0.1", "ECL.zip", 1, "not-a-hash");
        Assert.Equal(
            "update.package_invalid",
            unwrap(UpdateManifestParser.Parse(DeploymentTestData.Serialize(manifest))).Error?.Code);
    }

    private static Ecl.Core.Result<UpdateManifest> unwrap(Ecl.Core.Result<UpdateManifest> result) => result;
}
