// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Minecraft.Eula;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Minecraft;

public sealed class EulaServiceTests
{
    [Fact]
    public async Task AcceptsOnceAndPersistsConsent()
    {
        using var environment = new TestEnvironment();

        await using (var provider = environment.CreateServices())
        {
            var eula = provider.GetRequiredService<IEulaService>();
            Assert.False(eula.HasAccepted);
            Assert.Null(eula.GetConsent());

            var accepted = eula.Accept();
            Assert.True(accepted.IsSuccess);
            Assert.True(eula.HasAccepted);
            Assert.NotNull(eula.GetConsent());
        }

        await using var secondProvider = environment.CreateServices();
        var persisted = secondProvider.GetRequiredService<IEulaService>();
        Assert.True(persisted.HasAccepted);
        Assert.NotNull(persisted.GetConsent());
    }
}
