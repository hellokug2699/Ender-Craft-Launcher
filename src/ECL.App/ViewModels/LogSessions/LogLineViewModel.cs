// SPDX-License-Identifier: GPL-3.0-or-later

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Ecl.App.ViewModels.LogSessions;

/// <summary>
/// 一条已加载的日志行。行号是当前来源窗口的显示序号，不代表原始文件行号；
/// 原始文本始终保留，解析失败的行也照常显示。
/// </summary>
public sealed partial class LogLineViewModel : ObservableObject
{
    private readonly ParsedLogLine _parsed;

    public LogLineViewModel(int displayNumber, ParsedLogLine parsed)
    {
        _parsed = parsed;
        _displayNumber = displayNumber;
    }

    [ObservableProperty]
    private int _displayNumber;

    [ObservableProperty]
    private bool _isMatch;

    [ObservableProperty]
    private bool _isCurrentMatch;

    public DateTimeOffset? Timestamp => _parsed.Timestamp;

    public LogLevel Level => _parsed.Level;

    public string LevelText => LogLineParser.LevelText(_parsed.Level);

    public string TimestampText => _parsed.Timestamp is { } timestamp
        ? timestamp.ToLocalTime().ToString("HH:mm:ss.fff")
        : string.Empty;

    public string Thread => _parsed.Thread ?? string.Empty;

    public string Logger => _parsed.Logger ?? string.Empty;

    public string Message => _parsed.DisplayMessage;

    public string Raw => _parsed.Raw;

    public bool IsParsed => _parsed.IsParsed;

    public bool IsWarning => Level is LogLevel.Warning;

    public bool IsError => Level is LogLevel.Error or LogLevel.Critical;

    /// <summary>复制整行时使用原始文本，而不是解析后的替换形式。</summary>
    public string CopyText => _parsed.Raw;
}
