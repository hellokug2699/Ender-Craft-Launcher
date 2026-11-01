// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Ecl.Core;

namespace Ecl.Minecraft.Launch;

/// <summary>
/// 每会话 Log4j2 日志隔离（ADR 0015 §4）。只为可识别的 Mojang 默认日志配置派生副本，
/// 把文件 appender 指向会话目录；不修改共享实例文件，不覆盖整合包/用户自定义配置。
/// 派生是否真正生效由启动后的文件观测确认，失败则降级为共享日志并明确标记。
/// </summary>
internal static class Log4jIsolation
{
    private const string ArgumentPrefix = "-Dlog4j.configurationFile=";
    private const string DefaultAppender = "fileName=\"logs/latest.log\"";
    private const string DefaultPattern = "filePattern=\"logs/%d{yyyy-MM-dd}-%i.log.gz\"";

    internal sealed record Plan(
        IReadOnlyList<string> Arguments,
        string Mode,
        string? ConfigPath,
        string? Fingerprint,
        string? Warning);

    public static Result<Plan> Prepare(LaunchCommand command, string sessionDirectory)
    {
        var arguments = command.Arguments.ToList();
        var index = arguments.FindIndex(argument =>
            argument.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return Result<Plan>.Ok(new Plan(arguments, "unavailable", null, null, "启动命令未使用 log4j 配置文件。"));
        }

        var sourcePath = arguments[index][ArgumentPrefix.Length..].Trim('"');
        if (!File.Exists(sourcePath))
        {
            return Result<Plan>.Ok(new Plan(arguments, "unavailable", null, null, "日志配置不存在。"));
        }

        // 只隔离可识别的 Mojang 默认配置；自定义/整合包配置保持原样。
        string content;
        try
        {
            content = File.ReadAllText(sourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<Plan>.Ok(new Plan(arguments, "unavailable", null, null, "无法读取日志配置。"));
        }

        var fileName = Path.GetFileName(sourcePath);
        if (!fileName.StartsWith("client-", StringComparison.OrdinalIgnoreCase) ||
            !content.Contains(DefaultAppender, StringComparison.Ordinal) ||
            !content.Contains(DefaultPattern, StringComparison.Ordinal))
        {
            return Result<Plan>.Ok(new Plan(
                arguments,
                "shared",
                null,
                null,
                "日志配置不是可识别的 Mojang 默认格式，保留共享 latest.log。"));
        }

        var sessionLog = Path.Combine(sessionDirectory, "game.log");
        var sessionPattern = Path.Combine(sessionDirectory, "rolled", "%d{yyyy-MM-dd}-%i.log.gz");
        var derived = content
            .Replace(DefaultAppender, $"fileName=\"{Escape(sessionLog)}\"", StringComparison.Ordinal)
            .Replace(DefaultPattern, $"filePattern=\"{Escape(sessionPattern)}\"", StringComparison.Ordinal);

        var configPath = Path.Combine(sessionDirectory, "log4j2.xml");
        try
        {
            Directory.CreateDirectory(sessionDirectory);
            File.WriteAllText(configPath, derived, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<Plan>.Ok(new Plan(arguments, "shared", null, null, $"无法写入会话日志配置：{exception.Message}"));
        }

        arguments[index] = ArgumentPrefix + configPath;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(derived)))[..32].ToLowerInvariant();
        return Result<Plan>.Ok(new Plan(arguments, "pending", configPath, fingerprint, null));
    }

    private static string Escape(string path) => path.Replace("&", "&amp;").Replace("\"", "&quot;");
}
