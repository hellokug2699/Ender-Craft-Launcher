// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Ecl.Core.Storage;

namespace Ecl.Core;

/// <summary>
/// 应用的数据目录布局。所有模块只通过这里获取路径，避免各自拼接。
/// <para>
/// <see cref="Root"/> 是启动器的数据目录（配置、账户、日志、缓存），
/// <see cref="GameRoot"/> 是实例与游戏文件的根目录，也就是用户在设置里看到的
/// “实例下载位置（.minecraft 目录）”。两者分离后，游戏目录可以放到容量更大的磁盘，
/// 而账户与配置仍留在数据目录中。
/// </para>
/// </summary>
public interface IAppPaths
{
    string Root { get; }

    /// <summary>实例与 .minecraft 文件所在的根目录（当前主工作区）。</summary>
    string GameRoot { get; }

    /// <summary>
    /// 用户是否已经确认过 Minecraft 工作区。未确认时不得创建工作区结构、
    /// 开始下载或创建实例；路径仍可能回退到数据目录用于只读兼容。
    /// </summary>
    bool WorkspaceConfigured { get; }

    string ConfigDirectory { get; }
    string AccountsDirectory { get; }
    string InstancesDirectory { get; }
    string VersionsDirectory { get; }
    string LibrariesDirectory { get; }
    string AssetsDirectory { get; }

    /// <summary>ECL 管理的 Java 运行时目录（ADR 0018 的 <c>Runtimes</c>）。</summary>
    string RuntimesDirectory { get; }

    /// <summary>旧版 Java 运行时目录，仅用于继续识别既有安装，不再写入。</summary>
    string LegacyJavaDirectory { get; }
    string LogsDirectory { get; }
    string CacheDirectory { get; }
    string DownloadsDirectory { get; }
    string GlobalSettingsFile { get; }
    string AccountsFile { get; }
    string AuthServersFile { get; }

    /// <summary>Authlib-Injector 组件的缓存目录。</summary>
    string InjectorDirectory { get; }

    string GetInstanceDirectory(string instanceId);
    string GetInstanceMetadataFile(string instanceId);
    string GetInstanceSettingsFile(string instanceId);
    string GetInstanceGameDirectory(string instanceId);
    string GetInstanceNativesDirectory(string instanceId);
    string GetInstanceLogsDirectory(string instanceId);

    /// <summary>
    /// 切换实例下载位置。传空表示恢复默认（数据目录本身）。
    /// 运行期切换目录后，新的路径立即对所有通过 <see cref="IAppPaths"/> 获取路径的模块生效。
    /// </summary>
    void SetGameRoot(string? path);

    /// <summary>
    /// 登记某个实例的记录根目录（多受管理根 F-02/F-03）。记录目录解析为
    /// <c>&lt;root&gt;/instances/&lt;id&gt;</c>；切换主工作区不影响已登记的其他受管理根。
    /// </summary>
    void RegisterInstanceRoot(string instanceId, string managedRootPath);

    void UnregisterInstanceRoot(string instanceId);

    bool TryGetInstanceRoot(string instanceId, out string? managedRootPath);

    /// <summary>当前进程内已登记的实例→受管理根映射（诊断/测试用只读快照）。</summary>
    IReadOnlyDictionary<string, string> InstanceRootMappings { get; }

    /// <summary>
    /// 按工作区登记重建实例→受管理根映射：主工作区实例保持默认解析，
    /// 其他受管理根按其 <c>instances/&lt;id&gt;</c> 目录登记。只读磁盘，不修改文件。
    /// </summary>
    void SyncInstanceRootMappings(IReadOnlyList<WorkspaceRoot> roots, string? primaryId);

    void SetWorkspaceConfigured(bool configured);

    void EnsureCreated();
}

public sealed partial class AppPaths : IAppPaths
{
    private readonly ConcurrentDictionary<string, string> _instanceRoots = new(StringComparer.Ordinal);

    public const string ProductDirectoryName = "EnderCraftLauncher";
    public const string PortableMarkerFileName = "portable.flag";
    public const string DataDirectoryEnvironmentVariable = "ECL_DATA_DIR";
    public const string GameDirectoryEnvironmentVariable = "ECL_GAME_DIR";

    public string Root { get; }

    public string GameRoot { get; private set; }

    public bool WorkspaceConfigured { get; private set; }

    public string ConfigDirectory => Path.Combine(Root, "config");
    public string AccountsDirectory => Path.Combine(Root, "accounts");
    public string InstancesDirectory => Path.Combine(GameRoot, "instances");
    public string VersionsDirectory => Path.Combine(GameRoot, "versions");
    public string LibrariesDirectory => Path.Combine(GameRoot, "libraries");
    public string AssetsDirectory => Path.Combine(GameRoot, "assets");
    public string RuntimesDirectory => Path.Combine(Root, "Runtimes");
    public string LegacyJavaDirectory => Path.Combine(Root, "java");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string CacheDirectory => Path.Combine(Root, "cache");
    public string DownloadsDirectory => Path.Combine(Root, "downloads");
    public string GlobalSettingsFile => Path.Combine(ConfigDirectory, "global.json");
    public string AccountsFile => Path.Combine(AccountsDirectory, "accounts.json");
    public string AuthServersFile => Path.Combine(AccountsDirectory, "authservers.json");
    public string InjectorDirectory => Path.Combine(Root, "injector");

