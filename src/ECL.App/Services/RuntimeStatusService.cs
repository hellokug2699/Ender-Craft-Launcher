// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Ecl.Additions;
using Ecl.Core;
using Ecl.Core.Localization;
using Ecl.Deployment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.App.Services;

/// <summary>ECL 受监控组件的健康状态；与“是否正在运行”是独立维度（runtime-status.md §3.2）。</summary>
public enum RuntimeHealth
{
    Healthy,
    Warning,
    Damaged,
    Unknown,
}

/// <summary>组件当前是否在执行。按需停止的健康组件仍是健康状态。</summary>
public enum RuntimeRunState
{
    Running,
    Stopped,
    Unknown,
}

/// <summary>状态中心的一行。原因为空表示没有错误；未知原因必须明确写“未知原因”，不得编造。</summary>
public sealed record RuntimeComponentStatus(
    string Name,
    RuntimeHealth Health,
    RuntimeRunState RunState,
    string? Reason = null);

/// <summary>
/// 一次真实的状态中心检查结果。不可用时只给出原因，不生成看似正常的组件列表。
/// </summary>
public sealed record RuntimeStatusReport(
    bool IsAvailable,
    string? UnavailableReason,
    string? Version,
    DateTimeOffset CheckedAt,
    IReadOnlyList<RuntimeComponentStatus> Components)
{
    /// <summary>是否覆盖了全部应检查项；扩展记录损坏时为 <c>false</c>。</summary>
    public bool IsComplete { get; init; } = true;

    /// <summary>检查覆盖说明；只陈述实际读取过的来源。</summary>
    public string CoverageText { get; init; } = string.Empty;

    public static RuntimeStatusReport Unavailable(string reason) =>
        new(false, reason, null, DateTimeOffset.Now, [])
        {
            IsComplete = false,
            CoverageText = string.Empty,
        };
}

/// <summary>状态中心的数据入口。页面只呈现这里给出的真实结论，不自行推断健康状态。</summary>
public interface IRuntimeStatusService
{
    Task<RuntimeStatusReport> InspectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 状态中心的检查环境。生产使用安装根目录（AppContext.BaseDirectory）；测试可替换。
/// </summary>
public sealed record RuntimeStatusEnvironment(string InstallRoot)
{
    public static RuntimeStatusEnvironment FromProcess() => new(AppContext.BaseDirectory);
}

/// <summary>
/// 状态中心的真实数据源：以安装布局的签名更新清单核对 ECL 自身文件，再读取本机扩展安装记录；
/// 不执行修复、不启动 Bootstrapper、不修改任何文件。
/// </summary>
public sealed class RuntimeStatusService : IRuntimeStatusService
{
    private readonly IAdditionsService _additions;
    private readonly ILocalizer _localizer;
    private readonly ILogger<RuntimeStatusService> _logger;
    private readonly RuntimeStatusEnvironment _environment;

    public RuntimeStatusService(
        IAdditionsService additions,
        ILocalizer localizer,
        ILogger<RuntimeStatusService>? logger = null,
        RuntimeStatusEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(localizer);

        _additions = additions;
        _localizer = localizer;
        _logger = logger ?? NullLogger<RuntimeStatusService>.Instance;
        _environment = environment ?? RuntimeStatusEnvironment.FromProcess();
    }

