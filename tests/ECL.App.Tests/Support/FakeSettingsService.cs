// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration;
using Ecl.Configuration.Settings;

namespace Ecl.App.Tests.Support;

/// <summary>
/// 只保存原始字符串的内存设置服务：足够覆盖页面读取频道/开关与写入断言，
/// 不复制真实设置服务的元数据、可见性或持久化逻辑。
/// </summary>
internal sealed class FakeSettingsService : ISettingsService
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>与真实选项定义一致的默认值；未写入的键按默认值读取。</summary>
    public Dictionary<string, string> Defaults { get; } = new(StringComparer.Ordinal)
    {
        ["launcher.checkUpdates"] = "true",
        ["launcher.updateChannel"] = "main",
    };

    public IReadOnlyList<OptionDefinition> GetDefinitions(OptionScope? scope = null) => [];

    public OptionDefinition GetDefinition(string key) => throw new KeyNotFoundException(key);

    public T? Get<T>(string key, string? instanceId = null)
    {
        var raw = GetRawString(key, instanceId);
        if (string.IsNullOrEmpty(raw))
        {
            return default;
        }

        if (typeof(T) == typeof(bool))
        {
            return (T)(object)bool.Parse(raw);
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)raw;
        }

        return default;
    }

    public string GetRawString(string key, string? instanceId = null) =>
        Values.TryGetValue(key, out var value)
            ? value
            : Defaults.TryGetValue(key, out var fallback) ? fallback : string.Empty;

    public SettingsValueSource GetSource(string key, string? instanceId = null) =>
        Values.ContainsKey(key) ? SettingsValueSource.Global : SettingsValueSource.Default;

    public bool IsVisible(OptionDefinition definition, string? instanceId = null) => true;

    public bool TrySetRaw(string key, string? rawValue, string? instanceId, out string? error)
    {
        if (rawValue is null)
        {
            Values.Remove(key);
        }
        else
        {
            Values[key] = rawValue;
        }

        error = null;
        return true;
    }

    public bool TryClear(string key, string? instanceId, out string? error)
    {
        Values.Remove(key);
        error = null;
        return true;
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }
}
