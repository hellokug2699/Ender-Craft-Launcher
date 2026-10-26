// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Ecl.Auth;
using Ecl.Configuration;
using Ecl.Core;
using Ecl.Java;
using Ecl.Minecraft;
using Microsoft.Extensions.DependencyInjection;

namespace Ecl.Tests;

public sealed class TestEnvironment : IDisposable
{
    public string Root { get; }

    public AppPaths Paths { get; }

    public TestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "ecl-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();
    }

    public ServiceProvider CreateServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEclCore(Paths, CultureInfo.GetCultureInfo("zh-CN"));
        services.AddEclConfiguration();
        services.AddEclAuth();
        services.AddEclJava();
        services.AddEclMinecraft();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不应影响测试结果。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}
