# 启动流水线

启动不是一次性的“拼参数然后 Process.Start”，而是一条可观测、可扩展、可降级的分阶段流水线。这样做的原因：

- 安装、账户、模组、崩溃分析都会影响启动，但不应互相耦合。
- 用户需要知道“卡在哪一步”，而不是只看到一句“启动失败”。
- 单元测试可以只验证某个步骤，不需要真的拉起 Java。

## 1. 阶段

| Order | StepId | 实现 | 职责 |
| --- | --- | --- | --- |
| 100 | `validate` | `ValidateInstanceStep` | 实例目录、账户名、基础前提 |
| 150 | `version` | `ResolveVersionStep` | 读取本地版本 JSON 并合并 `inheritsFrom` |
| 200 | `java` | `ResolveJavaStep` | 实例/全局路径、自动选择、兼容性/32-64 位/内存警告、内存计算 |
| 350 | `files` | `ValidateGameFilesStep` | 检查 client、库、资源索引、日志配置与资源对象的存在性、大小与 SHA-1 |
| 360 | `natives` | `ExtractNativesStep` | 清理并解压实例隔离的 natives |
| 370 | `eula` | `EnsureEulaStep` | 检查/写入 `eula.txt`，未同意则阻止真实启动 |
| 400 | `command` | `BuildCommandStep` | 构建 JVM/游戏参数与可读命令 |
| 500 | `start` | `StartGameStep` | Dry Run 跳过；真实启动时拉起进程 |

步骤按 `Order` 排序，由 `LaunchService` 注入 `IEnumerable<ILaunchStep>`。新增步骤只需实现接口并注册。

`files` 步骤的校验范围：

- client、libraries、asset index、logging 文件：检查存在性、大小与 SHA-1；文件被截断或损坏时在真实启动前失败。
- client jar 按继承链定位：优先使用实例版本目录中的 jar（ECL 安装流程写入），缺失时使用继承链中声明 `downloads.client` 的原版版本目录中的 jar。其它启动器安装的 Fabric/Forge 等加载器版本没有自己的 jar，这一层回退是必需的。
- 资源对象（`assets/objects/<前两位>/<hash>`）：检查存在性与大小，避免每次启动对数百 MB 资源全量哈希。
- Dry Run 下缺失/损坏只返回 `Warning`，仍生成预期命令。
- 导入的实例（`GameDirectory` 指向原安装）由外部启动器维护，文件与清单不一致时只返回 `Warning` 并给出若干示例路径，不阻止启动。

安装失败时由 `InstallTransaction` 清理本次新建的文件、恢复被覆盖的版本 JSON，并在 `logs/install/` 写入诊断 JSON。

## 2. 上下文

`LaunchContext` 在步骤间传递：

- `Instance`：实例元数据。
- `Account`：默认账户。
- `Paths`：数据目录。
- `Settings`：配置服务；通过 `GetInstanceSetting<T>` 自动带实例 ID。
- `DryRun`：是否只预览。
- `Java` / `JavaExecutable` / `MemoryMb`：Java 解析结果。
- `Version`：版本 JSON。
- `Command`：可执行文件、参数列表、可读命令。
- `ProcessId`：真实启动后的 PID。

步骤只读写上下文，不直接依赖 UI。

## 3. Dry Run 与真实启动

### Dry Run

- 用于 UI 的“预览命令”和 CLI 的 `launch --dry-run`。
- 缺少版本文件时降级为 `Warning`，使用占位 `mainClass` 继续构建命令。
- 缺少 Java 时降级为 `Warning`，使用 `java` 作为可执行文件；Java 版本不匹配也降级为 `Warning`。
- 缺少游戏关键文件时降级为 `Warning`，仍生成预期命令。
- `natives` 返回 `Skipped`；`eula` 在未同意时返回 `Warning`。
- `start` 步骤返回 `Skipped`，不启动进程。
- 仍然完整走完流水线，因此能验证路径、参数与内存配置。

### 真实启动

- 缺少 Java、版本或关键文件时直接 `Failed`，不会执行后续步骤。
- 版本文件缺失、继承链缺父版本或 JSON 损坏时，`version` 步骤的失败消息会附带具体原因（缺失文件路径或解析错误），便于定位是哪个版本不完整。
- Java 版本与游戏版本不匹配时默认只 `Warning`，UI 会在启动前弹窗确认；实例设置可关闭兼容性检验。
- 未同意 EULA 时 `eula` 步骤 `Failed`；UI 首次真实启动会弹窗要求同意，记录后写入 `eula=true`。
- 所有步骤成功后，`StartGameStep` 通过 `IProcessLauncher` 启动进程。
- 启动成功后更新实例的 `LastPlayedAt`。
- 进程输出在无控制台模式下重定向到日志（前缀 `[MC]`）。

## 4. 命令构建

`CommandLineBuilder` 负责把上下文转换为：

```csharp
public sealed record LaunchCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    string Display);
```

- `Arguments` 用于 `ProcessStartInfo.ArgumentList`，避免手工拼接导致的 Windows 引号问题。
- `Display` 用于 UI/CLI 展示，会对含空格或引号的参数加引号。

当前 JVM 参数：

```text
-Xmx<memory>M
-Xms<min(1024, memory)>M
-Djava.library.path=<instance>/minecraft/natives
-Dminecraft.launcher.brand=EnderCraftLauncher
-Dminecraft.launcher.version=<launcher version>
-Dlog4j.configurationFile=<data>/assets/logging/<file id>   （版本 JSON 提供 logging 时）
<instance/global extra JVM arguments>
<java.jvmPreset 生成的 G1/ZGC/Aikar 参数>
<版本 JSON arguments.jvm>                                  （例如 Fabric 的 -DFabricMcEmu、
                                                           Forge/NeoForge 的 -DlibraryDirectory、-p）
```

