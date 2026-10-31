// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ValidateGameFilesStep(IAppPaths paths, ILocalizer localizer) : ILaunchStep
{
    public string Id => "files";

    public int Order => 350;

    public async Task<LaunchStepResult> ExecuteAsync(
        LaunchContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Version is null)
        {
            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                localizer.Get("launch.files.noVersion"));
        }

        var requiredFiles = VersionFilePlanner.GetRequiredFileChecks(
                context.Version,
                paths,
                context.Instance.VersionId)
            .GroupBy(check => check.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var missing = new List<VersionFileCheck>();
        var corrupt = new List<VersionFileCheck>();

        foreach (var check in requiredFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(check.Path))
            {
                missing.Add(check);
                continue;
            }

            if (check.Size.HasValue && new FileInfo(check.Path).Length != check.Size.Value)
            {
                corrupt.Add(check);
                continue;
            }

            if (check.VerifyHash &&
                !string.IsNullOrWhiteSpace(check.Sha1) &&
                !await MatchesSha1Async(check.Path, check.Sha1, cancellationToken).ConfigureAwait(false))
            {
                corrupt.Add(check);
            }
        }

        if (missing.Count == 0 && corrupt.Count == 0)
        {
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Success,
                localizer.Get("launch.files.ok", requiredFiles.Length));
        }

        // 导入的实例由其它启动器安装并维护，文件差异不应阻止启动。
        if (!string.IsNullOrWhiteSpace(context.Instance.GameDirectory))
        {
            var count = missing.Count + corrupt.Count;
            var sample = corrupt.Count > 0 ? corrupt[0].Path : missing[0].Path;
            return new LaunchStepResult(
                Id,
                LaunchStepStatus.Warning,
                localizer.Get("launch.files.imported", count, sample));
        }

        if (corrupt.Count > 0)
        {
            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                localizer.Get(
                    context.DryRun ? "launch.files.corrupt.preview" : "launch.files.corrupt",
                    corrupt.Count,
                    corrupt[0].Path));
        }

        return new LaunchStepResult(
            Id,
            context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
            localizer.Get(
                context.DryRun ? "launch.files.missing.preview" : "launch.files.missing",
                missing.Count,
                missing[0].Path));
    }

    private static async Task<bool> MatchesSha1Async(
        string path,
        string expectedSha1,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var actual = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(actual),
            expectedSha1,
            StringComparison.OrdinalIgnoreCase);
    }
}
