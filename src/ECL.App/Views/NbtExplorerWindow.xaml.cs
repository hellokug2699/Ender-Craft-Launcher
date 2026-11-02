// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Windows;
using Ecl.App.ViewModels;
using Ecl.World.Nbt;

namespace Ecl.App.Views;

public partial class NbtExplorerWindow : Window
{
    public NbtExplorerWindow(string filePath)
    {
        InitializeComponent();
        FilePathText.Text = filePath;
        try
        {
            var root = NbtFile.ReadFile(filePath);
            DataContext = new NbtExplorerViewModel(filePath, NbtNodeViewModel.FromTag(root), null);
        }
        catch (Exception exception) when (exception is NbtFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DataContext = new NbtExplorerViewModel(
                filePath,
                null,
                $"读取失败：{exception.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class NbtExplorerViewModel(string filePath, NbtNodeViewModel? root, string? errorMessage)
{
    public string FilePath { get; } = filePath;

    public NbtNodeViewModel? Root { get; } = root;

    public string? ErrorMessage { get; } = errorMessage;

    public bool HasError => ErrorMessage is not null;
}
