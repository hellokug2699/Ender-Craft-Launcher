// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Windows;
using Ecl.Core.Localization;
using Ecl.Minecraft.Instances;

namespace Ecl.App.Views;

/// <summary>
/// 把游戏目录 <c>versions</c> 里已有的版本导入为实例。
/// 只建立引用，不复制文件；是否隔离由原目录结构决定。
/// </summary>
public partial class VersionImportDialog : Window
{
    private readonly IInstanceService _instances;
    private readonly ILocalizer _localizer;

    public VersionImportDialog(
        IInstanceService instances,
        IReadOnlyList<DetectedVersion> detected,
        ILocalizer localizer)
    {
        _instances = instances;
        _localizer = localizer;

        InitializeComponent();

        foreach (var version in detected)
        {
            Items.Add(new ImportItem(version, localizer));
        }

        VersionList.ItemsSource = Items;
    }

    public ObservableCollection<ImportItem> Items { get; } = [];

    public int ImportedCount { get; private set; }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var selected = Items.Where(item => item.IsChecked && item.CanImport).ToArray();
        if (selected.Length == 0)
        {
            StatusText.Text = _localizer.Get("instances.importSelectAtLeastOne");
            return;
        }

        var failures = new List<string>();
        foreach (var item in selected)
        {
            var result = await _instances.ImportAsync(item.Version.VersionId).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                ImportedCount++;
                item.MarkImported();
            }
            else
            {
                failures.Add($"{item.Version.VersionId}: {result.Error?.Message}");
            }
        }

        if (failures.Count > 0)
        {
            StatusText.Text = _localizer.Get("instances.importFailed", failures.Count, string.Join("; ", failures));
            return;
        }

        DialogResult = true;
    }

    public sealed class ImportItem : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isChecked;

        public ImportItem(DetectedVersion version, ILocalizer localizer)
        {
            Version = version;
            Title = version.VersionId;
            var isolation = version.Isolated ? localizer.Get("instances.importIsolated") : localizer.Get("instances.importShared");
            Detail = version.AlreadyImported
                ? $"{version.Loader} · {isolation} · {localizer.Get("instances.importAlready")}"
                : $"{version.Loader} · {isolation} · {version.GameDirectory}";
            CanImport = !version.AlreadyImported;
            _isChecked = CanImport;
        }

        public DetectedVersion Version { get; }

        public string Title { get; }

        public string Detail { get; private set; }

        public bool CanImport { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }

                _isChecked = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void MarkImported()
        {
            _isChecked = false;
            Detail = Version.GameDirectory;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Detail)));
        }
    }
}
