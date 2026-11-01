// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Core.Diagnostics;
using Xunit;

namespace Ecl.Tests.Core;

public sealed class MemoryPressureTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private static MemoryPressureSample Sample(double availableGb, double commitHeadroomGb) =>
        new((long)(availableGb * Gb), 16 * Gb, (long)(commitHeadroomGb * Gb), 16 * Gb);

    [Fact]
    public void HealthySampleStaysNormal()
    {
        var machine = new MemoryPressureStateMachine();

        var level = machine.Observe(Start, Sample(10, 10));

        Assert.Equal(MemoryPressureLevel.Normal, level);
    }

    [Fact]
    public void EscalatesImmediatelyToCritical()
    {
        var machine = new MemoryPressureStateMachine();

        Assert.Equal(MemoryPressureLevel.Elevated, machine.Observe(Start, Sample(2, 8)));
        Assert.Equal(MemoryPressureLevel.Critical, machine.Observe(Start.AddSeconds(2), Sample(1, 8)));
    }

    [Fact]
    public void LeavingCriticalEntersRecoveryThenNormal()
    {
        var machine = new MemoryPressureStateMachine(recoveryHold: TimeSpan.FromSeconds(10));
        machine.Observe(Start, Sample(1, 8));

        Assert.Equal(MemoryPressureLevel.Recovery, machine.Observe(Start.AddSeconds(2), Sample(10, 10)));
        Assert.Equal(MemoryPressureLevel.Recovery, machine.Observe(Start.AddSeconds(8), Sample(10, 10)));
        Assert.Equal(MemoryPressureLevel.Normal, machine.Observe(Start.AddSeconds(13), Sample(10, 10)));
    }

    [Fact]
    public void RecoveryReturnsToCriticalWhenPressureComesBack()
    {
        var machine = new MemoryPressureStateMachine();
        machine.Observe(Start, Sample(1, 8));
        machine.Observe(Start.AddSeconds(2), Sample(10, 10));

        Assert.Equal(MemoryPressureLevel.Critical, machine.Observe(Start.AddSeconds(4), Sample(1, 8)));
    }

    [Fact]
    public void ElevatedHoldsBeforeReturningToNormal()
    {
        var machine = new MemoryPressureStateMachine(normalHold: TimeSpan.FromSeconds(10));
        machine.Observe(Start, Sample(2, 8));

        Assert.Equal(MemoryPressureLevel.Elevated, machine.Observe(Start.AddSeconds(2), Sample(10, 10)));
        Assert.Equal(MemoryPressureLevel.Elevated, machine.Observe(Start.AddSeconds(6), Sample(10, 10)));
        Assert.Equal(MemoryPressureLevel.Normal, machine.Observe(Start.AddSeconds(20), Sample(10, 10)));
    }

    [Fact]
    public void CommitPressureAloneCanEscalate()
    {
        var machine = new MemoryPressureStateMachine();

        Assert.Equal(MemoryPressureLevel.Elevated, machine.Observe(Start, Sample(10, 1.2)));
        Assert.Equal(MemoryPressureLevel.Critical, machine.Observe(Start.AddSeconds(2), Sample(10, 0.4)));
    }
}
