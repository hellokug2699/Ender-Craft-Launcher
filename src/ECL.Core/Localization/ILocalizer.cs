// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Ecl.Core.Localization;

public interface ILocalizer
{
    CultureInfo Culture { get; }

    IReadOnlyList<CultureInfo> AvailableCultures { get; }

    string Get(string key);

    string Get(string key, params object?[] arguments);
}