    public async Task<RuntimeStatusReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.Now;
        Result<SelfIntegrityReport> verification;
        try
        {
            verification = await new SelfIntegrityVerifier()
                .VerifyAsync(_environment.InstallRoot, verifyHashes: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(exception, "状态中心无法读取安装完整性。");
            return RuntimeStatusReport.Unavailable(_localizer.Get("runtime.unavailable.io")) with
            {
                CheckedAt = checkedAt,
            };
        }

        if (!verification.IsSuccess)
        {
            return RuntimeStatusReport.Unavailable(DescribeVerificationError(verification.Error)) with
            {
                CheckedAt = checkedAt,
            };
        }

        var report = verification.Value!;
        var components = new List<RuntimeComponentStatus>();

        components.Add(new RuntimeComponentStatus(
            _localizer.Get("runtime.trust"),
            MapTrust(report.Trust),
            RuntimeRunState.Stopped,
            DescribeTrust(report.Trust)));

        foreach (var file in report.Files)
        {
            var fileName = Path.GetFileName(file.Path);
            components.Add(new RuntimeComponentStatus(
                string.Equals(fileName, "ECL.exe", StringComparison.OrdinalIgnoreCase)
                    ? _localizer.Get("runtime.main")
                    : fileName is { Length: > 0 } ? fileName : file.Path,
                file.IsIntact ? RuntimeHealth.Healthy : RuntimeHealth.Damaged,
                IsRunningComponent(file.Path) ? RuntimeRunState.Running : RuntimeRunState.Stopped,
                file.IsIntact ? null : DescribeFileStatus(file.Status)));
        }

        if (!report.VersionMatchesManifest)
        {
            components.Add(new RuntimeComponentStatus(
                _localizer.Get("runtime.versionPointer"),
                RuntimeHealth.Damaged,
                RuntimeRunState.Stopped,
                _localizer.Get("runtime.versionMismatch")));
        }

        var complete = true;
        var additionCount = 0;
        try
        {
            var load = _additions.LoadState();
            if (load.IsCorrupt)
            {
                complete = false;
                components.Add(new RuntimeComponentStatus(
                    _localizer.Get("runtime.additions"),
                    RuntimeHealth.Damaged,
                    RuntimeRunState.Unknown,
                    _localizer.Get("runtime.addition.corrupt", load.Error ?? string.Empty)));
            }
            else
            {
                foreach (var addition in load.Document.Additions)
                {
                    additionCount++;
                    var missing = FindMissingAdditionFile(addition);
                    components.Add(new RuntimeComponentStatus(
                        addition.Id.Value,
                        missing is null ? RuntimeHealth.Healthy : RuntimeHealth.Damaged,
                        RuntimeRunState.Stopped,
                        missing is null
                            ? null
                            : _localizer.Get("runtime.addition.missing", missing)));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            complete = false;
            components.Add(new RuntimeComponentStatus(
                _localizer.Get("runtime.additions"),
                RuntimeHealth.Damaged,
                RuntimeRunState.Unknown,
                _localizer.Get("runtime.addition.corrupt", exception.Message)));
        }

        return new RuntimeStatusReport(
            true,
            null,
            report.Version,
            checkedAt,
            components)
        {
            IsComplete = complete,
            CoverageText = _localizer.Get(
                "runtime.coverage",
                report.Files.Count,
                additionCount,
                report.HashChecked ? _localizer.Get("runtime.coverage.hash") : _localizer.Get("runtime.coverage.sizeOnly")),
        };
    }

    /// <summary>把稳定错误码映射为本地化原因；不回显可能带路径与实现细节的领域消息。</summary>
    private string DescribeVerificationError(EclError? error) => error?.Code switch
    {
        "integrity.current_missing" => _localizer.Get("runtime.unavailable.currentMissing"),
        "integrity.manifest_missing" => _localizer.Get("runtime.unavailable.manifestMissing"),
        "integrity.manifest_invalid" => _localizer.Get("runtime.unavailable.manifestInvalid"),
        "integrity.manifest_unreadable" => _localizer.Get("runtime.unavailable.manifestUnreadable"),
        "integrity.install_root_invalid" => _localizer.Get("runtime.unavailable.installRootInvalid"),
        _ => _localizer.Get("runtime.unavailable.io"),
    };

    private RuntimeHealth MapTrust(SelfIntegrityTrustStatus trust) => trust switch
    {
        SelfIntegrityTrustStatus.Verified => RuntimeHealth.Healthy,
        SelfIntegrityTrustStatus.TrustMissing or SelfIntegrityTrustStatus.SignatureMissing => RuntimeHealth.Warning,
        SelfIntegrityTrustStatus.TrustInvalid or SelfIntegrityTrustStatus.SignatureInvalid => RuntimeHealth.Damaged,
        _ => RuntimeHealth.Unknown,
    };

    private string? DescribeTrust(SelfIntegrityTrustStatus trust) => trust switch
    {
        SelfIntegrityTrustStatus.Verified => null,
        SelfIntegrityTrustStatus.TrustMissing => _localizer.Get("runtime.trust.missing"),
        SelfIntegrityTrustStatus.SignatureMissing => _localizer.Get("runtime.trust.signatureMissing"),
        SelfIntegrityTrustStatus.TrustInvalid => _localizer.Get("runtime.trust.invalid"),
        SelfIntegrityTrustStatus.SignatureInvalid => _localizer.Get("runtime.trust.signatureInvalid"),
        _ => _localizer.Get("runtime.reason.unknown"),
    };

    private string DescribeFileStatus(SelfIntegrityFileStatus status) => status switch
    {
        SelfIntegrityFileStatus.Missing => _localizer.Get("runtime.file.missing"),
        SelfIntegrityFileStatus.SizeMismatch => _localizer.Get("runtime.file.sizeMismatch"),
        SelfIntegrityFileStatus.HashMismatch => _localizer.Get("runtime.file.hashMismatch"),
        SelfIntegrityFileStatus.Unreadable => _localizer.Get("runtime.file.unreadable"),
        SelfIntegrityFileStatus.PathEscape => _localizer.Get("runtime.file.pathEscape"),
        _ => _localizer.Get("runtime.reason.unknown"),
    };

    private static bool IsRunningComponent(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        if (string.Equals(fileName, "ECL.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var assemblyName = Path.GetFileNameWithoutExtension(fileName);
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>返回第一个缺失的扩展文件路径；全部存在时返回 null。不读取文件内容。</summary>
    private static string? FindMissingAdditionFile(InstalledAddition addition)
    {
        if (string.IsNullOrWhiteSpace(addition.Directory))
        {
            return addition.EntryAssembly;
        }

        var entry = Path.Combine(addition.Directory, addition.EntryAssembly);
        if (!File.Exists(entry))
        {
            return addition.EntryAssembly;
        }

        foreach (var file in addition.Files)
        {
            if (!File.Exists(Path.Combine(addition.Directory, file.Path)))
            {
                return file.Path;
            }
        }

        return null;
    }
}
