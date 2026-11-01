// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Deployment;

/// <summary>
/// Bootstrapper 的稳定退出码契约（ADR 0016 §5）。调用方与自动化依赖这些值，
/// 不得在失败路径上临时发明新的数字。
/// </summary>
public static class BootstrapperExitCodes
{
    public const int Success = 0;
    public const int ArgumentError = 2;
    public const int TrustError = 3;
    public const int DeployError = 4;
    public const int LaunchError = 5;

    /// <summary>把结构化错误映射到退出码。未识别的部署错误一律按部署失败处理。</summary>
    public static int FromError(EclError? error)
    {
        if (error is null)
        {
            return Success;
        }

        return error.Code switch
        {
            "update.trust_missing"
                or "update.unknown_key"
                or "update.signature_invalid"
                or "update.trust_invalid"
                or "update.signature_malformed" => TrustError,

            "update.executable_missing"
                or "update.launch_failed" => LaunchError,

            "bootstrapper.arguments" => ArgumentError,

            _ => DeployError,
        };
    }
}
