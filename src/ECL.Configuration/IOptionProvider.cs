// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Configuration;

public interface IOptionProvider
{
    IEnumerable<OptionDefinition> GetOptions();
}
