// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace Ecl.Configuration.Settings;

public sealed class SettingsDocument
{
    public int SchemaVersion { get; set; } = 1;

    public Dictionary<string, JsonElement> Values { get; set; } = new(StringComparer.Ordinal);
}

public enum SettingsValueSource
{
    Default,
    Global,
    Instance,
}
