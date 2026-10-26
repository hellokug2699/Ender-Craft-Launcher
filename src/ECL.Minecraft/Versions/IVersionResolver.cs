// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core;

namespace Ecl.Minecraft.Versions;

public interface IVersionResolver
{
    Task<Result<MinecraftVersion>> ResolveAsync(string versionId, CancellationToken cancellationToken = default);
}
