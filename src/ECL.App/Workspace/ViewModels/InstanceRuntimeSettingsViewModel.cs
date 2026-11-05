// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ecl.App.Services;
using Ecl.App.ViewModels;
using Ecl.Configuration.Settings;
using Ecl.Core.Localization;
using Ecl.Download;
using Ecl.Java;
using Ecl.Minecraft.Instances;
using Ecl.Minecraft.Java;
using Ecl.Minecraft.Versions;

namespace Ecl.App.Workspace.ViewModels;

/// <summary>
/// 一个实例工作区自有的 Java 覆盖、内存与 JVM 设置（B2 步骤 1c）。
///
/// 这些字段是可编辑的页面状态：两个实例工作区同时打开时，各自的编辑内容、Java 选择与
/// 兼容性文案必须互不影响。持久化仍走既有的实例作用域设置键
/// （<c>instance.java.*</c>，以稳定 InstanceId 区分），领域判断仍由
/// <see cref="IJavaSelectionService"/> 给出；本类只持有 UI 状态并调用服务。
///
/// 生命周期由 <see cref="InstanceDetailPageViewModel"/> 持有，关闭工作区时释放。
/// </summary>
public sealed partial class InstanceRuntimeSettingsViewModel : ObservableObject, IDisposable
{
    private readonly GameInstance _instance;
    private readonly ISettingsService _settings;
    private readonly IJavaLocator _javaLocator;
    private readonly IJavaSelectionService _javaSelection;
    private readonly IJavaInstallService _javaInstall;
    private readonly IVersionResolver _versionResolver;
    private readonly IDownloadController _downloads;
    private readonly IDialogService _dialogs;
    private readonly ILocalizer _localizer;
    private bool _suppressJavaSelection;
    private bool _isDownloadingJava;

    public InstanceRuntimeSettingsViewModel(
        GameInstance instance,
        ISettingsService settings,
        IJavaLocator javaLocator,
        IJavaSelectionService javaSelection,
        IJavaInstallService javaInstall,
        IVersionResolver versionResolver,
        IDownloadController downloads,
        IDialogService dialogs,
        ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _instance = instance;
        _settings = settings;
        _javaLocator = javaLocator;
        _javaSelection = javaSelection;
        _javaInstall = javaInstall;
        _versionResolver = versionResolver;
        _downloads = downloads;
        _dialogs = dialogs;
        _localizer = localizer;

        PresetOptions =
        [
            new JvmPresetOptionViewModel(string.Empty, _localizer.Get("common.inherit")),
            new JvmPresetOptionViewModel("default", _localizer.Get("option.java.jvmPreset.default")),
            new JvmPresetOptionViewModel("g1", _localizer.Get("option.java.jvmPreset.g1")),
            new JvmPresetOptionViewModel("zgc", _localizer.Get("option.java.jvmPreset.zgc")),
            new JvmPresetOptionViewModel("aikar", _localizer.Get("option.java.jvmPreset.aikar")),
        ];

        JavaOptions.Add(new JavaOptionViewModel(null, _localizer.Get("instances.java.auto")));
        _selectedJavaOption = JavaOptions[0];

        LoadSavedValues();
    }

    public string InstanceId => _instance.Id;

    public ObservableCollection<JavaOptionViewModel> JavaOptions { get; } = [];

    public IReadOnlyList<JvmPresetOptionViewModel> PresetOptions { get; }

    [ObservableProperty]
    private JavaOptionViewModel? _selectedJavaOption;

    [ObservableProperty]
    private string _javaStatus = string.Empty;

    [ObservableProperty]
    private string _javaDownloadStatus = string.Empty;

    [ObservableProperty]
    private string _jvmPreset = string.Empty;

    [ObservableProperty]
    private string _jvmArguments = string.Empty;

    [ObservableProperty]
    private string _memoryMbText = string.Empty;

    [ObservableProperty]
    private bool _useGlobalMemory;

    [ObservableProperty]
    private string _globalMemoryText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>实例填写了专属内存时才能编辑数值；“使用全局”状态下输入框不参与编辑。</summary>
    public bool IsMemoryEditable => !UseGlobalMemory;

    public bool IsDisposed { get; private set; }

