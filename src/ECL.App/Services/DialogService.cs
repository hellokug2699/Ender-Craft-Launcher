// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Ecl.App.Dialogs;
using Ecl.App.Dialogs.ViewModels;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Java;
using Microsoft.Win32;

namespace Ecl.App.Services;

public enum InstanceDeletionChoice
{
    Cancel,
    RemoveRecordOnly,
    DeleteManagedData,
}

public interface IDialogService
{
    bool Confirm(string title, string message, string? acknowledgementToken = null);

    /// <summary>
    /// 展示删除计划并选择删除模式：
    /// “是”=同时删除 ECL 托管数据，“否”=仅移除记录，“取消”=放弃。
    /// 没有托管数据时只提供“仅移除记录 / 取消”。
    /// </summary>
    InstanceDeletionChoice ChooseInstanceDeletion(string title, string message, bool canDeleteManagedData);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);

    string? PickOpenFile(string title, string filter);

    IReadOnlyList<string> PickOpenFiles(string title, string filter);

    string? PickSaveFile(string title, string filter, string? defaultFileName = null);

    /// <summary>选择文件夹（Windows 原生对话框）；取消返回 null。</summary>
    string? PickFolder(string title);

    bool OpenUrl(string url);
}

/// <summary>
/// 兼容层：现有同步调用继续可用，但确认/提示类弹窗统一走 <see cref="IDialogEngine"/>（ADR 0052）。
/// 主窗口弹窗宿主尚未附加时（例如首次启动向导、启动失败）回退到系统 MessageBox，
/// 文件选择仍使用 Windows 原生对话框。
/// </summary>
public sealed class DialogService(
    ISettingsService settings,
    IDialogEngine dialogs,
    ILocalizer localizer) : IDialogService
{
    public bool Confirm(string title, string message, string? acknowledgementToken = null)
    {
        // 使用稳定风险标识而不是显示文案；语言切换不应触发重复确认。
        // token 由 Core 计算：规范化路径 + 检测版本 + 有效要求指纹。
        var savedAcknowledgement = settings.Get<string?>(JavaRiskIdentity.SettingsKey);
        if (!string.IsNullOrWhiteSpace(acknowledgementToken) &&
            string.Equals(savedAcknowledgement, acknowledgementToken, StringComparison.Ordinal))
        {
            return true;
        }

        var accepted = ShowMessage(
            title,
            message,
            localizer.Get("dialog.yes"),
            cancelText: localizer.Get("dialog.no"),
            DialogSeverity.Warning);

        if (accepted && !string.IsNullOrWhiteSpace(acknowledgementToken))
        {
            settings.TrySetRaw(JavaRiskIdentity.SettingsKey, acknowledgementToken, null, out _);
        }

        return accepted;
    }

    public InstanceDeletionChoice ChooseInstanceDeletion(
        string title,
        string message,
        bool canDeleteManagedData)
    {
        if (!canDeleteManagedData)
        {
            return ShowMessage(
                       title,
                       message,
                       localizer.Get("dialog.removeRecordOnly"),
                       cancelText: localizer.Get("dialog.cancel"),
                       DialogSeverity.Warning)
                ? InstanceDeletionChoice.RemoveRecordOnly
                : InstanceDeletionChoice.Cancel;
        }

        var result = dialogs.IsAvailable
            ? dialogs.Show(new DialogRequest
            {
                Title = title,
                Content = new MessageDialogViewModel(
                    message,
                    localizer.Get("dialog.deleteManagedData"),
                    localizer.Get("dialog.removeRecordOnly"),
                    localizer.Get("dialog.cancel"),
                    DialogSeverity.Warning,
                    isDestructive: true),
                InitialFocus = DialogInitialFocus.Cancel,
                Severity = DialogSeverity.Warning,
            })
            : null;

        if (result is null)
        {
            return MessageBox.Show(
                       message,
                       title,
                       MessageBoxButton.YesNoCancel,
                       MessageBoxImage.Warning) switch
            {
                MessageBoxResult.Yes => InstanceDeletionChoice.DeleteManagedData,
                MessageBoxResult.No => InstanceDeletionChoice.RemoveRecordOnly,
                _ => InstanceDeletionChoice.Cancel,
            };
        }

        return result.Outcome switch
        {
            DialogOutcome.Primary => InstanceDeletionChoice.DeleteManagedData,
            DialogOutcome.Secondary => InstanceDeletionChoice.RemoveRecordOnly,
            _ => InstanceDeletionChoice.Cancel,
        };
    }

    public void ShowInfo(string title, string message) =>
        ShowMessage(title, message, localizer.Get("dialog.ok"), severity: DialogSeverity.Information);

    public void ShowError(string title, string message) =>
        ShowMessage(title, message, localizer.Get("dialog.ok"), severity: DialogSeverity.Error);

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickOpenFiles(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickSaveFile(string title, string filter, string? defaultFileName = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName ?? string.Empty,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>返回主操作是否被选择；关闭弹窗或取消都视为否。</summary>
    private bool ShowMessage(
        string title,
        string message,
        string primaryText,
        string? cancelText = null,
        DialogSeverity severity = DialogSeverity.Neutral)
    {
        if (dialogs.IsAvailable)
        {
            try
            {
                var result = dialogs.Show(new DialogRequest
                {
                    Title = title,
                    Content = new MessageDialogViewModel(message, primaryText, cancelText: cancelText, severity: severity),
                    InitialFocus = cancelText is null ? DialogInitialFocus.FirstFocusable : DialogInitialFocus.Cancel,
                    Severity = severity,
                });
                return result.IsPrimary;
            }
            catch (DialogHostUnavailableException)
            {
                // 宿主在检查与显示之间被卸载：回退到兼容路径，不能让用户操作静默丢失。
            }
        }

        var fallbackResult = cancelText is null
            ? MessageBox.Show(message, title, MessageBoxButton.OK, ToImage(severity))
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, ToImage(severity));
        return fallbackResult is MessageBoxResult.OK or MessageBoxResult.Yes;
    }

    private static MessageBoxImage ToImage(DialogSeverity severity) => severity switch
    {
        DialogSeverity.Error => MessageBoxImage.Error,
        DialogSeverity.Warning => MessageBoxImage.Warning,
        DialogSeverity.Information => MessageBoxImage.Information,
        _ => MessageBoxImage.None,
    };
}
