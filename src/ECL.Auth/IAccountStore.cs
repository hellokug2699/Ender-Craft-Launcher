// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Auth;

public interface IAccountStore
{
    AccountsDocument Load();

    void Save(AccountsDocument document);
}
