// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Ecl.Java;

/// <summary>Java 运行时的验证状态（页面规格 §3.2.5）：可用、验证中、异常不可用与信息未知必须区分。</summary>
public enum JavaRuntimeValidationState
{
    /// <summary>尚未验证；不得显示成可用。</summary>
    Unknown,

    /// <summary>验证进行中。</summary>
    Validating,

    /// <summary>实际执行 <c>java -version</c> 成功且信息可读。</summary>
    Available,

    /// <summary>可执行文件可以运行，但是 32 位，不能用于 Minecraft 启动。</summary>
    Unsupported32Bit,

    /// <summary>验证失败：无法启动、超时、非零退出码或输出不可读。</summary>
    Failed,
}

/// <summary>验证失败类别。页面据此选择本地化文案，不解析异常文本。</summary>
public enum JavaRuntimeValidationFailure
{
    None,
    NotStarted,
    TimedOut,
    ExitCode,
    Unreadable,
}

/// <summary>
/// 一次真实验证的结果。版本、发行版与架构只来自实际进程输出；
/// 解析不到时保持未知，不拿目录名或 release 文件冒充验证结论。
/// </summary>
public sealed record JavaRuntimeValidation(
    JavaRuntimeValidationState State,
    JavaVersion? Version,
    string? Vendor,
    string? Architecture,
    JavaRuntimeValidationFailure Failure,
    string? Detail,
    TimeSpan Duration)
{
    public bool IsAvailable => State == JavaRuntimeValidationState.Available;

    public bool IsUnsupported32Bit => State == JavaRuntimeValidationState.Unsupported32Bit;

    public static JavaRuntimeValidation Unknown { get; } = new(
        JavaRuntimeValidationState.Unknown,
        null,
        null,
        null,
        JavaRuntimeValidationFailure.None,
        null,
        TimeSpan.Zero);
}

/// <summary>一次 <c>java -XshowSettings:properties -version</c> 探测的原始结果。</summary>
public sealed record JavaVersionProbeResult(
    bool Started,
    bool TimedOut,
    int ExitCode,
    string Output,
    string? ErrorMessage)
{
    public static JavaVersionProbeResult NotStarted(string? message) =>
        new(false, false, -1, string.Empty, message);

    public static JavaVersionProbeResult Timeout() => new(true, true, -1, string.Empty, null);
}

/// <summary>可替换的进程探测入口，便于自动化测试不依赖本机 Java。</summary>
public interface IJavaVersionProbe
{
    Task<JavaVersionProbeResult> RunAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>执行 <c>java -version</c> 并返回标准输出/错误。只读取进程输出，不修改任何文件。</summary>
public sealed class ProcessJavaVersionProbe : IJavaVersionProbe
{
    public async Task<JavaVersionProbeResult> RunAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return JavaVersionProbeResult.NotStarted("executable-missing");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-XshowSettings:properties");
        startInfo.ArgumentList.Add("-version");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return JavaVersionProbeResult.NotStarted(exception.Message);
        }

        if (process is null)
        {
            return JavaVersionProbeResult.NotStarted(null);
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return JavaVersionProbeResult.Timeout();
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}";
            return new JavaVersionProbeResult(true, false, process.ExitCode, output, null);
        }
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
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
            // 进程可能已自行退出；超时结果仍然如实返回。
        }
    }
}

