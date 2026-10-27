// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;
using Microsoft.Win32;

namespace Ecl.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);

    string? PickOpenFile(string title, string filter);

    IReadOnlyList<string> PickOpenFiles(string title, string filter);

    string? PickSaveFile(string title, string filter, string? defaultFileName = null);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

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
}
