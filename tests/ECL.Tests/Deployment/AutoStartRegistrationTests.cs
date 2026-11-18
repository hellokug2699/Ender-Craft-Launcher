// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Deployment.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecl.Tests.Deployment;

/// <summary>
/// F-12 登录启动注册：命令形状、失败不破坏既有值、不支持平台与外部删除。
/// 这些测试全部使用内存注册表替身，不会写真实 HKCU。
/// </summary>
public sealed class AutoStartRegistrationTests
{
    private static AutoStartRegistration Create(
        FakeRunKeyStore store,
        FakeExecutableLocator? locator = null) =>
        new(store, locator ?? new FakeExecutableLocator(), NullLogger<AutoStartRegistration>.Instance);

    [Fact]
    public void EnablingWritesQuotedExecutableWithAutoStartArgument()
    {
        var store = new FakeRunKeyStore();
        var registration = Create(store);

        var result = registration.SetEnabled(true);

        Assert.True(result.IsSuccess);
        Assert.True(store.Values.TryGetValue(RunKeyNames.ValueName, out var command));
        // 引号必须在路径两侧：Program Files 之类的空格路径不能被拆成两个参数。
        Assert.Equal("\"C:\\ECL\\ECL.Bootstrapper.exe\" --autostart", command);
        Assert.Equal(command, registration.ExpectedCommand);
        Assert.Equal(1, store.WriteCount);
    }

    [Fact]
    public void ReadParsesExistingCommandAndReportsState()
    {
        var store = new FakeRunKeyStore();
        store.Values[RunKeyNames.ValueName] = "\"D:\\Games\\ECL\\ECL.Bootstrapper.exe\" --autostart";
        var registration = Create(store);

        var state = registration.Read();

        Assert.True(state.IsSupported);
        Assert.True(state.IsEnabled);
        Assert.Null(state.ErrorCode);
        Assert.Equal("\"D:\\Games\\ECL\\ECL.Bootstrapper.exe\" --autostart", state.Command);
    }

    [Fact]
    public void MissingValueReadsAsDisabledInsteadOfError()
    {
        var store = new FakeRunKeyStore();
        var registration = Create(store);

        var state = registration.Read();

        Assert.True(state.IsSupported);
        Assert.False(state.IsEnabled);
        Assert.Null(state.Command);
        Assert.Null(state.ErrorCode);
    }

    [Fact]
    public void WriteFailureKeepsPreviousValueAndReportsRegistryFailure()
    {
        var store = new FakeRunKeyStore { WriteFails = true };
        store.Values[RunKeyNames.ValueName] = "\"C:\\Old\\ECL.Bootstrapper.exe\" --autostart";
        var registration = Create(store);

        var result = registration.SetEnabled(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("autostart.registry_failed", result.Error!.Code);
        // “先写后删”会把用户已有启动项弄丢：失败时原值必须保持。
        Assert.Equal("\"C:\\Old\\ECL.Bootstrapper.exe\" --autostart", store.Values[RunKeyNames.ValueName]);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public void DisablingMissingValueIsTreatedAsSuccess()
    {
        var store = new FakeRunKeyStore();
        var registration = Create(store);

        var result = registration.SetEnabled(false);

        Assert.True(result.IsSuccess);
        Assert.False(store.Values.ContainsKey(RunKeyNames.ValueName));
        Assert.Equal(1, store.DeleteCount);
    }

    [Fact]
    public void DeleteFailureReportsRegistryFailure()
    {
        var store = new FakeRunKeyStore { DeleteFails = true };
        store.Values[RunKeyNames.ValueName] = "stale";
        var registration = Create(store);

        var result = registration.SetEnabled(false);

        Assert.False(result.IsSuccess);
        Assert.Equal("autostart.registry_failed", result.Error!.Code);
        Assert.Equal("stale", store.Values[RunKeyNames.ValueName]);
    }

    [Fact]
    public void ExternalDeletionIsObservedAsDisabled()
    {
        var store = new FakeRunKeyStore();
        var registration = Create(store);
        Assert.True(registration.SetEnabled(true).IsSuccess);

        store.RemoveExternally(RunKeyNames.ValueName);

        var state = registration.Read();
        Assert.False(state.IsEnabled);
        Assert.Null(state.Command);
    }

    [Fact]
    public void DotnetHostedProcessIsReportedUnsupportedAndNeverWrites()
    {
        var store = new FakeRunKeyStore();
        var registration = Create(store, new FakeExecutableLocator { IsDotnetHosted = true });

        var state = registration.Read();
        var result = registration.SetEnabled(true);

        Assert.False(state.IsSupported);
        Assert.Equal("autostart.unsupported", state.ErrorCode);
        Assert.Null(registration.ExpectedCommand);
        Assert.False(result.IsSuccess);
        Assert.Equal("autostart.unsupported", result.Error!.Code);
        Assert.Empty(store.Values);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public void UnresolvableExecutableIsAlsoUnsupported()
    {
        var store = new FakeRunKeyStore();
        var registration = Create(store, new FakeExecutableLocator { Executable = null });

        var result = registration.SetEnabled(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("autostart.unsupported", result.Error!.Code);
        Assert.Equal(0, store.WriteCount);
    }

    [Fact]
    public void RegistryReadFailureIsReportedNotTreatedAsDisabled()
    {
        var store = new FakeRunKeyStore { ReadFails = true };
        var registration = Create(store);

        var state = registration.Read();

        Assert.True(state.IsSupported);
        Assert.False(state.IsEnabled);
        Assert.Equal("autostart.read_failed", state.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(state.Error));
    }

    [Fact]
    public void InstallLocatorPrefersBootstrapperInInstallRootAndRejectsDotnetHost()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "ecl-install", "versions", "0.9.1");
        var bootstrapper = Path.Combine(Path.GetTempPath(), "ecl-install", "ECL.Bootstrapper.exe");
        var locator = new InstallExecutableLocator(
            baseDirectory: baseDirectory,
            processPath: @"C:\ECL\versions\0.9.1\ECL.exe",
            fileExists: path => string.Equals(path, bootstrapper, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(bootstrapper, locator.ResolveStartupExecutable());
        Assert.False(locator.IsDotnetHosted);

        var hosted = new InstallExecutableLocator(
            baseDirectory: baseDirectory,
            processPath: @"C:\Program Files\dotnet\dotnet.exe",
            fileExists: _ => false);

        Assert.True(hosted.IsDotnetHosted);
        Assert.Null(hosted.ResolveStartupExecutable());

        var portable = new InstallExecutableLocator(
            baseDirectory: Path.Combine(Path.GetTempPath(), "ecl-portable"),
            processPath: @"C:\Tools\ECL\ECL.exe",
            fileExists: _ => false);

        // 没有安装布局时回退到当前进程可执行文件，而不是返回 null。
        Assert.Equal(@"C:\Tools\ECL\ECL.exe", portable.ResolveStartupExecutable());
    }
}
