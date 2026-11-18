// SPDX-License-Identifier: GPL-3.0-or-later

namespace Ecl.Tests.Cli;

/// <summary>
/// 所有重定向 <see cref="Console"/> 的 CLI 测试共用同一把锁：
/// xUnit 会并行运行不同测试类，各自私有的锁无法阻止交叉重定向导致的输出丢失。
/// </summary>
internal static class ConsoleTestGate
{
    public static SemaphoreSlim Gate { get; } = new(1, 1);
}
