// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Configuration.Settings;

public interface ISettingsService
{
    IReadOnlyList<OptionDefinition> GetDefinitions(OptionScope? scope = null);

    OptionDefinition GetDefinition(string key);

    T? Get<T>(string key, string? instanceId = null);

    string GetRawString(string key, string? instanceId = null);

    SettingsValueSource GetSource(string key, string? instanceId = null);

    bool IsVisible(OptionDefinition definition, string? instanceId = null);

    bool TrySetRaw(string key, string? rawValue, string? instanceId, out string? error);

    bool TryClear(string key, string? instanceId, out string? error);

    event EventHandler<SettingsChangedEventArgs>? Changed;
}

public sealed record SettingsChangedEventArgs(string Key, string? InstanceId);
