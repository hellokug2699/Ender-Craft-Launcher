// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Ecl.Core;
using Ecl.Core.Json;
using Microsoft.Extensions.Logging;

namespace Ecl.Configuration.Settings;

public sealed class SettingsService : ISettingsService
{
    private readonly IAppPaths _paths;
    private readonly OptionRegistry _registry;
    private readonly SettingsFileStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, SettingsDocument> _documents = new(StringComparer.Ordinal);

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public SettingsService(
        IAppPaths paths,
        OptionRegistry registry,
        SettingsFileStore store,
        ILogger<SettingsService> logger)
    {
        _paths = paths;
        _registry = registry;
        _store = store;
        _logger = logger;
    }

    public IReadOnlyList<OptionDefinition> GetDefinitions(OptionScope? scope = null) => _registry.AllForScope(scope);

    public OptionDefinition GetDefinition(string key) => _registry.Get(key);

    public T? Get<T>(string key, string? instanceId = null)
    {
        var definition = _registry.Get(key);
        lock (_gate)
        {
            var element = ResolveElement(definition, instanceId);
            if (element is null)
            {
                return DefaultAs<T>(definition);
            }

            try
            {
                return element.Value.Deserialize<T>(EclJson.Default);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "配置项 {Key} 的值无法转换为 {Type}，将回退到默认值。", key, typeof(T).Name);
                return DefaultAs<T>(definition);
            }
        }
    }

    public string GetRawString(string key, string? instanceId = null)
    {
        var definition = _registry.Get(key);
        lock (_gate)
        {
            var element = ResolveElement(definition, instanceId);
            if (element is null)
            {
                return definition.Format(definition.DefaultValue);
            }

            return element.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                JsonValueKind.String => element.Value.GetString() ?? string.Empty,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.Value.GetRawText(),
            };
        }
    }

    public SettingsValueSource GetSource(string key, string? instanceId = null)
    {
        var definition = _registry.Get(key);
        lock (_gate)
        {
            if (definition.Scope == OptionScope.Instance)
            {
                if (instanceId is null)
                {
                    return SettingsValueSource.Default;
                }

                return GetOrLoad(instanceId).Values.ContainsKey(key)
                    ? SettingsValueSource.Instance
                    : SettingsValueSource.Default;
            }

            return GetOrLoad(null).Values.ContainsKey(key)
                ? SettingsValueSource.Global
                : SettingsValueSource.Default;
        }
    }

    public bool IsVisible(OptionDefinition definition, string? instanceId = null)
    {
        if (string.IsNullOrEmpty(definition.VisibleWhenKey))
        {
            return true;
        }

        var dependencyValue = GetRawString(definition.VisibleWhenKey, instanceId);
        return string.Equals(dependencyValue, definition.VisibleWhenValue, StringComparison.OrdinalIgnoreCase);
    }

    public bool TrySetRaw(string key, string? rawValue, string? instanceId, out string? error)
    {
        error = null;
        var definition = _registry.Get(key);

        if (definition.Scope == OptionScope.Global && instanceId is not null)
        {
            error = "全局配置项不能写入实例作用域。";
            return false;
        }

        if (definition.Scope == OptionScope.Instance && instanceId is null)
        {
            error = "实例配置项需要指定实例。";
            return false;
        }

        if (!definition.TryConvert(rawValue, out var converted, out error))
        {
            return false;
        }

        lock (_gate)
        {
            var document = GetOrLoad(instanceId);
            if (converted is null)
            {
                document.Values.Remove(key);
            }
            else
            {
                document.Values[key] = JsonSerializer.SerializeToElement(converted, definition.ValueType, EclJson.Default);
            }

            Save(instanceId, document);
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(key, instanceId));
        return true;
    }

    public bool TryClear(string key, string? instanceId, out string? error)
    {
        error = null;
        var definition = _registry.Get(key);

        if (definition.Scope == OptionScope.Global && instanceId is not null)
        {
            error = "全局配置项不属于实例作用域。";
            return false;
        }

        if (definition.Scope == OptionScope.Instance && instanceId is null)
        {
            error = "实例配置项需要指定实例。";
            return false;
        }

        lock (_gate)
        {
            var document = GetOrLoad(instanceId);
            if (document.Values.Remove(key))
            {
                Save(instanceId, document);
            }
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(key, instanceId));
        return true;
    }

    private JsonElement? ResolveElement(OptionDefinition definition, string? instanceId)
    {
        if (definition.Scope == OptionScope.Instance)
        {
            if (instanceId is null)
            {
                return null;
            }

            return GetOrLoad(instanceId).Values.TryGetValue(definition.Key, out var instanceValue)
                ? instanceValue
                : null;
        }

        return GetOrLoad(null).Values.TryGetValue(definition.Key, out var globalValue)
            ? globalValue
            : null;
    }

    private SettingsDocument GetOrLoad(string? instanceId)
    {
        var documentKey = instanceId ?? string.Empty;
        if (_documents.TryGetValue(documentKey, out var cached))
        {
            return cached;
        }

        var path = instanceId is null
            ? _paths.GlobalSettingsFile
            : _paths.GetInstanceSettingsFile(instanceId);

        var document = _store.Load(path);
        _documents[documentKey] = document;
        return document;
    }

    private void Save(string? instanceId, SettingsDocument document)
    {
        var path = instanceId is null
            ? _paths.GlobalSettingsFile
            : _paths.GetInstanceSettingsFile(instanceId);

        try
        {
            _store.Save(path, document);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "保存配置文件失败：{Path}", path);
            throw;
        }
    }

    private static T? DefaultAs<T>(OptionDefinition definition)
    {
        if (definition.DefaultValue is null)
        {
            return default;
        }

        var valueType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return valueType.IsInstanceOfType(definition.DefaultValue)
            ? (T)definition.DefaultValue
            : default;
    }
}
