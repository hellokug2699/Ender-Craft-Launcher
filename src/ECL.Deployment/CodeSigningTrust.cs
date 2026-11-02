// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Ecl.Core;

namespace Ecl.Deployment;

public sealed record CodeSigningTrustInfo(
    string Subject,
    string Thumbprint,
    string Sha256Fingerprint,
    DateTimeOffset? NotAfter,
    bool IsTrusted,
    string CertificatePath,
    string? Error)
{
    public bool IsAvailable => Error is null;
}

/// <summary>
/// 用户授权的自签名 Authenticode 证书信任。只信任固定指纹的叶证书到
/// <c>TrustedPeople</c>，不安装根证书、不静默导入；拒绝或 UAC 取消不影响 ECL 使用。
/// </summary>
public sealed class CodeSigningTrustService(string? trustDirectory = null)
{
    public const string TrustFileName = "code-signing-trust.json";

    private readonly string _directory = trustDirectory ?? AppContext.BaseDirectory;

    public Result<CodeSigningTrustInfo> Inspect()
    {
        var trustPath = Path.Combine(_directory, TrustFileName);
        if (!File.Exists(trustPath))
        {
            return Result<CodeSigningTrustInfo>.Fail("signing.trust_missing", "未随程序提供代码签名信任信息。");
        }

        try
        {
            using var stream = File.OpenRead(trustPath);
            var document = JsonSerializer.Deserialize<CodeSigningTrustDocument>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (document is null ||
                string.IsNullOrWhiteSpace(document.Certificate) ||
                string.IsNullOrWhiteSpace(document.Sha256Fingerprint))
            {
                return Result<CodeSigningTrustInfo>.Fail("signing.trust_invalid", "代码签名信任信息格式无效。");
            }

            if (!UpdatePathSafety.TryNormalizeRelativePath(document.Certificate, out var certificateRelative))
            {
                return Result<CodeSigningTrustInfo>.Fail("signing.trust_invalid", "代码签名信任信息包含无效证书路径。");
            }

            var certificatePath = Path.GetFullPath(Path.Combine(_directory, certificateRelative));
            if (!UpdatePathSafety.IsContainedIn(_directory, certificatePath) || !File.Exists(certificatePath))
            {
                return Result<CodeSigningTrustInfo>.Fail("signing.certificate_missing", "未找到公开证书文件。");
            }

            using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
            if (!string.Equals(fingerprint, document.Sha256Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Result<CodeSigningTrustInfo>.Fail(
                    "signing.fingerprint_mismatch",
                    "证书指纹与固定信任信息不一致，拒绝使用。");
            }

            return Result<CodeSigningTrustInfo>.Ok(new CodeSigningTrustInfo(
                certificate.Subject,
                certificate.Thumbprint,
                fingerprint,
                DateTimeOffset.TryParse(certificate.NotAfter.ToUniversalTime().ToString("O"), out var notAfter)
                    ? notAfter
                    : null,
                IsTrusted(certificate.Thumbprint),
                certificatePath,
                null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return Result<CodeSigningTrustInfo>.Fail("signing.trust_invalid", $"读取代码签名信任信息失败：{exception.Message}");
        }
    }

    public Result Trust(string expectedSha256Fingerprint)
    {
        var inspected = Inspect();
        if (!inspected.IsSuccess)
        {
            return Result.Fail(inspected.Error!);
        }

        var info = inspected.Value!;
        if (!string.Equals(info.Sha256Fingerprint, expectedSha256Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail("signing.fingerprint_mismatch", "证书指纹与用户确认的指纹不一致。");
        }

        if (info.IsTrusted)
        {
            return Result.Ok();
        }

        return RunCertutil(["-addstore", "-f", "TrustedPeople", info.CertificatePath]);
    }

    public Result Revoke(string expectedSha256Fingerprint)
    {
        var inspected = Inspect();
        if (!inspected.IsSuccess)
        {
            return Result.Fail(inspected.Error!);
        }

        var info = inspected.Value!;
        if (!string.Equals(info.Sha256Fingerprint, expectedSha256Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail("signing.fingerprint_mismatch", "证书指纹与用户确认的指纹不一致。");
        }

        if (!info.IsTrusted)
        {
            return Result.Ok();
        }

        return RunCertutil(["-delstore", "TrustedPeople", info.Thumbprint]);
    }

    public static bool IsTrusted(string thumbprint)
    {
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            try
            {
                using var store = new X509Store(StoreName.TrustedPeople, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                if (store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count > 0)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is CryptographicException or UnauthorizedAccessException)
            {
                // 无法读取某个存储时继续检查另一个。
            }
        }

        return false;
    }

    private static Result RunCertutil(IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("certutil.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Result.Fail("signing.certutil_failed", "无法启动证书导入工具。");
            }

            process.WaitForExit();
            return process.ExitCode == 0
                ? Result.Ok()
                : Result.Fail("signing.certutil_failed", $"证书操作失败（退出码 {process.ExitCode}）。");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Result.Fail("signing.cancelled", "证书信任操作已取消或无法提升权限；ECL 仍可使用现有更新签名验证。");
        }
    }

    public sealed record CodeSigningTrustDocument
    {
        public string Certificate { get; init; } = string.Empty;

        public string Subject { get; init; } = string.Empty;

        public string Sha256Fingerprint { get; init; } = string.Empty;
    }
}
