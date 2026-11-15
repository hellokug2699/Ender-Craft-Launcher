// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json;

namespace Ecl.Deployment;

public enum InstallerIntegrityState
{
    Confirmed,
    Mismatch,
    Unavailable,
    Unverified,
}

public sealed record InstallerIntegrityResult(
    string FilePath,
    string FileName,
    string? Version,
    string LocalSha256,
    string? OfficialSha256,
    InstallerIntegrityState State,
    string Message)
{
    public string PowerShellCommand => $"Get-FileHash -LiteralPath \"{FilePath}\" -Algorithm SHA256";
}

/// <summary>
/// 首次安装完整性自检。只展示本地哈希与发布侧记录；是否一致由用户逐字符确认，
/// 程序不得把“跳过/摘要缺失”显示为验证成功。
/// </summary>
public static class InstallerIntegrity
{
    public const string ChecksumFileName = "installer-integrity.json";

    public static async Task<InstallerIntegrityResult> InspectAsync(
        string mediaDirectory,
        string? executablePath = null,
        CancellationToken cancellationToken = default)
    {
        var executable = executablePath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            executable = Environment.ProcessPath;
        }

        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            executable = Path.Combine(AppContext.BaseDirectory, "ECL.Installer.exe");
        }

        var fileName = Path.GetFileName(executable);
        var version = File.Exists(executable)
            ? FileVersionInfo.GetVersionInfo(executable).ProductVersion
            : null;
        var localHash = File.Exists(executable)
            ? await UpdateHash.ComputeSha256Async(executable, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var sidecarPath = executable + ".sha256";
        if (File.Exists(sidecarPath))
        {
            try
            {
                var sidecar = await File.ReadAllTextAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
                var fields = sidecar.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var sidecarHash = fields.FirstOrDefault();
                var sidecarFile = fields.Length > 1 ? Path.GetFileName(fields[1]) : fileName;
                if (sidecarHash is null ||
                    sidecarHash.Length != 64 ||
                    !sidecarHash.All(Uri.IsHexDigit) ||
                    !string.Equals(sidecarFile, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return new InstallerIntegrityResult(
                        executable,
                        fileName,
                        version,
                        localHash,
                        null,
                        InstallerIntegrityState.Unavailable,
                        "旁置 SHA-256 文件格式无效，或摘要对应的文件名与当前安装程序不一致。");
                }

                var sidecarMatches = string.Equals(localHash, sidecarHash, StringComparison.OrdinalIgnoreCase);
                return new InstallerIntegrityResult(
                    executable,
                    fileName,
                    version,
                    localHash,
                    sidecarHash,
                    sidecarMatches ? InstallerIntegrityState.Confirmed : InstallerIntegrityState.Mismatch,
                    sidecarMatches
                        ? "旁置官方 SHA-256 与本地文件一致。请仍使用 PowerShell 独立计算并逐字符核对后再确认。"
                        : "本地 SHA-256 与旁置官方摘要不一致。建议取消安装并从官方 Release 重新下载。");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new InstallerIntegrityResult(
                    executable,
                    fileName,
                    version,
                    localHash,
                    null,
                    InstallerIntegrityState.Unavailable,
                    $"读取旁置 SHA-256 文件失败：{exception.Message}");
            }
        }

        var checksumPath = Path.Combine(mediaDirectory, ChecksumFileName);
        if (!File.Exists(checksumPath))
        {
            return new InstallerIntegrityResult(
                executable,
                fileName,
                version,
                localHash,
                null,
                InstallerIntegrityState.Unavailable,
                "安装介质中没有官方 SHA-256 记录，无法自动比对；必须由用户核对或显式跳过。");
        }

        try
        {
            using var stream = File.OpenRead(checksumPath);
            var record = await JsonSerializer.DeserializeAsync<InstallerChecksumRecord>(
                             stream,
                             new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                             cancellationToken)
                         .ConfigureAwait(false);
            if (record is null || string.IsNullOrWhiteSpace(record.Sha256) || string.IsNullOrWhiteSpace(record.File))
            {
                return new InstallerIntegrityResult(
                    executable,
                    fileName,
                    record?.Version ?? version,
                    localHash,
                    null,
                    InstallerIntegrityState.Unavailable,
                    "官方 SHA-256 记录格式无效。");
            }

            if (!string.Equals(record.File, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return new InstallerIntegrityResult(
                    executable,
                    fileName,
                    record.Version ?? version,
                    localHash,
                    record.Sha256,
                    InstallerIntegrityState.Unavailable,
                    "官方摘要对应的文件名与当前安装程序不一致。");
            }

            var matches = string.Equals(localHash, record.Sha256, StringComparison.OrdinalIgnoreCase);
            return new InstallerIntegrityResult(
                executable,
                fileName,
                record.Version ?? version,
                localHash,
                record.Sha256,
                matches ? InstallerIntegrityState.Confirmed : InstallerIntegrityState.Mismatch,
                matches
                    ? "程序自检结果一致。请仍使用 PowerShell 独立计算并逐字符核对后再确认。"
                    : "程序自检发现本地 SHA-256 与官方记录不一致。建议取消安装并从官方 Release 重新下载。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new InstallerIntegrityResult(
                executable,
                fileName,
                version,
                localHash,
                null,
                InstallerIntegrityState.Unavailable,
                $"读取官方摘要失败：{exception.Message}");
        }
    }

    public sealed record InstallerChecksumRecord
    {
        public string File { get; init; } = string.Empty;

        public string? Version { get; init; }

        public string Sha256 { get; init; } = string.Empty;
    }
}
