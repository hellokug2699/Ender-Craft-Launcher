// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Minecraft.Loaders;
using Microsoft.Extensions.Logging;

namespace Ecl.Minecraft.Install;

public sealed record ForgeProcessorRunRequest(
    string JavaExecutable,
    string InstallerPath,
    string ClientJarPath,
    string GameVersion,
    string Side,
    ForgeInstallProfile Profile);

public interface IForgeProcessorRunner
{
    Task<Result> RunAsync(
        ForgeProcessorRunRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 使用用户可用的 Java 运行 Forge / NeoForge 安装器 processors，
/// 生成打补丁后的客户端 jar（libraries/.../*-client.jar）。
/// </summary>
public sealed class ForgeProcessorRunner(
    IAppPaths paths,
    ILocalizer localizer,
    ILogger<ForgeProcessorRunner> logger) : IForgeProcessorRunner
{
    public async Task<Result> RunAsync(
        ForgeProcessorRunRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var processors = ForgeProcessorPlanner.SelectProcessors(request.Profile, request.Side);
        if (processors.Count == 0)
        {
            return Result.Ok();
        }

        var cacheRoot = Path.Combine(
            paths.CacheDirectory,
            "forge",
            Path.GetFileNameWithoutExtension(request.InstallerPath));

        var tokens = ForgeProcessorPlanner.BuildTokens(
            request.Profile,
            request.Side,
            request.InstallerPath,
            paths.Root,
            request.ClientJarPath,
            request.GameVersion,
            paths.LibrariesDirectory,
            entry => ExtractInstallerEntry(request.InstallerPath, cacheRoot, entry));

        for (var index = 0; index < processors.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var processor = processors[index];
            var displayName = processor.Jar ?? $"processor-{index + 1}";
            progress?.Report(new InstallProgress(
                InstallStage.Processors,
                $"{localizer.Get("install.stage.processors")} ({index + 1}/{processors.Count}) {displayName}",
                index,
                processors.Count,
                0,
                0,
                displayName));

            var classpath = ForgeProcessorPlanner.ResolveClasspath(processor, paths.LibrariesDirectory);
            var missing = classpath.FirstOrDefault(path => !File.Exists(path));
            if (missing is not null)
            {
                return Result.Fail(
                    "install.processor_missing",
                    localizer.Get("install.error.processorMissing", missing),
                    displayName);
            }

            var mainClass = ReadMainClass(classpath[0]);
            if (string.IsNullOrWhiteSpace(mainClass))
            {
                return Result.Fail(
                    "install.processor_failed",
                    localizer.Get("install.error.processorFailed", displayName, -1),
                    classpath[0]);
            }

            var arguments = ForgeProcessorPlanner.ResolveArguments(
                processor,
                tokens,
                paths.LibrariesDirectory);

            var exitCode = await ExecuteAsync(
                    request.JavaExecutable,
                    classpath,
                    mainClass,
                    arguments,
                    displayName,
                    cancellationToken)
                .ConfigureAwait(false);

            if (exitCode != 0)
            {
                return Result.Fail(
                    "install.processor_failed",
                    localizer.Get("install.error.processorFailed", displayName, exitCode),
                    displayName);
            }
        }

        if (tokens.TryGetValue("PATCHED", out var patched) && !File.Exists(patched))
        {
            return Result.Fail(
                "install.processor_failed",
                localizer.Get("install.error.processorOutputMissing", patched),
                patched);
        }

        progress?.Report(new InstallProgress(
            InstallStage.Processors,
            localizer.Get("install.stage.processors"),
            processors.Count,
            processors.Count,
            0,
            0,
            null));

        return Result.Ok();
    }

    private async Task<int> ExecuteAsync(
        string javaExecutable,
        IReadOnlyList<string> classpath,
        string mainClass,
        IReadOnlyList<string> arguments,
        string displayName,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = javaExecutable,
            WorkingDirectory = paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(string.Join(Path.PathSeparator, classpath));
        startInfo.ArgumentList.Add(mainClass);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        logger.LogInformation("运行安装器处理器 {Processor}：{Java} {MainClass}", displayName, javaExecutable, mainClass);
        logger.LogDebug("处理器参数：{Arguments}", string.Join(' ', arguments));

        using var process = new Process { StartInfo = startInfo };
        var output = new List<string>();
        var error = new List<string>();
        var outputLock = new object();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                return;
            }

            lock (outputLock)
            {
                output.Add(eventArgs.Data);
                if (output.Count > 200)
                {
                    output.RemoveAt(0);
                }
            }

            logger.LogDebug("[{Processor}] {Line}", displayName, eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                return;
            }

            lock (outputLock)
            {
                error.Add(eventArgs.Data);
                if (error.Count > 200)
                {
                    error.RemoveAt(0);
                }
            }

            logger.LogWarning("[{Processor}] {Line}", displayName, eventArgs.Data);
        };

        if (!process.Start())
        {
            return -1;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var tail = error.Count > 0 ? error : output;
            logger.LogError(
                "安装器处理器 {Processor} 退出码 {ExitCode}：{Output}",
                displayName,
                process.ExitCode,
                string.Join(Environment.NewLine, tail.TakeLast(10)));
        }

        return process.ExitCode;
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
        catch (InvalidOperationException)
        {
            // 进程已经退出。
        }
    }

    private static string ExtractInstallerEntry(string installerPath, string cacheRoot, string entryName)
    {
        using var archive = ZipFile.OpenRead(installerPath);
        var entry = archive.GetEntry(entryName)
                    ?? throw new InvalidDataException($"安装器缺少文件：{entryName}");

        var destination = Path.Combine(
            cacheRoot,
            entryName.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        entry.ExtractToFile(destination, overwrite: true);
        return destination;
    }

    private static string? ReadMainClass(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);
        var entry = archive.GetEntry("META-INF/MANIFEST.MF");
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? key = null;
        var value = new StringBuilder();

        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(' '))
            {
                if (key is not null)
                {
                    value.Append(line[1..]);
                }

                continue;
            }

            if (key is not null)
            {
                if (string.Equals(key, "Main-Class", StringComparison.OrdinalIgnoreCase))
                {
                    return value.ToString().Trim();
                }
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                key = null;
                value.Clear();
                continue;
            }

            key = line[..separator].Trim();
            value.Clear();
            value.Append(line[(separator + 1)..].Trim());
        }

        return key is not null && string.Equals(key, "Main-Class", StringComparison.OrdinalIgnoreCase)
            ? value.ToString().Trim()
            : null;
    }
}
