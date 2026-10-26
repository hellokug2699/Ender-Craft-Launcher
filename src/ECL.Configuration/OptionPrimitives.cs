// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Configuration;

public enum OptionScope
{
    Global = 0,
    Instance = 1,
}

public enum OptionKind
{
    Boolean,
    Integer,
    Decimal,
    Text,
    Select,
    Path,
    Color,
    KeyBinding,
}

public sealed record OptionChoice(string Value, string DisplayNameKey);
