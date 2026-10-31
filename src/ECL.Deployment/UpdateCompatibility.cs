// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Ecl.Core;

namespace Ecl.Deployment;

public static class UpdateCompatibility
{
    public static UpdateArchitecture CurrentArchitecture =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => UpdateArchitecture.Arm64,
            _ => UpdateArchitecture.X64,
        };

    public static Result EnsureCompatible(
        UpdateManifest manifest,
        ReleaseVersion updaterVersion,
        UpdateArchitecture? currentArchitecture = null)
    {
        var architecture = currentArchitecture ?? CurrentArchitecture;
        if (manifest.Architecture != architecture)
        {
            return Result.Fail(
                "update.architecture_mismatch",
                "更新包的目标架构与当前进程不匹配。",
                $"{manifest.Architecture.ToApiValue()} != {architecture.ToApiValue()}");
        }

        if (manifest.MinimumUpdaterVersion.CompareTo(updaterVersion) > 0)
        {
            return Result.Fail(
                "update.updater_too_old",
                "需要先更新启动引导程序，再安装此版本。",
                $"最低 {manifest.MinimumUpdaterVersion}，当前 {updaterVersion}");
        }

        return Result.Ok();
    }
}
