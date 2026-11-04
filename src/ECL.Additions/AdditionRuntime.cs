// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Runtime.Loader;
using Ecl.Core;
using Ecl.Deployment;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecl.Additions;

public enum AdditionLoadState
{
    NotLoaded,
    Loading,
    Loaded,
    Disabled,
    Incompatible,
    Broken,
}

public sealed record AdditionRuntimeSnapshot(AdditionId Id, AdditionLoadState State, string? Error);

/// <summary>
/// 按需加载扩展能力（ADR 0032 §7）。安装不等于加载：只有用户进入对应功能或明确调用时才加载；
/// 扩展通过 <see cref="IAdditionModule"/> 接入，不获得启动器内部对象或 UI 注入能力。
/// 加载失败只影响该扩展，不影响普通启动或其他扩展。
/// </summary>
public sealed class AdditionRuntime
{
    private readonly AdditionStateStore _stateStore;
    private readonly ReleaseVersion _coreApiVersion;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<AdditionId, LoadedAddition> _loaded = [];

    public AdditionRuntime(
        AdditionStateStore stateStore,
        string additionsRoot,
        ReleaseVersion coreApiVersion,
        ILogger<AdditionRuntime>? logger = null)
    {
        _stateStore = stateStore;
        AdditionsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(additionsRoot));
        _coreApiVersion = coreApiVersion;
        _logger = logger ?? NullLogger<AdditionRuntime>.Instance;
    }

    public string AdditionsRoot { get; }

    public IReadOnlyList<AdditionRuntimeSnapshot> Snapshot()
    {
        var load = _stateStore.Load();
        return load.Document.Additions.Select(record =>
        {
            if (_loaded.TryGetValue(record.Id, out var loaded))
            {
                return new AdditionRuntimeSnapshot(record.Id, AdditionLoadState.Loaded, null);
            }

            return new AdditionRuntimeSnapshot(
                record.Id,
                record.Enabled ? AdditionLoadState.NotLoaded : AdditionLoadState.Disabled,
                load.IsCorrupt ? load.Error : null);
        }).ToArray();
    }

    public async Task<Result<IAdditionModule>> ActivateAsync(
        AdditionId id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded.TryGetValue(id, out var existing))
            {
                return Result<IAdditionModule>.Ok(existing.Module);
            }

            var load = _stateStore.Load();
            if (load.IsCorrupt)
            {
                return Result<IAdditionModule>.Fail(
                    "additions.state_corrupt",
                    "扩展状态文件损坏，无法安全加载扩展。",
                    load.Error);
            }

            var record = load.Document.Additions.FirstOrDefault(item => item.Id == id);
            if (record is null)
            {
                return Result<IAdditionModule>.Fail("additions.not_installed", $"扩展 {id.Value} 未安装。");
            }

            if (!record.Enabled)
            {
                return Result<IAdditionModule>.Fail("additions.disabled", $"扩展 {id.Value} 已安装但未启用。");
            }

            if (record.CoreApiMin is not null &&
                record.CoreApiMax is not null &&
                (_coreApiVersion.CompareTo(record.CoreApiMin) < 0 || _coreApiVersion.CompareTo(record.CoreApiMax) > 0))
            {
                return Result<IAdditionModule>.Fail(
                    "additions.incompatible_core_api",
                    $"扩展 {id.Value} 与当前 Core API {_coreApiVersion} 不兼容。");
            }

            var directory = Path.GetFullPath(record.Directory);
            if (!UpdatePathSafety.IsContainedIn(AdditionsRoot, directory) || !Directory.Exists(directory))
            {
                return Result<IAdditionModule>.Fail(
                    "additions.install_missing",
                    $"扩展 {id.Value} 的安装目录不存在或越界。");
            }

            var entryAssembly = Path.GetFullPath(Path.Combine(
                directory,
                record.EntryAssembly.Replace('/', Path.DirectorySeparatorChar)));
            if (!UpdatePathSafety.IsContainedIn(directory, entryAssembly) || !File.Exists(entryAssembly))
            {
                return Result<IAdditionModule>.Fail(
                    "additions.entry_missing",
                    $"扩展 {id.Value} 的入口程序集缺失。");
            }

            AdditionLoadContext? context = null;
            try
            {
                context = new AdditionLoadContext(entryAssembly);
                var assembly = context.LoadFromAssemblyPath(entryAssembly);
                var moduleType = assembly
                    .GetTypes()
                    .FirstOrDefault(type =>
                        typeof(IAdditionModule).IsAssignableFrom(type) &&
                        type is { IsAbstract: false, IsInterface: false } &&
                        type.GetConstructor(Type.EmptyTypes) is not null);
                if (moduleType is null)
                {
                    context.Unload();
                    return Result<IAdditionModule>.Fail(
                        "additions.entry_incompatible",
                        $"扩展 {id.Value} 没有可用的 IAdditionModule 实现。");
                }

                var module = (IAdditionModule)Activator.CreateInstance(moduleType)!;
                var dataDirectory = Path.Combine(AdditionsRoot, "data", id.Value);
                Directory.CreateDirectory(dataDirectory);
                var hostContext = new AdditionHostContext(id, record.Version, _coreApiVersion, dataDirectory);
                await module.InitializeAsync(hostContext, cancellationToken).ConfigureAwait(false);
                _loaded[id] = new LoadedAddition(record.Version, module, context);
                _logger.LogInformation("扩展 {Addition} {Version} 已按需加载。", id.Value, record.Version);
                return Result<IAdditionModule>.Ok(module);
            }
            catch (OperationCanceledException)
            {
                context?.Unload();
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                context?.Unload();
                _logger.LogWarning(exception, "加载扩展 {Addition} 失败。", id.Value);
                return Result<IAdditionModule>.Fail(
                    "additions.load_failed",
                    $"扩展 {id.Value} 加载失败；不影响普通启动。",
                    exception.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> DeactivateAsync(
        AdditionId id,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded.Remove(id, out var loaded))
            {
                return Result.Ok();
            }

            try
            {
                await loaded.Module.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogWarning(exception, "扩展 {Addition} 停止时失败。", id.Value);
            }
            finally
            {
                loaded.Context.Unload();
            }

            return Result.Ok();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ShutdownAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var id in _loaded.Keys.ToArray())
        {
            await DeactivateAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record LoadedAddition(
        ReleaseVersion Version,
        IAdditionModule Module,
        AdditionLoadContext Context);

    private sealed class AdditionLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver? _resolver =
            File.Exists(Path.ChangeExtension(assemblyPath, ".deps.json"))
                ? new AssemblyDependencyResolver(assemblyPath)
                : null;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver?.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
