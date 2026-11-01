// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Ecl.Core;

namespace Ecl.Deployment;

/// <summary>
/// 首次安装/升级的编排层：校验介质与目标、复制 Bootstrapper 与公开信任库，
/// 然后调用稳定的 Bootstrapper CLI 完成签名验证、解包、逐文件校验与指针切换。
/// 安装器自身不实现第二套解包或签名逻辑。
/// </summary>
public sealed class InstallerService
{
    private readonly string? _bootstrapperPathOverride;

    public InstallerService(string? bootstrapperPathOverride = null)
    {
        _bootstrapperPathOverride = bootstrapperPathOverride;
    }

    public async Task<Result<InstallOutcome>> InstallAsync(
        InstallOptions options,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(options.InstallRoot))
        {
            return Result<InstallOutcome>.Fail("installer.target.relative", "安装目录必须是绝对路径。");
        }

        string target;
        try
        {
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.InstallRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<InstallOutcome>.Fail("installer.target.invalid", $"安装目录无效：{exception.Message}");
        }

        if (string.Equals(
                target,
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(target) ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<InstallOutcome>.Fail("installer.target.root", "不能把启动器直接安装到磁盘根目录。");
        }

        var isUpgrade = File.Exists(Path.Combine(target, "current.json"));

        progress?.Report(new InstallProgress("validating", target));
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, $".ecl-install-probe-{Guid.NewGuid():N}");
            await File.WriteAllTextAsync(probe, "probe", cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<InstallOutcome>.Fail(
                "installer.target.notWritable",
                $"安装目录不可写：{exception.Message}",
                target);
        }

        var bootstrapperTarget = Path.Combine(target, "ECL.Bootstrapper.exe");
        var trustTarget = Path.Combine(target, "update-trust.json");

        progress?.Report(new InstallProgress("copying", "ECL.Bootstrapper.exe"));
        try
        {
            File.Copy(options.Media.BootstrapperPath, bootstrapperTarget, overwrite: true);
            File.Copy(options.Media.TrustPath, trustTarget, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<InstallOutcome>.Fail(
                "installer.copy.failed",
                $"复制部署组件失败：{exception.Message}");
        }

        var bootstrapper = _bootstrapperPathOverride ?? bootstrapperTarget;
        var startInfo = new ProcessStartInfo(bootstrapper)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = target,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "--install-root", target,
                     "--apply",
                     "--manifest", options.Media.ManifestPath,
                     "--signature", options.Media.SignaturePath,
                     "--package", options.Media.PackagePath,
                     "--trust", trustTarget,
                     "--deploy-only",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        progress?.Report(new InstallProgress(
            isUpgrade ? "upgrading" : "installing",
            "签名验证、解包与逐文件校验由 Bootstrapper 执行。"));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Result<InstallOutcome>.Fail("installer.start.failed", "无法启动 ECL.Bootstrapper.exe。");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Result<InstallOutcome>.Fail("installer.start.failed", $"无法启动 ECL.Bootstrapper.exe：{exception.Message}");
        }

        using var registration = cancellationToken.Register(() => TryKill(process));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new InstallProgress("cancelled"));
            return Result<InstallOutcome>.Fail(
                "installer.cancelled",
                "安装已取消。Bootstrapper 若已开始部署，会留下可恢复的 staging；current 指针只有在验证通过后才会切换。");
        }

        var logPath = Path.Combine(target, "bootstrapper.log");
        var outcome = new InstallOutcome(process.ExitCode, File.Exists(logPath) ? logPath : null);
        if (outcome.Success)
        {
            progress?.Report(new InstallProgress("completed", target, 100));
            return Result<InstallOutcome>.Ok(outcome);
        }

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        progress?.Report(new InstallProgress("failed", outcome.DescribeFailure()));
        return Result<InstallOutcome>.Fail(
            "installer.deploy.failed",
            $"{outcome.DescribeFailure()}（退出码 {process.ExitCode}）",
            string.IsNullOrWhiteSpace(detail) ? logPath : detail);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 进程已退出或无权终止；取消结果由调用方报告。
        }
    }
}
