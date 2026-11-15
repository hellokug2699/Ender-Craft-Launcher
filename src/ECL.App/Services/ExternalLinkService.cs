// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.App.Services;

/// <summary>
/// 打开外部网址或随包文本文件。关于页只通过这里打开项目仓库、文档、许可证与贡献者页面；
/// 打开失败只记录原因并把状态交回页面，不抛出、不阻塞页面或启动流程。
/// </summary>
public interface IExternalLinkService
{
    bool Open(string target);
}

public sealed class ExternalLinkService : IExternalLinkService
{
    private readonly ILogger<ExternalLinkService> _logger;

    public ExternalLinkService(ILogger<ExternalLinkService>? logger = null) =>
        _logger = logger ?? NullLogger<ExternalLinkService>.Instance;

    public bool Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(exception, "无法打开外部目标：{Target}", target);
            return false;
        }
    }
}
