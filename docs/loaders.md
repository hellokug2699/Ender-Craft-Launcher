# 加载器与组件

v0.5 支持 **Fabric**、**Forge**、**NeoForge** 与 **Cleanroom**。Quilt、Legacy Fabric 不在支持范围内。

## 支持矩阵

| 加载器 | 游戏版本 | Java | 说明 |
| --- | --- | --- | --- |
| Vanilla | 全部 | 按版本 JSON | 原版安装 |
| Fabric | Fabric Meta 支持的版本 | 继承原版要求 | 自动安装 Fabric API |
| Forge | Forge Maven 中存在的版本 | 继承原版要求 | 安装器 spec 0（≤1.12.2）直接解压；spec 1+ 运行 processors |
| NeoForge | 1.20.1 起（1.20.1 使用 `net.neoforged:forge` 产物） | 继承原版要求 | 与 Forge 相同的安装器机制 |
| Cleanroom | 仅 1.12.2 | Java 25 | 从官方安装器提取完整版本 JSON |

## 版本目录

安装前会列出可用版本与更新时间，UI 与 CLI 使用同一套数据。

### Fabric Loader

- 版本列表：`https://meta.fabricmc.net/v2/versions/loader/<game>`
- 安装配置：`https://meta.fabricmc.net/v2/versions/loader/<game>/<loader>/profile/json`
- 更新时间：GitHub `FabricMC/fabric-loader` 的 releases Atom 源。
  Atom 只包含最近若干条发布；更早版本显示“更新时间未知”。

### Fabric API

- 版本列表：Modrinth `fabric-api` 项目，按 `game_versions` 与 `loaders=fabric` 过滤。
- 更新时间：Modrinth 的 `date_published`。
- 选择 Fabric 时 **必定安装 Fabric API**，默认选择该游戏版本可用的最新版本。UI 会显示“将会自动安装 Fabric API”横幅。

### Forge

- 版本列表：`https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml`
  的 `<version>` 列表，按 `<游戏版本>-<Forge 版本>` 过滤并倒序。安装时使用 `<Forge 版本>`。
- 安装器：`https://maven.minecraftforge.net/net/minecraftforge/forge/<game>-<forge>/forge-<game>-<forge>-installer.jar`
- 更新时间：Maven 元数据不提供逐版本时间，列表显示“更新时间未知”。

### NeoForge

NeoForge 的 Maven 版本号随游戏版本变化：

| 游戏版本 | Maven 产物 | 版本前缀 |
| --- | --- | --- |
| 1.20.1 | `net/neoforged/forge` | `1.20.1-47.1.x`（存储为 `47.1.x`） |
| 1.20.2 – 1.21.x | `net/neoforged/neoforge` | 去掉 `1.` 前缀，如 `1.21.1` → `21.1.x` |
| 26.x | `net/neoforged/neoforge` | 补足三段，如 `26.3` → `26.3.0.x` |

- 版本列表：`https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml`
  （1.20.1 使用 `net/neoforged/forge/maven-metadata.xml`）。
- 安装器：`https://maven.neoforged.net/releases/net/neoforged/neoforge/<version>/neoforge-<version>-installer.jar`
- 更新时间：Maven 元数据不提供逐版本时间，列表显示“更新时间未知”。
- `-beta` / `-alpha` 版本标记为测试版，默认仍选最新版本。

### Cleanroom

- 版本列表：`https://repo.cleanroommc.com/releases/com/cleanroommc/cleanroom/maven-metadata.xml`
- 安装器：`https://repo.cleanroommc.com/releases/com/cleanroommc/cleanroom/<version>/cleanroom-<version>-installer.jar`
- 更新时间：GitHub `CleanroomMC/Cleanroom` 的 releases Atom 源。
- 安装器是 Forge 风格 zip，ECL 读取其中的 `version.json` 与 `install_profile.json`：
  - `version.json` 是完整版本定义（含 mainClass、Java 25、客户端下载、217 个库、资源索引）。
  - `install_profile.json.repositories` 用于补全 `url` 为空的库（例如 `com.cleanroommc` → `maven.cleanroommc.com`）。
  - 不执行安装器进程，避免交互式 GUI 与额外副作用。
- Cleanroom 官方建议搭配 `Fugue` 与 `Scalar Legacy` 以兼容旧 Forge 模组；它们是普通 Mod，ECL 当前不自动安装，Mod 管理在 v0.6 接入。

### OptiFine

- 版本列表与下载：BMCLAPI `https://bmclapi2.bangbang93.com/optifine/<game>`。
- 更新时间：BMCLAPI 记录 `_id`（MongoDB ObjectId）前 4 字节的时间戳，表示该版本入库时间。
- **分发限制**：ECL 不随启动器分发 OptiFine，也不会自动安装。只有用户在创建实例时显式选择 OptiFine 才会下载到实例 `mods/` 目录。
- **兼容性限制**：当前不支持 OptiFabric，NeoForge 也没有对应 OptiFine 版本。选择 Fabric、NeoForge 或原版（不选加载器）时，OptiFine 选项会被禁用；Forge 与 Cleanroom 可选。

## Forge / NeoForge 安装机制

安装器 jar 内包含 `install_profile.json` 与 `version.json`：

