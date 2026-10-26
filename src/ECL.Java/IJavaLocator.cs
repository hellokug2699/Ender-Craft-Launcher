// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Java;

public interface IJavaLocator
{
    Task<IReadOnlyList<JavaRuntime>> FindAllAsync(CancellationToken cancellationToken = default);

    JavaRuntime? FromPath(string path);
}
