// SPDX-License-Identifier: GPL-3.0-or-later

using Ecl.Configuration;
using Ecl.Configuration.Settings;
using Ecl.Deployment.Startup;

namespace Ecl.Tests.Deployment;

/// <summary>
/// 内存登录启动项注册表替身：可以模拟写入/读取/删除失败，以及外部程序删除值。
/// 测试不接触真实 HKCU，也不会让开发机上的 ECL 启动项被改动。
/// </summary>
internal sealed class FakeRunKeyStore : IRunKeyStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public bool IsSupported { get; set; } = true;

    public bool ReadFails { get; set; }

    public bool WriteFails { get; set; }

    public bool DeleteFails { get; set; }

    public int WriteCount { get; private set; }

    public int DeleteCount { get; private set; }

    public bool TryRead(string valueName, out string? command, out string? error)
    {
        command = null;
        error = null;
        if (!IsSupported)
        {
            error = "当前平台不支持 Windows 登录启动项。";
            return false;
        }

        if (ReadFails)
        {
            error = "读取注册表失败。";
            return false;
        }

        command = Values.TryGetValue(valueName, out var value) ? value : null;
        return true;
    }

    public bool TryWrite(string valueName, string command, out string? error)
    {
        error = null;
        if (!IsSupported)
        {
            error = "当前平台不支持 Windows 登录启动项。";
            return false;
        }

        if (WriteFails)
        {
            error = "写入注册表失败。";
            return false;
        }

        WriteCount++;
        Values[valueName] = command;
        return true;
    }

    public bool TryDelete(string valueName, out string? error)
    {
        error = null;
        if (!IsSupported)
        {
            error = "当前平台不支持 Windows 登录启动项。";
            return false;
        }

        if (DeleteFails)
        {
            error = "删除注册表值失败。";
            return false;
        }

        DeleteCount++;
        Values.Remove(valueName);
        return true;
    }

    /// <summary>模拟用户或其他程序删除启动项。</summary>
    public void RemoveExternally(string valueName) => Values.Remove(valueName);
}

/// <summary>可解析/不可解析的启动入口替身。</summary>
internal sealed class FakeExecutableLocator : IExecutableLocator
{
    public string? Executable { get; set; } = @"C:\ECL\ECL.Bootstrapper.exe";

    public bool IsDotnetHosted { get; set; }

    public string? ResolveStartupExecutable() => IsDotnetHosted ? null : Executable;
}

/// <summary>只覆盖登录启动对齐所需读取的配置替身；写入会被记录以便断言“不修改用户设置”。</summary>
internal sealed class DeploymentTestSettings : ISettingsService
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal)
    {
        [LauncherKeys.StartWithWindows] = "false",
    };

    public List<(string Key, string? Value)> Writes { get; } = [];

    public IReadOnlyList<OptionDefinition> GetDefinitions(OptionScope? scope = null) => [];

    public OptionDefinition GetDefinition(string key) => throw new KeyNotFoundException(key);

    public T? Get<T>(string key, string? instanceId = null)
    {
        var raw = GetRawString(key, instanceId);
        if (string.IsNullOrEmpty(raw))
        {
            return default;
        }

        if (typeof(T) == typeof(bool))
        {
            return (T)(object)bool.Parse(raw);
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)raw;
        }

        return default;
    }

    public string GetRawString(string key, string? instanceId = null) =>
        Values.TryGetValue(key, out var value) ? value : string.Empty;

    public SettingsValueSource GetSource(string key, string? instanceId = null) =>
        Values.ContainsKey(key) ? SettingsValueSource.Global : SettingsValueSource.Default;

    public bool IsVisible(OptionDefinition definition, string? instanceId = null) => true;

    public bool TrySetRaw(string key, string? rawValue, string? instanceId, out string? error)
    {
        error = null;
        Writes.Add((key, rawValue));
        if (rawValue is null)
        {
            Values.Remove(key);
        }
        else
        {
            Values[key] = rawValue;
        }

        return true;
    }

    public bool TryClear(string key, string? instanceId, out string? error)
    {
        error = null;
        Writes.Add((key, null));
        Values.Remove(key);
        return true;
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }
}
