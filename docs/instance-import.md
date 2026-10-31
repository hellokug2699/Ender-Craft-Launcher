# 导入实例回归（P1 1.1）

扫描并导入其它启动器留下的 `versions/<id>` 安装时，ECL 只建立引用：不复制、不移动、不改写原安装文件。
这份文档说明回归样本、测试覆盖的边界，以及当前验证状态。

## 1. 样本

样本由 `tests/ECL.Tests/Minecraft/ImportInstallationFixture.cs` 生成，内容固定，可重复运行。
所有版本 JSON 中记录的 SHA-1 与大小都来自实际写出的文件，因此“文件齐全”的样本可以通过启动前的文件校验。

| 样本 | 生成方法 | 布局 | 用途 |
| --- | --- | --- | --- |
| 共享原版 | `WriteSharedVanilla` | `versions/1.21.1/`，无隔离标记；游戏目录为 `.minecraft` 根 | 共享目录导入、启动与文件校验 |
| 隔离 Fabric | `WriteIsolatedFabric` | `versions/fabric-loader-0.16.0-1.21.1/` 内含 `mods/`、`config/`、`saves/`、`options.txt`，`inheritsFrom: 1.21.1` | 版本隔离、继承链、client jar 归属、内容路径 |
| 隔离 Forge | `WriteIsolatedForge` | `versions/1.20.1-forge-47.4.0/`，`inheritsFrom: 1.20.1`，`--fml.mcVersion` | Forge 合并版本、缺失可选文件诊断 |
| 缺失可选文件 | `new InstallFileOptions { Libraries = false, AssetObjects = false, LoggingConfig = false }` | 版本 JSON 仍声明这些文件 | “导入的实例不阻止启动，但给出警告” |
| 损坏版本 JSON | `WriteCorruptVersion` | JSON 被截断 | 扫描拒绝、导入报错 |
| 不完整版本 JSON | `WriteIncompleteVersion` | `{}` 之类没有 `mainClass` 的 JSON | 扫描拒绝、导入报错 |
| 缺父版本 | `WriteOrphanVersion` | `inheritsFrom` 指向不存在的版本 | 扫描标记为不完整；导入后父版本被外部删除时启动给出路径诊断 |
| 非版本目录 | `WriteDirectoryWithoutManifest` | `versions/assets/`、`versions/unrelated/` | 扫描必须忽略 |

文件树快照（`FileTreeSnapshot`）记录相对路径 → SHA-256，`Diff` 同时报告新增、删除与内容变化。
共享样本只比较游戏数据（`versions/`、`libraries/`、`assets/` 与根目录文件），避免把 ECL 自己的
`instances/`、`config/` 等数据目录算作安装内容。

## 2. 覆盖范围

`tests/ECL.Tests/Minecraft/ImportRegressionTests.cs`：

| 测试 | 验证点 |
| --- | --- |
| `ScanListsOnlyDirectoriesWithACompleteVersionManifest` | 只识别带同名 JSON 且继承链完整的版本；损坏/不完整版本给出原因；重复扫描不产生实例；已导入标记 |
| `ImportRejectsCorruptAndIncompleteVersionsWithDiagnosticCodes` | `instance.version_invalid` / `instance.version_incomplete` 与可读原因 |
| `ImportAndRepeatedImportLeaveTheOriginalInstallationUntouched`（共享/隔离） | 导入前后文件哈希不变；重复导入返回 `instance.already_imported`；不创建 ECL 自建游戏目录 |
| `SharedVanillaImportLaunchesFromTheExistingGameRoot` | 共享目录的 `--gameDir`、client jar、logging 参数；真实启动（假 Java/进程启动器）成功 |
| `IsolatedFabricImportUsesTheInheritedClientJarAndVersionDirectory` | 加载器版本没有自己的 jar 时，类路径使用被继承的原版 jar；`--gameDir` 指向版本目录 |
| `IsolatedForgeImportWithMissingOptionalFilesWarnsAndStillLaunches` | 缺失库/资源对象/日志配置时 `files` 步骤为 `Warning`；不注入 log4j 参数；仍能启动 |
| `ImportedInstanceWithMissingParentFailsWithAPathDiagnostic` | 父版本被删除时 `version` 步骤失败，消息包含缺失路径；不重建父版本 |
| `ImportedInstanceWithRemovedVersionDirectoryFailsWithoutRecreatingIt` | 版本目录被外部移动/删除时明确失败，不创建空目录 |
| `ImportedInstanceModsUseTheRealGameDirectory` | Mod 列表、禁用、导入、删除都作用于实际游戏目录 |
| `ImportedInstanceContentPacksUseTheRealGameDirectory` | 资源包/光影包列表、导入、启用写入实际游戏目录的 `options.txt` |
| `ImportedInstanceCrashReportsAndDiagnosticsComeFromTheRealGameDirectory` | 崩溃分析来源与诊断包条目来自实际游戏目录 |
| `ScanResolvesTheMinecraftVersionThroughNestedLoaderProfiles` | 多层继承时 GameVersion 解析到最底层原版版本；实例列表修复不会写成加载器配置名 |
| `ImportUsesTheRequestedDisplayName` | 自定义显示名称不影响版本与目录 |

## 3. 运行

```powershell
dotnet test tests/ECL.Tests/ECL.Tests.csproj --filter FullyQualifiedName~ImportRegressionTests
```

完整回归：

```powershell
dotnet test tests/ECL.Tests/ECL.Tests.csproj
```

## 4. 历史验证记录

以下结果记录于 2026-10-31，仅适用于当时执行的代码与提交，不代表当前分支 HEAD 已重新运行验证。

- 2026-10-31，Windows 10/11 + .NET SDK 10.0.403：
  `dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Debug` → 296 通过，0 失败；
  `ImportRegressionTests` 共 14 个用例（含 2 个共享/隔离参数化用例）。
- 本次修复的导入路径缺陷：加载器版本没有自己的 client jar 时类路径指向不存在的空 jar；
  损坏/不完整版本 JSON 仍可导入且启动失败缺少原因；多层继承时 `GameVersion` 会被写成加载器配置名。
- 测试使用假 Java 可执行文件与 `IProcessLauncher`，不下载、不启动真实 Minecraft；
  真实账户、真实游戏与真实共享 `.minecraft` 的实机验收仍需按路线图门槛单独执行。
- Windows / Linux CI：[run #163](https://github.com/hellokug2699/Ender-Craft-Launcher/actions/runs/41182107585)，
  提交 `c505cde`，`completed` / `success`（Windows 构建、格式检查、测试；Linux 测试）。

## 5. 已知限制

- 样本中的版本 JSON 是最小可用结构，不覆盖所有加载器/整合包的真实字段组合；
  真实安装回归仍建议手工导入一份原版与一份 Fabric/Forge 安装各一次。
- 启动可能写入 `eula.txt`（游戏要求）与 `options.txt`（用户显式启用资源包/光影时）；
  “导入不修改原文件”的断言只覆盖扫描与导入流程本身。
- 目录共享、删除保护与迁移属于 P1 1.2/1.3，本文档不覆盖。
