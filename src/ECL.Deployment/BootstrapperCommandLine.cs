// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Deployment;

public enum BootstrapperAction
{
    Launch,
    Apply,
    Rollback,
    Cleanup,
}

public sealed record BootstrapperDeploy(
    string ManifestPath,
    string SignaturePath,
    string PackagePath,
    string? TrustFile);

/// <summary>Bootstrapper 的稳定命令行协议（ADR 0016 §5）。解析失败必须拒绝执行。</summary>
public sealed record BootstrapperCommand(
    string? InstallRoot,
    BootstrapperAction Action,
    BootstrapperDeploy? Deploy,
    bool DeployOnly,
    bool Help,
    IReadOnlyList<string> ApplicationArguments);

public static class BootstrapperCommandLine
{
    public const string ErrorCode = "bootstrapper.arguments";

    public static Result<BootstrapperCommand> Parse(string[] args)
    {
        string? installRoot = null;
        string? manifest = null;
        string? signature = null;
        string? package = null;
        string? trust = null;
        var apply = false;
        var rollback = false;
        var cleanup = false;
        var deployOnly = false;
        var help = false;
        var application = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--install-root":
                    if (!TryTakeValue(args, ref index, "--install-root", out installRoot, out var rootError))
                    {
                        return Result<BootstrapperCommand>.Fail(ErrorCode, rootError!);
                    }

                    break;

                case "--apply":
                    apply = true;
                    break;

                case "--manifest":
                    if (!TryTakeValue(args, ref index, "--manifest", out manifest, out var manifestError))
                    {
                        return Result<BootstrapperCommand>.Fail(ErrorCode, manifestError!);
                    }

                    break;

                case "--signature":
                    if (!TryTakeValue(args, ref index, "--signature", out signature, out var signatureError))
                    {
                        return Result<BootstrapperCommand>.Fail(ErrorCode, signatureError!);
                    }

                    break;

                case "--package":
                    if (!TryTakeValue(args, ref index, "--package", out package, out var packageError))
                    {
                        return Result<BootstrapperCommand>.Fail(ErrorCode, packageError!);
                    }

                    break;

                case "--trust":
                    if (!TryTakeValue(args, ref index, "--trust", out trust, out var trustError))
                    {
                        return Result<BootstrapperCommand>.Fail(ErrorCode, trustError!);
                    }

                    break;

                case "--rollback":
                    rollback = true;
                    break;

                case "--cleanup":
                    cleanup = true;
                    break;

                case "--deploy-only":
                    deployOnly = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    help = true;
                    break;

                default:
                    application.Add(args[index]);
                    break;
            }
        }

        var hasDeployInputs = manifest is not null || signature is not null || package is not null;
        if (apply || hasDeployInputs)
        {
            if (string.IsNullOrWhiteSpace(manifest) ||
                string.IsNullOrWhiteSpace(signature) ||
                string.IsNullOrWhiteSpace(package))
            {
                return Result<BootstrapperCommand>.Fail(
                    ErrorCode,
                    "--apply 需要同时提供 --manifest、--signature 与 --package。");
            }

            if (rollback || cleanup)
            {
                return Result<BootstrapperCommand>.Fail(
                    ErrorCode,
                    "--apply 不能与 --rollback 或 --cleanup 同时使用。");
            }
        }

        if (rollback && cleanup)
        {
            return Result<BootstrapperCommand>.Fail(
                ErrorCode,
                "--rollback 不能与 --cleanup 同时使用。");
        }

        var action = apply || hasDeployInputs
            ? BootstrapperAction.Apply
            : rollback
                ? BootstrapperAction.Rollback
                : cleanup
                    ? BootstrapperAction.Cleanup
                    : BootstrapperAction.Launch;

        var deploy = action == BootstrapperAction.Apply
            ? new BootstrapperDeploy(manifest!, signature!, package!, trust)
            : null;

        return Result<BootstrapperCommand>.Ok(new BootstrapperCommand(
            installRoot,
            action,
            deploy,
            deployOnly,
            help,
            application));
    }

    private static bool TryTakeValue(
        string[] args,
        ref int index,
        string name,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            error = $"{name} 需要参数值。";
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