/// <summary><c>java -version</c> 输出的结构化结果。</summary>
public sealed record JavaVersionOutput(
    JavaVersion? Version,
    string? Vendor,
    string? Architecture,
    string? RawVersion)
{
    private static readonly Regex VersionLinePattern = new(
        "version\\s+\"(?<value>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ArchitectureBitsPattern = new(
        "sun\\.arch\\.data\\.model\\s*=\\s*(?<value>\\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析 <c>-XshowSettings:properties -version</c> 的混合输出。
    /// Java 8 把版本信息写到 stderr，Java 9+ 的 properties 也来自 stderr；
    /// 调用方应传入 stdout 与 stderr 的合并文本。
    /// </summary>
    public static JavaVersionOutput? TryParse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var rawVersion = ReadProperty(output, "java.version")
                         ?? ReadProperty(output, "java.runtime.version");
        var vendor = ReadProperty(output, "java.vendor");
        var architecture = ReadProperty(output, "os.arch");

        if (rawVersion is null)
        {
            var match = VersionLinePattern.Match(output);
            if (match.Success)
            {
                rawVersion = match.Groups["value"].Value.Trim();
            }
        }

        if (rawVersion is null && vendor is null && architecture is null)
        {
            return null;
        }

        // 8/16 位标记来自进程自身报告；os.arch 缺失时仍可用 sun.arch.data.model 判定位数。
        if (string.IsNullOrWhiteSpace(architecture) &&
            ArchitectureBitsPattern.Match(output) is { Success: true } bits)
        {
            architecture = bits.Groups["value"].Value == "32" ? "x86" : "amd64";
        }

        return new JavaVersionOutput(JavaVersion.TryParse(rawVersion), vendor, architecture, rawVersion);
    }

    private static string? ReadProperty(string output, string name)
    {
        var pattern = new Regex(
            "^\\s*" + Regex.Escape(name) + "\\s*=\\s*(?<value>.+?)\\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = pattern.Match(output);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }
}

public interface IJavaRuntimeValidator
{
    /// <summary>实际执行该运行时并返回真实验证状态；不修改任何文件。</summary>
    Task<JavaRuntimeValidation> ValidateAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 有界、可取消、不阻塞 UI 的 Java 验证实现。
/// 一个候选失败只影响该候选，不阻断其它候选。
/// </summary>
public sealed class JavaRuntimeValidator(
    IJavaVersionProbe probe,
    TimeSpan? timeout = null) : IJavaRuntimeValidator
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);

    public async Task<JavaRuntimeValidation> ValidateAsync(
        JavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var stopwatch = Stopwatch.StartNew();
        var probeResult = await probe
            .RunAsync(runtime.ExecutablePath, _timeout, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        if (!probeResult.Started)
        {
            return Failed(JavaRuntimeValidationFailure.NotStarted, probeResult.ErrorMessage, stopwatch.Elapsed);
        }

        if (probeResult.TimedOut)
        {
            return Failed(JavaRuntimeValidationFailure.TimedOut, null, stopwatch.Elapsed);
        }

        var parsed = JavaVersionOutput.TryParse(probeResult.Output);
        if (probeResult.ExitCode != 0)
        {
            return new JavaRuntimeValidation(
                JavaRuntimeValidationState.Failed,
                parsed?.Version,
                parsed?.Vendor,
                parsed?.Architecture,
                JavaRuntimeValidationFailure.ExitCode,
                probeResult.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                stopwatch.Elapsed);
        }

        if (parsed is null)
        {
            return Failed(JavaRuntimeValidationFailure.Unreadable, null, stopwatch.Elapsed);
        }

        var bitness = JavaArchitecture.Classify(parsed.Architecture);
        if (bitness == JavaBitness.Bit32 || (bitness == JavaBitness.Unknown && runtime.IsKnown32Bit))
        {
            return new JavaRuntimeValidation(
                JavaRuntimeValidationState.Unsupported32Bit,
                parsed.Version,
                parsed.Vendor,
                parsed.Architecture ?? runtime.Architecture,
                JavaRuntimeValidationFailure.None,
                null,
                stopwatch.Elapsed);
        }

        if (parsed.Version is null)
        {
            // 进程能运行但版本不可读：这是“信息未知”，不是可用，也不是失败；
            // 页面必须如实呈现，不能猜测版本号。
            return new JavaRuntimeValidation(
                JavaRuntimeValidationState.Unknown,
                null,
                parsed.Vendor,
                parsed.Architecture,
                JavaRuntimeValidationFailure.Unreadable,
                parsed.RawVersion,
                stopwatch.Elapsed);
        }

        return new JavaRuntimeValidation(
            JavaRuntimeValidationState.Available,
            parsed.Version,
            parsed.Vendor,
            parsed.Architecture ?? runtime.Architecture,
            JavaRuntimeValidationFailure.None,
            null,
            stopwatch.Elapsed);
    }

    private static JavaRuntimeValidation Failed(
        JavaRuntimeValidationFailure failure,
        string? detail,
        TimeSpan duration) =>
        new(
            JavaRuntimeValidationState.Failed,
            null,
            null,
            null,
            failure,
            detail,
            duration);
}
