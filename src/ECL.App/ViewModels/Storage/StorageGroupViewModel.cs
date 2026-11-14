// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Ecl.Core.Localization;

namespace Ecl.App.ViewModels.Storage;

/// <summary>固定分组：Minecraft 实例内容、ECL 自身文件、扩展数据、备份。</summary>
public sealed class StorageGroupViewModel
{
    public StorageGroupViewModel(string title, IEnumerable<StorageCategoryViewModel> categories)
    {
        Title = title;
        Categories = new ObservableCollection<StorageCategoryViewModel>(categories);
    }

    public string Title { get; }

    public ObservableCollection<StorageCategoryViewModel> Categories { get; }
}
