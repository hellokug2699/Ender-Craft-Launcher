// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Configuration;

public sealed class OptionRegistry
{
    private readonly Dictionary<string, OptionDefinition> _definitions;

    public IReadOnlyList<OptionDefinition> All { get; }

    public OptionRegistry(IEnumerable<IOptionProvider> providers)
    {
        var definitions = providers
            .SelectMany(provider => provider.GetOptions())
            .OrderBy(definition => definition.Scope)
            .ThenBy(definition => definition.Order)
            .ThenBy(definition => definition.Key, StringComparer.Ordinal)
            .ToArray();

        var map = new Dictionary<string, OptionDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!map.TryAdd(definition.Key, definition))
            {
                throw new InvalidOperationException($"配置项 Key 重复：{definition.Key}");
            }
        }

        _definitions = map;
        All = definitions;
    }

    public OptionDefinition? Find(string key) => _definitions.GetValueOrDefault(key);

    public OptionDefinition Get(string key) => _definitions.TryGetValue(key, out var definition)
        ? definition
        : throw new KeyNotFoundException($"未知的配置项：{key}");

    public IReadOnlyList<OptionDefinition> ForScope(OptionScope scope) =>
        All.Where(definition => definition.Scope == scope).ToArray();

    public IReadOnlyList<OptionDefinition> AllForScope(OptionScope? scope) =>
        scope is null ? All : ForScope(scope.Value);
}
