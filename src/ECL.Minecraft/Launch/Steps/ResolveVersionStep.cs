// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Localization;
using Ecl.Minecraft.Versions;

namespace Ecl.Minecraft.Launch.Steps;

internal sealed class ResolveVersionStep(IVersionResolver versionResolver, ILocalizer localizer) : ILaunchStep
{
    public string Id => "version";

    public int Order => 150;

    public async Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken cancellationToken = default)
    {
        var result = await versionResolver.ResolveAsync(context.Instance.VersionId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var message = localizer.Get(
                context.DryRun ? "launch.version.missing.preview" : "launch.version.missing",
                context.Instance.VersionId);

            // 继承链缺父版本、版本 JSON 损坏时，失败原因（缺失的具体文件/解析错误）
            // 比“找不到版本文件”更有助于定位问题。
            if (!string.IsNullOrWhiteSpace(result.Error?.Detail))
            {
                message = $"{message} {localizer.Get("launch.version.detail", result.Error.Detail)}";
            }

            return new LaunchStepResult(
                Id,
                context.DryRun ? LaunchStepStatus.Warning : LaunchStepStatus.Failed,
                message);
        }

        context.Version = result.Value;

        return new LaunchStepResult(
            Id,
            LaunchStepStatus.Success,
            localizer.Get("launch.version.found", context.Instance.VersionId));
    }
}