    public AppPaths(string root, string? gameRoot = null)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("数据目录不能为空。", nameof(root));
        }

        Root = Path.GetFullPath(root);
        GameRoot = NormalizeGameRoot(gameRoot) ?? Root;
        WorkspaceConfigured = NormalizeGameRoot(gameRoot) is not null;
    }

    /// <summary>
    /// 优先级：显式参数 &gt; ECL_DATA_DIR &gt; 便携模式 &gt; 用户本地数据目录。
    /// 游戏目录额外支持 ECL_GAME_DIR，便于测试与高级用户隔离存档。
    /// </summary>
    public static AppPaths Create(string? root = null)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            return new AppPaths(root, Environment.GetEnvironmentVariable(GameDirectoryEnvironmentVariable));
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new AppPaths(fromEnvironment, Environment.GetEnvironmentVariable(GameDirectoryEnvironmentVariable));
        }

        var portableMarker = Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName);
        if (File.Exists(portableMarker))
        {
            return new AppPaths(
                Path.Combine(AppContext.BaseDirectory, "data"),
                Environment.GetEnvironmentVariable(GameDirectoryEnvironmentVariable));
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.Combine(AppContext.BaseDirectory, "data");
        }

        return new AppPaths(
            Path.Combine(localData, ProductDirectoryName),
            Environment.GetEnvironmentVariable(GameDirectoryEnvironmentVariable));
    }

    public void SetGameRoot(string? path)
    {
        var normalized = NormalizeGameRoot(path);
        GameRoot = normalized ?? Root;
        WorkspaceConfigured = normalized is not null;
    }

    public void SetWorkspaceConfigured(bool configured) => WorkspaceConfigured = configured;

    public IReadOnlyDictionary<string, string> InstanceRootMappings => _instanceRoots;

    public void RegisterInstanceRoot(string instanceId, string managedRootPath)
    {
        if (!IsValidInstanceId(instanceId) || string.IsNullOrWhiteSpace(managedRootPath))
        {
            return;
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRootPath));
        _instanceRoots[instanceId] = normalized;
    }

    public void UnregisterInstanceRoot(string instanceId) => _instanceRoots.TryRemove(instanceId, out _);

    public bool TryGetInstanceRoot(string instanceId, out string? managedRootPath) =>
        _instanceRoots.TryGetValue(instanceId, out managedRootPath);

    public void SyncInstanceRootMappings(IReadOnlyList<WorkspaceRoot> roots, string? primaryId)
    {
        _instanceRoots.Clear();
        foreach (var root in roots)
        {
            if (root.Kind != WorkspaceKind.Managed ||
                !root.Enabled ||
                string.Equals(root.Id, primaryId, StringComparison.Ordinal))
            {
                continue;
            }

            var instancesDirectory = Path.Combine(root.Path, "instances");
            if (!Directory.Exists(instancesDirectory))
            {
                continue;
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(instancesDirectory))
                {
                    var instanceId = Path.GetFileName(directory);
                    if (IsValidInstanceId(instanceId) &&
                        File.Exists(Path.Combine(directory, "instance.json")))
                    {
                        RegisterInstanceRoot(instanceId, root.Path);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 不可读的受管理根不阻塞启动；其实例将在下次成功读取时登记。
            }
        }
    }

    public string GetInstanceDirectory(string instanceId)
    {
        EnsureValidInstanceId(instanceId);
        return _instanceRoots.TryGetValue(instanceId, out var rootPath)
            ? Path.Combine(rootPath, "instances", instanceId)
            : Path.Combine(InstancesDirectory, instanceId);
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
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(InjectorDirectory);
        Directory.CreateDirectory(RuntimesDirectory);

        // 未确认工作区前不得创建工作区结构，避免把实例默认写进数据目录。
        // 已登记但当前不可用的工作区也不重建，交由工作区校验提示重新连接。
        if (!WorkspaceConfigured || !Directory.Exists(GameRoot))
        {
            return;
        }

        Directory.CreateDirectory(InstancesDirectory);
        Directory.CreateDirectory(VersionsDirectory);
        Directory.CreateDirectory(LibrariesDirectory);
        Directory.CreateDirectory(AssetsDirectory);
    }

    public static bool IsValidInstanceId(string? instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) && InstanceIdRegex().IsMatch(instanceId);

    private static string? NormalizeGameRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }

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
