// SPDX-License-Identifier: GPL-3.0-or-later

using System.Windows;

namespace Ecl.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message);

    void ShowError(string title, string message);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
