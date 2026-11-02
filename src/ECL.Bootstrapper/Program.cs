// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Reflection;
using System.Text;
using Ecl.Core;
using Ecl.Deployment;

namespace Ecl.Bootstrapper;

/// <summary>
/// 最小启动引导程序：读取 current.json 并启动对应版本；也负责在 ECL 主进程退出后
/// 应用已经下载、验证并 staging 的完整更新包。它不覆盖正在运行的 ECL 程序集。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var parsed = BootstrapperCommandLine.Parse(args);
        if (!parsed.IsSuccess)
        {
            Console.Error.WriteLine(parsed.Error!.Message);
            Console.WriteLine(Usage);
            return BootstrapperExitCodes.ArgumentError;
        }

        var command = parsed.Value!;

        if (command.Help)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var layout = new UpdateLayout(command.InstallRoot ?? AppContext.BaseDirectory);
        var installer = new UpdateInstaller(layout);
        using var log = BootstrapperLog.Open(layout.InstallRoot);

        try
        {
            if (command.Action == BootstrapperAction.Verify)
            {
                return VerifyIntegrity(layout, log, verifyHashes: true);
            }

            if (command.Action == BootstrapperAction.Repair)
            {
                return RepairIntegrity(layout, log);
            }

            if (command.Deploy is not null)
            {
                return ApplyAsync(layout, installer, command.Deploy, log);
            }

            if (command.Action == BootstrapperAction.Cleanup)
            {
                using var cleanupLock = UpdateLock.TryAcquire(layout.InstallRoot);
                if (cleanupLock is null)
                {
                    Console.Error.WriteLine("另一个更新进程正在运行。");
                    return BootstrapperExitCodes.DeployError;
                }

                var removed = installer.CleanupStaging();
                Console.WriteLine($"已清理 {removed} 个未完成的 staging 目录；当前版本未被触碰。");
                log.Write($"清理 staging：{removed} 个目录");
                return 0;
            }

            if (command.Action == BootstrapperAction.Rollback)
            {
                using var rollbackLock = UpdateLock.TryAcquire(layout.InstallRoot);
                if (rollbackLock is null)
                {
                    Console.Error.WriteLine("另一个更新进程正在运行。");
                    return BootstrapperExitCodes.DeployError;
                }

                var rollback = installer.RollbackAsync().GetAwaiter().GetResult();
                if (!rollback.IsSuccess)
                {
                    return Fail(rollback.Error!, log);
                }

                Console.WriteLine($"已回滚到 {rollback.Value!.Version}");
            }

            if (command.DeployOnly)
            {
                return 0;
            }

            return Launch(layout, command.ApplicationArguments, log);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.Write($"未处理异常：{exception}");
            Console.Error.WriteLine(exception.Message);
            return BootstrapperExitCodes.DeployError;
        }
    }

    private static int ApplyAsync(
        UpdateLayout layout,
        UpdateInstaller installer,
        BootstrapperDeploy deploy,
        BootstrapperLog log)
    {
        using var updateLock = UpdateLock.TryAcquire(layout.InstallRoot);
        if (updateLock is null)
        {
            Console.Error.WriteLine("另一个更新进程正在运行。");
            return BootstrapperExitCodes.DeployError;
        }

        var cleaned = installer.CleanupStaging();
        if (cleaned > 0)
        {
            log.Write($"部署前清理未完成 staging：{cleaned} 个目录");
        }

        if (!File.Exists(layout.TrustFile) && deploy.TrustFile is null)
        {
            return Fail(
                new EclError(
                    "update.trust_missing",
                    "未找到 update-trust.json，无法验证更新签名。"),
                log);
        }

        var trustResult = UpdateTrustStore
            .FromFileAsync(deploy.TrustFile ?? layout.TrustFile)
            .GetAwaiter()
            .GetResult();
        if (!trustResult.IsSuccess)
        {
            return Fail(trustResult.Error!, log);
        }

        byte[] manifestBytes;
        byte[] signatureBytes;
        try
        {
            manifestBytes = File.ReadAllBytes(deploy.ManifestPath);
            signatureBytes = File.ReadAllBytes(deploy.SignaturePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Fail(new EclError("update.manifest_missing", "无法读取更新清单或签名。", exception.Message), log);
        }

        var verifier = new UpdateSignatureVerifier(trustResult.Value!);
        var verified = verifier.Verify(manifestBytes, signatureBytes);
        if (!verified.IsSuccess)
        {
            return Fail(verified.Error!, log);
        }

        var parsed = UpdateManifestParser.Parse(manifestBytes);
        if (!parsed.IsSuccess)
        {
            return Fail(parsed.Error!, log);
        }

        var compatibility = UpdateCompatibility.EnsureCompatible(
            parsed.Value!,
            GetBootstrapperVersion());
        if (!compatibility.IsSuccess)
        {
            return Fail(compatibility.Error!, log);
        }

        var apply = installer
            .ApplyAsync(parsed.Value!, deploy.PackagePath)
            .GetAwaiter()
            .GetResult();
        if (!apply.IsSuccess)
        {
            return Fail(apply.Error!, log);
        }

        CopyProvenance(
            layout,
            apply.Value!.ParsedVersion ?? ReleaseVersion.Parse(apply.Value.Version),
            deploy.ManifestPath,
            deploy.SignaturePath,
            log);
        Console.WriteLine($"已部署 ECL {apply.Value!.Version}，当前版本指针已切换。");
        log.Write($"部署完成：{apply.Value.Version}（上一版本：{apply.Value.PreviousVersion ?? "无"}）");
        return 0;
    }

    /// <summary>
    /// 把已签名的更新清单与签名保存到版本目录，供 ADR 0034 的自身完整性校验和离线恢复使用。
    /// 清单来自已经通过签名验证的部署输入，不额外信任安装介质中的任意文件。
    /// </summary>
    private static void CopyProvenance(
        UpdateLayout layout,
        ReleaseVersion version,
        string manifestPath,
        string signaturePath,
        BootstrapperLog log)
    {
        try
        {
            var versionDirectory = layout.GetVersionDirectory(version);
            File.Copy(
                manifestPath,
                Path.Combine(versionDirectory, SelfIntegrityVerifier.ManifestFileName),
                overwrite: true);
            File.Copy(
                signaturePath,
                Path.Combine(versionDirectory, SelfIntegrityVerifier.SignatureFileName),
                overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write($"警告：无法在版本目录保存安装清单副本：{exception.Message}");
        }
    }

    private static int VerifyIntegrity(UpdateLayout layout, BootstrapperLog log, bool verifyHashes)
    {
        var result = new SelfIntegrityVerifier()
            .VerifyAsync(layout.InstallRoot, verifyHashes)
            .GetAwaiter()
            .GetResult();
        if (!result.IsSuccess)
        {
            return Fail(result.Error!, log);
        }

        var report = result.Value!;
        Console.WriteLine(report.Describe());
        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"  - {issue}");
        }

        log.Write($"完整性校验：{report.Describe()}；{string.Join("；", report.Issues)}");
        return report.IsHealthy ? 0 : BootstrapperExitCodes.IntegrityError;
    }

    private static int RepairIntegrity(UpdateLayout layout, BootstrapperLog log)
    {
        var result = new SelfRepairService().RepairAsync(layout.InstallRoot).GetAwaiter().GetResult();
        if (!result.IsSuccess)
        {
            return Fail(result.Error!, log);
        }

        var outcome = result.Value!;
        Console.WriteLine(outcome.Before.Describe());
        if (outcome.RestoredFiles.Count > 0)
        {
            Console.WriteLine($"已从本地恢复材料修复 {outcome.RestoredFiles.Count} 个文件：");
            foreach (var file in outcome.RestoredFiles)
            {
                Console.WriteLine($"  - {file}");
            }
        }

        if (outcome.ResidualIssues.Count > 0)
        {
            Console.WriteLine("仍存在的问题：");
            foreach (var issue in outcome.ResidualIssues)
            {
                Console.WriteLine($"  - {issue}");
            }
        }

        log.Write($"Repair Mode：恢复 {outcome.RestoredFiles.Count} 个文件，成功={outcome.Success}；{string.Join("；", outcome.ResidualIssues)}");
        if (outcome.Success)
        {
            Console.WriteLine("ECL 安装完整性已恢复。");
            return 0;
        }

        Console.WriteLine("无法完成离线修复：本地恢复材料不足或来源未验证。请从官方发布源重新安装对应版本。");
        return BootstrapperExitCodes.IntegrityError;
    }

    /// <summary>
    /// 正常启动前的轻量检查（ADR 0034 §8）：只核对当前版本与文件是否存在/大小一致；
    /// 仅在内容受损时进入 Repair Mode，避免每次启动都做全量哈希扫描。
    /// </summary>
    private static int EnsureIntegrityBeforeLaunch(UpdateLayout layout, BootstrapperLog log)
    {
        var result = new SelfIntegrityVerifier()
            .VerifyAsync(layout.InstallRoot, verifyHashes: false)
            .GetAwaiter()
            .GetResult();
        if (!result.IsSuccess)
        {
            if (result.Error!.Code is "integrity.manifest_missing" or "integrity.current_missing")
            {
                Console.WriteLine("未找到完整性清单（旧版安装或开发构建）；跳过自身完整性校验。");
                log.Write($"跳过自身完整性校验：{result.Error.Code}");
                return 0;
            }

            return Fail(result.Error!, log);
        }

        var report = result.Value!;
        if (!report.ContentIntact)
        {
            Console.WriteLine(report.Describe());
            Console.WriteLine("检测到 ECL 安装文件损坏，进入 Repair Mode。");
            log.Write("检测到安装文件损坏，进入 Repair Mode。");
            var repair = new SelfRepairService().RepairAsync(layout.InstallRoot).GetAwaiter().GetResult();
            if (!repair.IsSuccess)
            {
                return Fail(repair.Error!, log);
            }

            if (!repair.Value!.Success)
            {
                if (repair.Value.ResidualIssues.Count > 0)
                {
                    Console.WriteLine(string.Join(Environment.NewLine, repair.Value.ResidualIssues.Select(issue => $"  - {issue}")));
                }

                Console.WriteLine("无法完成离线修复：本地恢复材料不足或来源未验证。请从官方发布源重新安装对应版本。");
                log.Write("Repair Mode 未能修复安装，拒绝启动。");
                return BootstrapperExitCodes.IntegrityError;
            }

            Console.WriteLine("ECL 安装文件已从本地恢复材料修复。");
            log.Write("Repair Mode 完成本地修复。");
        }
        else if (report.Trust != SelfIntegrityTrustStatus.Verified)
        {
            Console.WriteLine("提示：无法验证安装清单来源签名；文件内容检查通过。");
            log.Write($"来源签名不可用：{report.Trust}");
        }

        return 0;
    }

    private static int Launch(UpdateLayout layout, IReadOnlyList<string> arguments, BootstrapperLog log)
    {
        var integrity = EnsureIntegrityBeforeLaunch(layout, log);
        if (integrity != 0)
        {
            return integrity;
        }

        var currentResult = layout.ReadCurrentAsync().GetAwaiter().GetResult();
        if (!currentResult.IsSuccess)
        {
            return Fail(currentResult.Error!, log);
        }

        var current = currentResult.Value!;
        var versionDirectory = layout.GetVersionDirectory(current.ParsedVersion!);
        var executable = Path.Combine(
            versionDirectory,
            current.Executable.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(executable))
        {
            return Fail(
                new EclError(
                    "update.executable_missing",
                    "当前版本的可执行文件不存在，请重新安装或回滚。",
                    executable),
                log);
        }

        Console.WriteLine($"启动 ECL {current.Version}");
        log.Write($"启动 ECL {current.Version}：{executable}");
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = versionDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return Fail(new EclError("update.launch_failed", "无法启动 ECL 主程序。"), log);
        }

        process.WaitForExit();
        log.Write($"ECL {current.Version} 退出，退出码 {process.ExitCode}。");
        if (process.ExitCode == 0)
        {
            TryCaptureRecoveryCache(layout, current, log);
        }

        return process.ExitCode;
    }

    /// <summary>
    /// 一次成功启动后保存本地恢复材料（每个版本最多一次），供后续离线修复使用。
    /// 捕获失败只记录日志，不影响正常启动。
    /// </summary>
    private static void TryCaptureRecoveryCache(UpdateLayout layout, CurrentVersion current, BootstrapperLog log)
    {
        var cache = new RecoveryCache(Path.Combine(layout.InstallRoot, "recovery"));
        if (Directory.Exists(Path.Combine(cache.VersionsDirectory, current.Version)))
        {
            return;
        }

        try
        {
            var captured = cache.CaptureAsync(layout.InstallRoot, current).GetAwaiter().GetResult();
            log.Write(captured.IsSuccess
                ? $"已保存恢复缓存：{current.Version}（{captured.Value!.FileCount} 个文件）"
                : $"未保存恢复缓存：{captured.Error}");
        }
        catch (Exception exception)
        {
            log.Write($"保存恢复缓存异常：{exception.Message}");
        }
    }

    private static int Fail(EclError error, BootstrapperLog log)
    {
        Console.Error.WriteLine(error);
        log.Write($"失败：{error.Code} {error}");
        return BootstrapperExitCodes.FromError(error);
    }

    private static ReleaseVersion GetBootstrapperVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (informational is not null)
        {
            var plus = informational.IndexOf('+');
            var value = plus >= 0 ? informational[..plus] : informational;
            if (ReleaseVersion.TryParse(value, out var version))
            {
                return version;
            }
        }

        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        return assemblyVersion is null
            ? ReleaseVersion.Parse("0.0.0")
            : ReleaseVersion.Parse($"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}");
    }

    private const string Usage = """
        用法：
          ECL.Bootstrapper.exe [--install-root <目录>] [ECL 参数...]
          ECL.Bootstrapper.exe [--install-root <目录>] --apply
              --manifest <update-manifest.json>
              --signature <update-manifest.sig>
              --package <ECL-*.zip>
              [--trust <update-trust.json>] [--deploy-only]
          ECL.Bootstrapper.exe [--install-root <目录>] --rollback [--deploy-only]
          ECL.Bootstrapper.exe [--install-root <目录>] --cleanup
          ECL.Bootstrapper.exe [--install-root <目录>] --verify
          ECL.Bootstrapper.exe [--install-root <目录>] --repair

        默认读取 <install-root>/current.json 并启动其中的版本。更新部署在独立进程中完成，
        不会覆盖正在运行的 ECL。--cleanup 只清理未完成的 staging，不触碰 current 版本。
        --verify 全量核对签名清单与文件哈希；--repair 只使用本地恢复材料恢复 ECL 自有文件，
        不会删除或重置实例、存档、配置等用户数据。正常启动只做轻量检查，发现内容损坏时进入 Repair Mode。
        """;

    private sealed class BootstrapperLog : IDisposable
    {
        private readonly string _path;
        private readonly object _gate = new();

        private BootstrapperLog(string path) => _path = path;

        public static BootstrapperLog Open(string installRoot)
        {
            try
            {
                Directory.CreateDirectory(installRoot);
                return new BootstrapperLog(Path.Combine(installRoot, "bootstrapper.log"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new BootstrapperLog(Path.Combine(Path.GetTempPath(), "ecl-bootstrapper.log"));
            }
        }

        public void Write(string message)
        {
            try
            {
                lock (_gate)
                {
                    File.AppendAllText(
                        _path,
                        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 日志失败不能阻断启动/更新。
            }
        }

        public void Dispose()
        {
        }
    }
}