    /// <summary>加载本实例已保存的 Java 选项与内存/JVM 值；不写入任何设置。</summary>
    public async Task InitializeAsync()
    {
        await RefreshJavaOptionsAsync().ConfigureAwait(true);
        await UpdateJavaStatusAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshJavaAsync() => await RefreshJavaOptionsAsync().ConfigureAwait(true);

    [RelayCommand]
    private void BrowseJava()
    {
        if (IsDisposed)
        {
            return;
        }

        var path = _dialogs.PickOpenFile(
            "选择此实例使用的 Java",
            "Java 可执行文件 (java.exe)|java.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        var runtime = _javaLocator.FromPath(path);
        if (runtime is null)
        {
            StatusMessage = "所选文件无法识别为有效的 Java 运行时。";
            return;
        }

        var option = JavaOptions.FirstOrDefault(item =>
            string.Equals(item.Runtime?.ExecutablePath, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            option = new JavaOptionViewModel(runtime, JavaSelectionPresentation.FormatRuntime(runtime));
            JavaOptions.Add(option);
        }

        _suppressJavaSelection = true;
        try
        {
            SelectedJavaOption = option;
        }
        finally
        {
            _suppressJavaSelection = false;
        }

        PersistJavaSelection();
        StatusMessage = $"已为当前实例选择 {runtime.DisplayName}。";
        _ = UpdateJavaStatusAsync();
    }

    /// <summary>保存本实例的 JVM 参数、预设与内存；只写本工作区对应 InstanceId。</summary>
    [RelayCommand]
    private void Save()
    {
        if (IsDisposed)
        {
            return;
        }

        var memoryText = MemoryMbText.Trim();
        if (!UseGlobalMemory &&
            (memoryText.Length == 0 ||
             !int.TryParse(memoryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMb) ||
             memoryMb is < 1024 or > 65536))
        {
            StatusMessage = _localizer.Get("instances.memory.invalid");
            return;
        }

        if (!_settings.TrySetRaw("instance.java.extraArguments", JvmArguments, InstanceId, out var jvmError))
        {
            StatusMessage = jvmError ?? "保存实例 JVM 参数失败。";
            return;
        }

        if (!_settings.TrySetRaw("instance.java.jvmPreset", JvmPreset, InstanceId, out var presetError))
        {
            StatusMessage = presetError ?? "保存实例 JVM 预设失败。";
            return;
        }

        var memorySaved = UseGlobalMemory
            ? _settings.TryClear("instance.java.memoryMb", InstanceId, out var memoryError)
            : _settings.TrySetRaw("instance.java.memoryMb", memoryText, InstanceId, out memoryError);
        if (!memorySaved)
        {
            StatusMessage = memoryError ?? "保存实例内存设置失败。";
            return;
        }

        RefreshGlobalMemoryText();
        StatusMessage = UseGlobalMemory
            ? _localizer.Get("instances.memory.savedGlobal")
            : _localizer.Get("instances.memory.saved");
    }

    /// <summary>下载适合本实例的 Java，并把该运行时固定为本实例的覆盖项。</summary>
    [RelayCommand]
    private async Task DownloadJavaAsync()
    {
        if (IsDisposed || _isDownloadingJava)
        {
            return;
        }

        var versionResult = await _versionResolver.ResolveAsync(_instance.VersionId).ConfigureAwait(true);
        var version = versionResult.IsSuccess ? versionResult.Value : null;
        var requirements = await _javaSelection
            .ResolveRequirementsAsync(_instance, version)
            .ConfigureAwait(true);
        if (requirements.HasConflicts)
        {
            JavaDownloadStatus = _localizer.Get("instances.java.conflict", requirements.Explain());
            StatusMessage = JavaDownloadStatus;
            return;
        }

        if (requirements.IsUnknown)
        {
            JavaDownloadStatus = _localizer.Get("instances.java.unknownRequirement", _instance.GameVersion);
        }

        var requirement = requirements.ToRequirement();
        if (!versionResult.IsSuccess)
        {
            JavaDownloadStatus = _localizer.Get("instances.java.usingGameVersion", _instance.GameVersion);
        }

        _isDownloadingJava = true;
        JavaDownloadStatus = _localizer.Get("instances.java.downloading");
        _downloads.Resume();

        var progress = new Progress<JavaInstallProgress>(update =>
        {
            JavaDownloadStatus = update.TotalFiles > 0
                ? $"{update.Message} ({update.CompletedFiles}/{update.TotalFiles})"
                : update.Message;
        });

        try
        {
            var result = await _javaInstall
                .EnsureAsync(requirement, progress)
                .ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                JavaDownloadStatus = result.Error?.Message ?? _localizer.Get("instances.java.downloadFailed");
                StatusMessage = JavaDownloadStatus;
                return;
            }

            JavaDownloadStatus = _localizer.Get("instances.java.downloadSucceeded");
            await RefreshJavaOptionsAsync().ConfigureAwait(true);

            if (result.Value is not null)
            {
                _settings.TrySetRaw(
                    "instance.java.executablePath",
                    result.Value.ExecutablePath,
                    InstanceId,
                    out _);
                SelectConfiguredJavaOption(result.Value.ExecutablePath);
            }

            await UpdateJavaStatusAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            JavaDownloadStatus = _localizer.Get("instances.java.downloadFailed");
            StatusMessage = exception.Message;
        }
        finally
        {
            _isDownloadingJava = false;
            _downloads.Resume();
        }
    }

    partial void OnUseGlobalMemoryChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMemoryEditable));

