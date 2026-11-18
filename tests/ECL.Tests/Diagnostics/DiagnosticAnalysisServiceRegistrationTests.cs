// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Diagnostics;
using Ecl.Minecraft.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecl.Tests.Diagnostics;

/// <summary>
/// F-10 DI 契约：Minecraft 模块必须能解析诊断引擎、输入加载器、历史存储、
/// 会话事实源与编排服务，且重复注册不会产生多个事实源。
/// </summary>
public sealed class DiagnosticAnalysisServiceRegistrationTests
{
    [Fact]
    public void MinecraftModuleRegistersDiagnosticServices()
    {
        using var environment = new TestEnvironment();
        using var provider = environment.CreateServices();

        Assert.NotNull(provider.GetRequiredService<DiagnosticEngine>());
        Assert.NotNull(provider.GetRequiredService<DiagnosticInputLoader>());
        Assert.NotNull(provider.GetRequiredService<DiagnosticHistoryStore>());
        // F-10 S4/S9：会话 manifest 与 Mod 关系事实源各一，不因重复注册产生多个实例。
        Assert.Equal(2, provider.GetServices<IDiagnosticFactSource>().Count());
        Assert.NotNull(provider.GetRequiredService<IDiagnosticWorldFactSource>());
        Assert.NotNull(provider.GetRequiredService<IDiagnosticAnalysisService>());
    }
}
