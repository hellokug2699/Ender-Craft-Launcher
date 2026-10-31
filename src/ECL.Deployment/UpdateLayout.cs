// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;

namespace Ecl.Deployment;

/// <summary>
/// 安装目录布局：
/// <code>
/// &lt;install&gt;/
/// ├─ ECL.Bootstrapper.exe
/// ├─ update-trust.json
/// ├─ current.json
/// ├─ versions/&lt;version&gt;/
/// └─ staging/
/// </code>
/// 用户数据目录（实例、账户、配置）不在此布局内，升级/回滚/卸载都不得触碰。
/// </summary>
public sealed class UpdateLayout
{
    public UpdateLayout(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            throw new ArgumentException("安装目录不能为空。", nameof(installRoot));
        }

        InstallRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        VersionsDirectory = Path.Combine(InstallRoot, "versions");
        StagingDirectory = Path.Combine(InstallRoot, "staging");
        CurrentFile = Path.Combine(InstallRoot, "current.json");
        TrustFile = Path.Combine(InstallRoot, "update-trust.json");
        BootstrapExecutable = Path.Combine(InstallRoot, "ECL.Bootstrapper.exe");
    }

    public string InstallRoot { get; }

    public string VersionsDirectory { get; }

    public string StagingDirectory { get; }

    public string CurrentFile { get; }

    public string TrustFile { get; }

    public string BootstrapExecutable { get; }

    public string GetVersionDirectory(ReleaseVersion version)
    {
        var directory = Path.GetFullPath(Path.Combine(VersionsDirectory, version.ToString()));
        if (!UpdatePathSafety.IsContainedIn(VersionsDirectory, directory) ||
            !string.Equals(
                Path.GetDirectoryName(directory),
                Path.TrimEndingDirectorySeparator(VersionsDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("版本目录越界。");
        }

        return directory;
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(InstallRoot);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(StagingDirectory);
    }

    public async Task<Result<CurrentVersion>> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(CurrentFile))
        {
            return Result<CurrentVersion>.Fail(
                "update.current_missing",
                "未找到当前版本指针。",
                CurrentFile);
        }

        try
        {
            await using var stream = File.OpenRead(CurrentFile);
            var current = await JsonSerializer.DeserializeAsync<CurrentVersion>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (current is null ||
                !ReleaseVersion.TryParse(current.Version, out var version) ||
                !UpdatePathSafety.TryNormalizeRelativePath(current.Executable, out var executable))
            {
                return Result<CurrentVersion>.Fail(
                    "update.current_invalid",
                    "当前版本指针格式无效。",
                    CurrentFile);
            }

            return Result<CurrentVersion>.Ok(current with
            {
                ParsedVersion = version,
                Executable = executable,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Result<CurrentVersion>.Fail(
                "update.current_invalid",
                "读取当前版本指针失败。",
                exception.Message);
        }
    }

    public async Task<Result> WriteCurrentAsync(
        CurrentVersion current,
        CancellationToken cancellationToken = default)
    {
        if (!UpdatePathSafety.TryNormalizeRelativePath(current.Executable, out var executable))
        {
            return Result.Fail("update.current_invalid", "当前版本指针的可执行文件路径无效。");
        }

        var payload = current with { Executable = executable };
        var temporary = $"{CurrentFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(InstallRoot);
            await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(payload, JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, CurrentFile, overwrite: true);
            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(
                "update.current_write_failed",
                "写入当前版本指针失败。",
                exception.Message);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // 清理临时文件失败不影响主结果。
                }
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };
}

public sealed record CurrentVersion
{
    public string Version { get; init; } = string.Empty;

    public string Executable { get; init; } = "ECL.exe";

    public string? PreviousVersion { get; init; }

    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public ReleaseVersion? ParsedVersion { get; init; }
}
