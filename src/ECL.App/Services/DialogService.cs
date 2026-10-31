// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
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
    bool Confirm(string title, string message);

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

    bool OpenUrl(string url);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public InstanceDeletionChoice ChooseInstanceDeletion(
        string title,
        string message,
        bool canDeleteManagedData)
    {
        if (!canDeleteManagedData)
        {
            return MessageBox.Show(
                       message,
                       title,
                       MessageBoxButton.OKCancel,
                       MessageBoxImage.Warning) == MessageBoxResult.OK
                ? InstanceDeletionChoice.RemoveRecordOnly
                : InstanceDeletionChoice.Cancel;
        }

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

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

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
}