        // 取消勾选时用全局值作为起点，避免用户面对一个空输入框。
        if (!value && string.IsNullOrWhiteSpace(MemoryMbText))
        {
            MemoryMbText = _settings
                .Get<int>("java.defaultMemoryMb")
                .ToString(CultureInfo.InvariantCulture);
        }
    }

    partial void OnSelectedJavaOptionChanged(JavaOptionViewModel? value)
    {
        if (_suppressJavaSelection || IsDisposed)
        {
            return;
        }

        PersistJavaSelection();
        _ = UpdateJavaStatusAsync();
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        JavaOptions.Clear();
    }

    private void LoadSavedValues()
    {
        JvmArguments = _settings.Get<string?>("instance.java.extraArguments", InstanceId) ?? string.Empty;
        JvmPreset = _settings.Get<string?>("instance.java.jvmPreset", InstanceId) ?? string.Empty;
        var instanceMemory = _settings.Get<int?>("instance.java.memoryMb", InstanceId);
        UseGlobalMemory = instanceMemory is null;
        MemoryMbText = instanceMemory?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        RefreshGlobalMemoryText();
    }

    private void RefreshGlobalMemoryText() =>
        GlobalMemoryText = _localizer.Get(
            "instance.detail.memoryGlobal",
            _settings.Get<int>("java.defaultMemoryMb"));

    private async Task RefreshJavaOptionsAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var configured = _settings.Get<string?>("instance.java.executablePath", InstanceId);
        _suppressJavaSelection = true;
        try
        {
            var runtimes = await _javaLocator.FindAllAsync().ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            JavaOptions.Clear();
            JavaOptions.Add(new JavaOptionViewModel(null, _localizer.Get("instances.java.auto")));
            foreach (var runtime in runtimes)
            {
                JavaOptions.Add(new JavaOptionViewModel(
                    runtime,
                    JavaSelectionPresentation.FormatRuntime(runtime)));
            }

            SelectConfiguredJavaOption(configured);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            JavaStatus = exception.Message;
        }
        finally
        {
            _suppressJavaSelection = false;
        }
    }

    private void SelectConfiguredJavaOption(string? configuredPath)
    {
        var option = JavaOptions.FirstOrDefault(item =>
            item.Runtime is not null &&
            string.Equals(item.Runtime.ExecutablePath, configuredPath, StringComparison.OrdinalIgnoreCase));

        if (option is null && !string.IsNullOrWhiteSpace(configuredPath))
        {
            var runtime = _javaLocator.FromPath(configuredPath);
            if (runtime is not null)
            {
                option = new JavaOptionViewModel(runtime, JavaSelectionPresentation.FormatRuntime(runtime));
                JavaOptions.Add(option);
            }
        }

        SelectedJavaOption = option ?? JavaOptions.FirstOrDefault();
    }

    private void PersistJavaSelection()
    {
        var path = SelectedJavaOption?.Runtime?.ExecutablePath ?? string.Empty;
        if (!_settings.TrySetRaw("instance.java.executablePath", path, InstanceId, out var error))
        {
            StatusMessage = error ?? string.Empty;
        }
    }

    private async Task UpdateJavaStatusAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            var versionResult = await _versionResolver.ResolveAsync(_instance.VersionId).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            if (!versionResult.IsSuccess)
            {
                JavaStatus = _localizer.Get("instances.java.installFirst");
                return;
            }

            var selection = await _javaSelection
                .ResolveAsync(_instance, versionResult.Value)
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            JavaStatus = JavaSelectionPresentation.Describe(selection, _localizer);
        }
        catch (Exception exception)
        {
            JavaStatus = exception.Message;
        }
    }
}
