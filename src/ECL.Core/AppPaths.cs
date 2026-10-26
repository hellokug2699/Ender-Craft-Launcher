// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;

namespace Ecl.Core;

/// <summary>
/// 应用的数据目录布局。所有模块只通过这里获取路径，避免各自拼接。
/// </summary>
public interface IAppPaths
{
    string Root { get; }
    string ConfigDirectory { get; }
    string AccountsDirectory { get; }
    string InstancesDirectory { get; }
    string VersionsDirectory { get; }
    string LibrariesDirectory { get; }
    string AssetsDirectory { get; }
    string JavaDirectory { get; }
    string LogsDirectory { get; }
    string CacheDirectory { get; }
    string GlobalSettingsFile { get; }
    string AccountsFile { get; }

    string GetInstanceDirectory(string instanceId);
    string GetInstanceMetadataFile(string instanceId);
    string GetInstanceSettingsFile(string instanceId);
    string GetInstanceGameDirectory(string instanceId);
    string GetInstanceNativesDirectory(string instanceId);
    string GetInstanceLogsDirectory(string instanceId);

    void EnsureCreated();
}

public sealed partial class AppPaths : IAppPaths
{
    public const string ProductDirectoryName = "EnderCraftLauncher";
    public const string PortableMarkerFileName = "portable.flag";
    public const string DataDirectoryEnvironmentVariable = "ECL_DATA_DIR";

    public string Root { get; }
    public string ConfigDirectory => Path.Combine(Root, "config");
    public string AccountsDirectory => Path.Combine(Root, "accounts");
    public string InstancesDirectory => Path.Combine(Root, "instances");
    public string VersionsDirectory => Path.Combine(Root, "versions");
    public string LibrariesDirectory => Path.Combine(Root, "libraries");
    public string AssetsDirectory => Path.Combine(Root, "assets");
    public string JavaDirectory => Path.Combine(Root, "java");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string GlobalSettingsFile => Path.Combine(ConfigDirectory, "global.json");
    public string AccountsFile => Path.Combine(AccountsDirectory, "accounts.json");

    public AppPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("数据目录不能为空。", nameof(root));
        }

        Root = Path.GetFullPath(root);
    }

    /// <summary>
    /// 优先级：显式参数 &gt; ECL_DATA_DIR &gt; 便携模式 &gt; 用户本地数据目录。
    /// </summary>
    public static AppPaths Create(string? root = null)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            return new AppPaths(root);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new AppPaths(fromEnvironment);
        }

        var portableMarker = Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName);
        if (File.Exists(portableMarker))
        {
            return new AppPaths(Path.Combine(AppContext.BaseDirectory, "data"));
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.Combine(AppContext.BaseDirectory, "data");
        }

        return new AppPaths(Path.Combine(localData, ProductDirectoryName));
    }

    public string GetInstanceDirectory(string instanceId)
    {
        EnsureValidInstanceId(instanceId);
        return Path.Combine(InstancesDirectory, instanceId);
    }

    public string GetInstanceMetadataFile(string instanceId) => Path.Combine(GetInstanceDirectory(instanceId), "instance.json");

    public string GetInstanceSettingsFile(string instanceId) => Path.Combine(GetInstanceDirectory(instanceId), "settings.json");

    public string GetInstanceGameDirectory(string instanceId) => Path.Combine(GetInstanceDirectory(instanceId), "minecraft");

    public string GetInstanceNativesDirectory(string instanceId) => Path.Combine(GetInstanceGameDirectory(instanceId), "natives");

    public string GetInstanceLogsDirectory(string instanceId) => Path.Combine(GetInstanceDirectory(instanceId), "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(AccountsDirectory);
        Directory.CreateDirectory(InstancesDirectory);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(LibrariesDirectory);
        Directory.CreateDirectory(AssetsDirectory);
        Directory.CreateDirectory(JavaDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    public static bool IsValidInstanceId(string? instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) && InstanceIdRegex().IsMatch(instanceId);

    private static void EnsureValidInstanceId(string instanceId)
    {
        if (!IsValidInstanceId(instanceId))
        {
            throw new ArgumentException($"非法的实例 ID：{instanceId}", nameof(instanceId));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceIdRegex();
}