- **spec 0（≤1.12.2）**：不需要 processors，也不需要 Java。ECL 直接使用 `version.json` / `versionInfo`：
  - Forge 产物在 Maven 中只有 `-universal.jar`，ECL 会把 `net.minecraftforge:forge` / `net.neoforged:forge` 的地址无条件改为 `<maven>/...-universal.jar`；
  - 库条目可能给出 Maven 根地址（如 `https://maven.minecraftforge.net/`）或完整文件 URL，ECL 会区分两者，避免重复拼接路径；
  - 没有 URL 的旧依赖（`launchwrapper`、`lzma` 等）回退到 Mojang 官方库 `https://libraries.minecraft.net/`。
- **spec 1+（1.13+ / NeoForge）**：安装器需要运行 processors 才能生成打补丁的客户端 jar
  （`libraries/net/.../<artifact>-<version>-client.jar`）。ECL 会：
  1. 下载 `install_profile.json.libraries` 到共享 `libraries/`（含 installertools、jarsplitter、
     AutoRenamingTool、binarypatcher、mappings 等）。
  2. 由 `ForgeProcessorPlanner` 解析 `{INSTALLER}`、`{ROOT}`、`{MINECRAFT_JAR}`、`{SIDE}`、
     `{LIBRARY_DIR}` 以及 `data` 中的 `{MAPPINGS}`、`{PATCHED}` 等占位符；
     `[...]` 形式的 Maven 引用会转换为 `libraries/` 下的实际路径，
     `/data/*.lzma` 会从安装器 zip 解压到 `cache/forge/`。
  3. 按 `sides` 过滤（客户端只运行 `client` 与未声明 side 的处理器），使用实例可用的 Java
     逐个执行处理器。任一步退出码非 0 时安装失败并给出处理器名称与退出码。
  4. 校验 `PATCHED` 输出文件存在，否则判定安装失败。
- 运行 processors 需要本机存在可用 Java；ECL 复用实例的 Java 选择（默认按版本 JSON 的
  `javaVersion` 选取），未检测到 Java 时提示先安装/指定 Java。
- 只有 `install_profile.json` 中确实存在客户端 processors 时才会要求 Java；spec 0 的 Forge
  以及没有 processor 的安装器不依赖 Java。
- 下载源策略中，`maven.minecraftforge.net` 与 `maven.neoforged.net/releases` 会映射到
  BMCLAPI 的 `/maven/` 镜像。

## 安装布局

实例版本 ID 形如：

- `1.21.1-fabric-0.20.1`
- `1.20.1-forge-47.4.0`
- `1.21.1-neoforge-21.1.264`
- `1.12.2-cleanroom-0.7.12-alpha`

安装结果：

```
versions/<version-id>/<version-id>.json   # 加载器版本 JSON
versions/<version-id>/<version-id>.jar    # 客户端 jar
libraries/                                # 原版 + 加载器依赖（含 processors 产物）
assets/                                   # 资源索引与对象
instances/<instance-id>/minecraft/mods/   # Fabric API / OptiFine
cache/forge/                              # 安装器及其解压的 data 文件
```

Fabric / Forge / NeoForge 版本 JSON 保留 `inheritsFrom`，依赖原版版本 JSON；Cleanroom 版本 JSON 是独立的完整定义。

## 安装失败回滚与诊断

安装过程记录在 `InstallTransaction` 中：

- 本次新建的 client、库、资源对象、Forge processors 生成的 patched client jar、Fabric API / OptiFine mod 在失败时删除；
- 被覆盖的加载器版本 JSON 恢复为安装前内容；
- 原本已存在的共享库与资源不会删除，避免影响其他实例；
- 失败原因写入 `<data>/logs/install/install-<实例ID>-<时间>.json`，包含实例、加载器、错误码、
  错误详情、运行环境与回滚操作记录，便于反馈问题。

## 启动参数

- 继承链合并时，加载器库排在原版库之前：Forge 1.7.10 的 guava 17.0 必须先于原版 1.7.10 的 15.0 进入类路径，否则会触发 `NoSuchMethodError`。
- Fabric profile 的 `arguments.jvm` 会注入启动命令，例如 `-DFabricMcEmu= net.minecraft.client.main.Main`。
- Cleanroom 与 1.12.2 Forge 的旧式 `minecraftArguments` 中启动器未处理的选项会被补充，
  例如 `--tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker`。
- 现代 Forge / NeoForge 的 `arguments.jvm` 会注入 `-DlibraryDirectory=${library_directory}` 与
  `-p <module path>`；`${library_directory}`、`${classpath_separator}` 由启动器替换为实际路径。
- `arguments.game` 中的加载器专属参数（`--launchTarget forgeclient`、`--fml.*`）会被提取并追加到游戏参数；
  其余原版参数仍由启动器自行生成。

## CLI

```powershell
ecl loader list --version 1.21.1 --type fabric
ecl loader list --version 1.20.1 --type forge
ecl loader list --version 1.21.1 --type neoforge
ecl loader list --version 1.21.1 --type fabric-api
ecl loader list --version 1.12.2 --type cleanroom
ecl loader list --version 1.12.2 --type optifine

ecl instance create "Forge 生存" --version 1.20.1 --loader forge
ecl instance create "NeoForge 生存" --version 1.21.1 --loader neoforge
ecl instance create "Cleanroom 整合" --version 1.12.2 --loader cleanroom --loader-version 0.7.12-alpha
ecl instance create "Cleanroom + OptiFine" --version 1.12.2 --loader cleanroom \
  --loader-version 0.7.12-alpha --optifine-version HD_U:G5
ecl install <实例ID>
```

未指定 `--loader-version` 时会使用该游戏版本最新加载器；Fabric 未指定 `--fabric-api-version` 时使用最新 API。
