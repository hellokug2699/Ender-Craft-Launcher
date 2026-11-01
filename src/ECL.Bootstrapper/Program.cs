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
            if (command.Deploy is not null)
            {
                return ApplyAsync(layout, installer, command.Deploy, log);
            }

            if (command.Action == BootstrapperAction.Cleanup)
            {
                using var cleanupLock = AcquireUpdateLock(layout.InstallRoot);
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
                using var rollbackLock = AcquireUpdateLock(layout.InstallRoot);
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
        using var updateLock = AcquireUpdateLock(layout.InstallRoot);
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

        Console.WriteLine($"已部署 ECL {apply.Value!.Version}，当前版本指针已切换。");
        log.Write($"部署完成：{apply.Value.Version}（上一版本：{apply.Value.PreviousVersion ?? "无"}）");
        return 0;
    }

    private static int Launch(UpdateLayout layout, IReadOnlyList<string> arguments, BootstrapperLog log)
    {
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
        return process.ExitCode;
    }

    private static FileStream? AcquireUpdateLock(string installRoot)
    {
        try
        {
            return new FileStream(
                Path.Combine(installRoot, ".update.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
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

        默认读取 <install-root>/current.json 并启动其中的版本。更新部署在独立进程中完成，
        不会覆盖正在运行的 ECL。--cleanup 只清理未完成的 staging，不触碰 current 版本。
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
