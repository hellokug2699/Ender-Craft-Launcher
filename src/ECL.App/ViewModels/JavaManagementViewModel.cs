// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Java;

namespace Ecl.App.ViewModels;

/// <summary>
/// Java runtime inventory and global priority editor. Discovery is lazy and cached for the
/// lifetime of this view model; opening Settings does not trigger a disk scan.
/// </summary>
public sealed partial class JavaManagementViewModel(
    IJavaLocator locator,
    ISettingsService settings,
    IDialogService dialogs,
    ILocalizer localizer) : ObservableObject
{
    private const string PriorityKey = "java.defaultPath";
    private bool _loaded;

    public ObservableCollection<JavaRuntimeEntryViewModel> Runtimes { get; } = [];

    [ObservableProperty]
    private JavaRuntimeEntryViewModel? _selectedRuntime;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public async Task ActivateAsync()
    {
        if (_loaded)
        {
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = localizer.Get("java.manager.scanning");
        try
        {
            var configured = JavaSearchPath.Split(settings.Get<string>(PriorityKey));
            var discovered = await locator.FindAllAsync().ConfigureAwait(true);
            var ordered = OrderByPriority(discovered, configured);
            Runtimes.Clear();
            foreach (var runtime in ordered)
            {
                var entry = new JavaRuntimeEntryViewModel(runtime, configured.Any(path => Matches(runtime, path)));
                entry.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(JavaRuntimeEntryViewModel.IsPriority))
                    {
                        SavePriority();
                    }
                };
                Runtimes.Add(entry);
            }

            _loaded = true;
            StatusMessage = localizer.Get("java.manager.count", Runtimes.Count);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void MoveUp(JavaRuntimeEntryViewModel? entry) => Move(entry, -1);

    [RelayCommand]
    private void MoveDown(JavaRuntimeEntryViewModel? entry) => Move(entry, 1);

    [RelayCommand]
    private void Remove(JavaRuntimeEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        entry.IsPriority = false;
        StatusMessage = localizer.Get("java.manager.removed");
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = dialogs.PickOpenFile(
            localizer.Get("java.manager.importTitle"),
            OperatingSystem.IsWindows() ? "Java executable|java.exe|Executable|*.exe|All files|*.*" : "Java executable|java|All files|*");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var runtime = locator.FromPath(path);
        if (runtime is null)
        {
            dialogs.ShowError(localizer.Get("java.manager.title"), localizer.Get("java.manager.invalidRuntime"));
            return;
        }

        var existing = Runtimes.FirstOrDefault(item => SamePath(item.Runtime.ExecutablePath, runtime.ExecutablePath));
        if (existing is null)
        {
            var imported = new JavaRuntimeEntryViewModel(runtime, true);
            imported.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(JavaRuntimeEntryViewModel.IsPriority))
                {
                    SavePriority();
                }
            };
            Runtimes.Add(imported);
        }
        else
        {
            existing.IsPriority = true;
        }

        var selected = Runtimes.Where(item => item.IsPriority).ToList();
        var nonPriority = Runtimes.Where(item => !item.IsPriority).ToList();
        Runtimes.Clear();
        foreach (var item in selected.Concat(nonPriority))
        {
            Runtimes.Add(item);
        }

        SavePriority();
        SelectedRuntime = Runtimes.FirstOrDefault(item => SamePath(item.Runtime.ExecutablePath, runtime.ExecutablePath));
        StatusMessage = localizer.Get("java.manager.imported", runtime.DisplayName);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenFolder(JavaRuntimeEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.Runtime.HomePath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private void Move(JavaRuntimeEntryViewModel? entry, int offset)
    {
        if (entry is null)
        {
            return;
        }

        var index = Runtimes.IndexOf(entry);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Runtimes.Count)
        {
            return;
        }

        Runtimes.Move(index, target);
        SavePriority();
    }

    private void SavePriority()
    {
        var paths = Runtimes.Where(item => item.IsPriority).Select(item => item.Runtime.ExecutablePath);
        settings.TrySetRaw(PriorityKey, string.Join(';', paths), null, out _);
    }

    private static IReadOnlyList<JavaRuntime> OrderByPriority(
        IReadOnlyList<JavaRuntime> discovered,
        IReadOnlyList<string> configured)
    {
        var result = new List<JavaRuntime>();
        foreach (var path in configured)
        {
            var runtime = discovered.FirstOrDefault(item => Matches(item, path));
            if (runtime is not null && !result.Any(item => SamePath(item.ExecutablePath, runtime.ExecutablePath)))
            {
                result.Add(runtime);
            }
        }

        result.AddRange(discovered.Where(item => !result.Any(existing =>
            SamePath(existing.ExecutablePath, item.ExecutablePath))));
        return result;
    }

    private static bool Matches(JavaRuntime runtime, string path) =>
        SamePath(runtime.ExecutablePath, path) ||
        SamePath(runtime.HomePath, path) ||
        runtime.ExecutablePath.StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed partial class JavaRuntimeEntryViewModel(JavaRuntime runtime, bool isPriority) : ObservableObject
{
    public JavaRuntime Runtime { get; } = runtime;

    public string Name => Runtime.DisplayName;

    public string Version => Runtime.Version?.Raw ?? "Unknown";

    public string Vendor => Runtime.Vendor ?? "Unknown";

    public string Architecture => Runtime.Architecture ?? "Unknown";

    public string Path => Runtime.ExecutablePath;

    [ObservableProperty]
    private bool _isPriority = isPriority;
}
