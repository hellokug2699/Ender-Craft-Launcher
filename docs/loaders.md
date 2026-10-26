# 加载器与组件

v0.5 支持 **Fabric** 与 **Cleanroom**。Quilt、Legacy Fabric、Forge、NeoForge 不在本阶段范围内；Forge/NeoForge 的 installer processors 留到后续版本。

## 支持矩阵

| 加载器 | 游戏版本 | Java | 说明 |
| --- | --- | --- | --- |
| Vanilla | 全部 | 按版本 JSON | 原版安装 |
| Fabric | Fabric Meta 支持的版本 | 继承原版要求 | 自动安装 Fabric API |
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
- **兼容性限制**：当前不支持 OptiFabric。选择 Fabric 或原版（不选加载器）时，OptiFine 选项会被禁用；只有 Cleanroom 加载器可选 OptiFine。

## 安装布局

实例版本 ID 形如：

- `1.21.1-fabric-0.20.1`
- `1.12.2-cleanroom-0.7.12-alpha`

安装结果：

```
versions/<version-id>/<version-id>.json   # 加载器版本 JSON（Fabric profile 或 Cleanroom version.json）
versions/<version-id>/<version-id>.jar    # 客户端 jar
libraries/                                # 原版 + 加载器依赖
assets/                                   # 资源索引与对象
instances/<instance-id>/minecraft/mods/   # Fabric API / OptiFine
```

Fabric 版本 JSON 保留 `inheritsFrom`，依赖原版版本 JSON；Cleanroom 版本 JSON 是独立的完整定义。

## 启动参数

- Fabric profile 的 `arguments.jvm` 会注入启动命令，例如 `-DFabricMcEmu= net.minecraft.client.main.Main`。
- Cleanroom 旧式 `minecraftArguments` 中启动器未处理的选项会被补充，例如 `--tweakClass net.minecraftforge.fml.common.launcher.FMLTweaker`。
- 启动器仍自行生成标准参数（用户名、目录、资源索引等），不依赖版本 JSON 的完整 `arguments` 占位符替换。

## CLI

```powershell
ecl loader list --version 1.21.1 --type fabric
ecl loader list --version 1.21.1 --type fabric-api
ecl loader list --version 1.12.2 --type cleanroom
ecl loader list --version 1.12.2 --type optifine

ecl instance create "Fabric 生存" --version 1.21.1 --loader fabric
ecl instance create "Cleanroom 整合" --version 1.12.2 --loader cleanroom --loader-version 0.7.12-alpha
ecl instance create "Cleanroom + OptiFine" --version 1.12.2 --loader cleanroom \
  --loader-version 0.7.12-alpha --optifine-version HD_U:G5
ecl install <实例ID>
```

未指定 `--loader-version` 时会使用该游戏版本最新加载器；Fabric 未指定 `--fabric-api-version` 时使用最新 API。