当前游戏参数：

```text
-cp <libraries + client jar>
<mainClass>
--username <name>
--version <versionId>
--gameDir <instance>/minecraft
--assetsDir <data>/assets
--assetIndex <assetIndex.id> | legacy
--uuid <uuid>
--accessToken 0
--userType legacy
--versionType <release/snapshot>
--width <game.windowWidth>
--height <game.windowHeight>
--userProperties {}                                      （旧式 minecraftArguments 包含该选项时）
<instance/global extra game arguments>
<旧式 minecraftArguments 中未处理的选项>                   （例如 Cleanroom 的 --tweakClass）
<arguments.game 中的加载器专属参数>                        （例如 --launchTarget forgeclient、--fml.*）
```

加载器阶段只补充自定义选项，标准参数仍由启动器生成。`arguments.jvm` / `arguments.game` 中带 `rules` 的对象会按当前 OS、架构与 feature 过滤：Windows 上不会注入 macOS 的 `-XstartOnFirstThread`，x64 上不会注入 x86 的 `-Xss1M`；未启用的 feature（demo、自定义分辨率、quick play）对应参数会被丢弃。`${library_directory}`、`${classpath}`、`${classpath_separator}`、`${user_properties}`、`${auth_player_name}` 等占位符会被替换；无法解析的占位符所对应的选项会被丢弃，而不是把字面量传给游戏。版本 JSON 中的 `-cp` 由启动器统一接管，避免重复类路径。

### 额外参数解析

- `java.extraArguments` / `game.extraArguments` 是原始字符串。
- 支持双引号与单引号包裹带空格的参数；不做 shell 展开。
- 实例值优先于全局值；取到实例值时不再追加全局值。

### 类路径

- 通过 `PlatformLibrarySelector` 按当前 OS/架构与 library `rules` 过滤。
- 继承链合并时加载器库排在原版库之前（Forge 1.7.10 的 guava 17.0 需要覆盖原版 15.0）。
- 现代版本的 natives 以 `natives-<os>[-arch]` classifier 形式出现在 `name` 中；每组按架构选择唯一变体。
- 类路径只包含主 artifact；natives classifier 与 legacy `Natives` 的 native jar 不进入类路径。
- 库路径来自 `downloads.artifact.path`，缺失时按 Maven 坐标推导。
- 追加实例的 client jar：优先 `<data>/versions/<versionId>/<versionId>.jar`；该文件不存在时回退到继承链中声明 `downloads.client` 的版本目录，兼容其它启动器生成的加载器版本。
- 版本 JSON 缺失时使用 `<classpath>` 占位（仅 Dry Run）。

## 5. 失败策略

- 步骤失败 → 立即停止，报告包含已完成和失败步骤。
- 步骤警告 → 继续，最终 `Success = true`，但保留警告。
- 步骤异常 → 捕获、记录日志、转换为 `Failed`，不会让 UI 崩溃。
- `LaunchReport` 包含总耗时、每一步耗时、命令、PID。

UI 与 CLI 使用同一份报告，只是渲染方式不同。

## 6. 进程启动

`SystemProcessLauncher`：

- `UseShellExecute = false`。
- 使用 `ArgumentList` 逐项传参。
- `game.showConsole = false` 时 `CreateNoWindow = true`，重定向 stdout/stderr 到日志。
- `game.showConsole = true` 时显示 Java 控制台窗口，不重定向。
- 进程退出时记录退出码并释放句柄。

`IProcessLauncher` 是接口，测试使用 `FakeProcessLauncher` 验证参数与调用次数。

## 7. 当前边界与缺口

- 启动流水线本身提供阶段状态、耗时、警告与失败报告；启动流程尚未作为 Operation 发布到任务中心（下载、安装、整合包、内容与 Java 已接入，见 [operations.md](operations.md)）。
- 崩溃分析与脱敏诊断包已实现，详见 [diagnostics.md](diagnostics.md)；它们不等于完整的 JVM 崩溃符号化分析。
- 下载并发、重试、断点续传、暂停/恢复、流式进度、速度/ETA 与失败归因已实现；下载限速尚未实现，详见 [operations.md](operations.md)。
- Java 自动安装支持 Mojang、Adoptium、Zulu 与 Liberica；内置 JVM 预设与外部 JSON 自定义预设均已支持，但 UI 尚不提供命名预设编辑器，详见 [java.md](java.md)。
- Microsoft 与 Authlib-Injector/Yggdrasil 认证已实现；账户中心的注销、凭据清理与失效重认证体验仍需完善，详见 [authentication.md](authentication.md)。

本文档描述启动流水线，不把下载、崩溃诊断或账户 UI 的缺口混写为启动步骤缺失。

## 8. 扩展方式

### 新增步骤

```csharp
internal sealed class CheckModConflictsStep : ILaunchStep
{
    public string Id => "mods";
    public int Order => 340;

    public Task<LaunchStepResult> ExecuteAsync(LaunchContext context, CancellationToken ct)
    {
        // 检查 Mod 依赖/冲突，返回 Success / Warning / Failed
    }
}
```

在 `AddEclMinecraft` 注册：

```csharp
services.AddSingleton<ILaunchStep, CheckModConflictsStep>();
```

并补充 `launch.step.mods` 与相关消息的本地化键。

### 新增账户类型

账户解析在 `LaunchService` 中完成；未来 Microsoft / 第三方账户只需提供 `Account` 的有效 `AccessToken`、`Uuid`、`UserType`，命令构建保持不变。令牌刷新应放在认证模块，不放进启动步骤。
