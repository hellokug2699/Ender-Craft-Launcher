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
| 200 | `java` | `ResolveJavaStep` | 实例/全局路径、自动选择、兼容性检查、内存计算 |
| 350 | `files` | `ValidateGameFilesStep` | 检查 client、库、natives、资源索引、日志配置 |
| 360 | `natives` | `ExtractNativesStep` | 清理并解压实例隔离的 natives |
| 370 | `eula` | `EnsureEulaStep` | 检查/写入 `eula.txt`，未同意则阻止真实启动 |
| 400 | `command` | `BuildCommandStep` | 构建 JVM/游戏参数与可读命令 |
| 500 | `start` | `StartGameStep` | Dry Run 跳过；真实启动时拉起进程 |

步骤按 `Order` 排序，由 `LaunchService` 注入 `IEnumerable<ILaunchStep>`。新增步骤只需实现接口并注册。

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
- Java 版本与游戏版本不匹配时默认只 `Warning`，UI 会在启动前弹窗确认；实例设置可关闭兼容性检验。
- 未同意 EULA 时 `eula` 步骤 `Failed`；用户显式勾选后写入 `eula=true`。
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
<instance/global extra game arguments>
```

### 额外参数解析

- `java.extraArguments` / `game.extraArguments` 是原始字符串。
- 支持双引号与单引号包裹带空格的参数；不做 shell 展开。
- 实例值优先于全局值；取到实例值时不再追加全局值。

### 类路径

- 通过 `PlatformLibrarySelector` 按当前 OS/架构与 library `rules` 过滤。
- 现代版本的 natives 以 `natives-<os>[-arch]` classifier 形式出现在 `name` 中；每组按架构选择唯一变体。
- 类路径只包含主 artifact；natives classifier 与 legacy `Natives` 的 native jar 不进入类路径。
- 库路径来自 `downloads.artifact.path`，缺失时按 Maven 坐标推导。
- 追加 `<data>/versions/<versionId>/<versionId>.jar`。
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

## 7. 当前缺口

原版安装与启动闭环已完成，仍缺以下能力：

1. Java 运行时按 Minecraft 版本自动匹配/下载（v0.4）；当前选择本机最高版本。
2. 版本 JSON `arguments` 体系的占位符替换；当前由启动器自建参数，原版可用，加载器阶段需要完整支持。
3. 游戏日志单独落盘、崩溃报告解析与 Mod 嫌疑排序。
4. 加载器/整合包安装与继承链生成。
5. 下载限速（并发、重试、续传已实现）。

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
