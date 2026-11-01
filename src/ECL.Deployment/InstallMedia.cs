// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Deployment;

/// <summary>
/// 安装介质契约（ADR 0017）：Bootstrapper、信任库、Manifest、签名与完整包必须同时存在，
/// 且签名与哈希验证由 Bootstrapper 执行，安装器不自行信任任何内容。
/// </summary>
public sealed record InstallMedia(
    string Directory,
    string BootstrapperPath,
    string TrustPath,
    string ManifestPath,
    string SignaturePath,
    string PackagePath)
{
    public const string DefaultPackageName = "ECL-package.zip";

    public static Result<InstallMedia> Inspect(string directory, string? packagePath = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Result<InstallMedia>.Fail("installer.media.empty", "安装介质目录不能为空。");
        }

        string fullDirectory;
        try
        {
            fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<InstallMedia>.Fail("installer.media.invalid", $"安装介质目录无效：{exception.Message}");
        }

        if (!System.IO.Directory.Exists(fullDirectory))
        {
            return Result<InstallMedia>.Fail("installer.media.missing", "安装介质目录不存在。", fullDirectory);
        }

        var bootstrapper = Path.Combine(fullDirectory, "ECL.Bootstrapper.exe");
        var trust = Path.Combine(fullDirectory, "update-trust.json");
        var manifest = Path.Combine(fullDirectory, "update-manifest.json");
        var signature = Path.Combine(fullDirectory, "update-manifest.sig");
        var package = string.IsNullOrWhiteSpace(packagePath)
            ? Path.Combine(fullDirectory, DefaultPackageName)
            : Path.GetFullPath(packagePath);

        foreach (var (path, code, name) in new[]
                 {
                     (bootstrapper, "installer.media.bootstrapper", "ECL.Bootstrapper.exe"),
                     (trust, "installer.media.trust", "update-trust.json"),
                     (manifest, "installer.media.manifest", "update-manifest.json"),
                     (signature, "installer.media.signature", "update-manifest.sig"),
                     (package, "installer.media.package", Path.GetFileName(package)),
                 })
        {
            if (!File.Exists(path))
            {
                return Result<InstallMedia>.Fail(code, $"安装介质不完整：缺少 {name}。", path);
            }
        }

        return Result<InstallMedia>.Ok(new InstallMedia(
            fullDirectory,
            bootstrapper,
            trust,
            manifest,
            signature,
            package));
    }
}

public sealed record InstallOptions(string InstallRoot, InstallMedia Media);

public sealed record InstallProgress(string Stage, string? Detail = null, int? Percent = null);

public sealed record InstallOutcome(int ExitCode, string? LogPath)
{
    public bool Success => ExitCode == 0;

    public string DescribeFailure() => ExitCode switch
    {
        2 => "安装参数无效。",
        3 => "签名或信任库验证失败，已拒绝安装。",
        4 => "部署失败，当前版本未被替换。",
        5 => "安装成功但启动失败，可使用 ECL.Bootstrapper 回滚。",
        _ => $"Bootstrapper 以退出码 {ExitCode} 结束。",
    };
}
