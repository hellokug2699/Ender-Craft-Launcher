# ADR 0008：Forge 与 NeoForge 安装器 processors

- Status: Completed
- Date: 2026-10-27
- Scope: ECL 架构与实现
- 相关版本：v0.5（加载器支持扩展）

## 背景

ADR 0007 明确了 Fabric/Cleanroom 的安装方式，并明确不执行安装器进程。Forge 1.13+ 与 NeoForge 的安装器
（`install_profile.json` spec 1）没有可直接下载的完整客户端：必须运行安装器自带的 processors
（MCP 数据提取、jar 拆分、重混淆、二进制补丁），才能得到 `libraries/.../<artifact>-<version>-client.jar`。
不运行 processors 无法启动游戏，因此本 ADR 对 Forge/NeoForge 例外于 ADR 0007 的“不执行安装器”决策。

## 决策

### 1. 区分 spec 0 与 spec 1+

- `spec < 1`（例如 1.12.2）：安装器 zip 内的 `version.json` 已是完整版本定义，Forge 产物是
  `<maven>/.../forge-<version>-universal.jar`。ECL 只补齐空 URL，按普通版本安装。
- `spec >= 1`：下载安装器，读取 `install_profile.json` 与 `version.json`，写入加载器版本 JSON，
  正常运行原版 + 加载器库/资源下载，最后执行 processors。

### 2. processors 由 ECL 直接运行

- 下载 `install_profile.libraries` 到共享 `libraries/`，其中包括 installertools、jarsplitter、
  AutoRenamingTool、binarypatcher 与 mappings。
- 按 `sides` 过滤：客户端只执行 `client` 和未声明 side 的处理器；服务端处理器跳过。
- 处理器以 `java -cp <jar+classpath> <Main-Class> <args...>` 运行，主类从 jar 的
  `META-INF/MANIFEST.MF` 读取，不写死处理器实现类。
- 参数占位符（`{INSTALLER}`、`{ROOT}`、`{MINECRAFT_JAR}`、`{SIDE}`、`{LIBRARY_DIR}`、`data` 中的键）
  由 `ForgeProcessorPlanner` 解析；`[...]` Maven 引用映射到 `libraries/` 路径，
  `/data/*` 从安装器 zip 解压到 `cache/forge/<installer>/`。
- 任一处理器退出码非 0 或 `PATCHED` 输出缺失时，安装失败并返回处理器名称与退出码。

### 3. 安装时 Java 复用实例的 Java 选择

- 使用 `IJavaSelectionService`（与启动相同的设置：实例 Java、全局 Java、自动探测）。
- 未检测到 Java 时给出明确错误，而不是静默产出不可启动的实例；Java 自动下载仍由 v0.4 的 Java 管理负责。

### 4. 版本目录与版本号

- Forge：`maven-metadata.xml` 中 `<game>-<forge>` 形式的版本，按游戏版本过滤，存储 `forge` 部分。
- NeoForge：
  - 1.20.1 使用 `net.neoforged:forge`，版本形如 `1.20.1-47.1.x`，存储 `47.1.x`；
  - 1.20.2+ 使用 `net.neoforged:neoforge`，版本前缀为去掉 `1.` 的游戏版本（`1.21.1` → `21.1.x`）；
  - 26.x 补足三段（`26.3` → `26.3.0.x`）。
- Maven 元数据不含逐版本更新时间，UI 显示“更新时间未知”。

### 5. 下载镜像

- `maven.minecraftforge.net` → BMCLAPI `/maven/`；
- `maven.neoforged.net/releases` → BMCLAPI `/maven/`（去掉 `/releases` 前缀）。
- 与 ADR 0005 的四档策略一致；第三方 Maven 地址在镜像模式仍有镜像可用。

### 6. 启动参数

- JVM 参数沿用版本 JSON 的 `arguments.jvm`，新增 `${library_directory}` 与 `${classpath_separator}` 替换。
- 仅从 `arguments.game` 提取加载器专属参数（`--launchTarget`、`--fml.*`），避免重复原版参数。
- 1.12.2 Forge 沿用旧式 `minecraftArguments`，补充 `--tweakClass`。

### 7. OptiFine 范围

- OptiFine 以 mod 形式安装，因此对 Forge 与 Cleanroom 开放；Fabric、NeoForge、原版禁用。
- 仍由用户显式选择，ECL 不自动安装、不分发。

## 后果

- Forge/NeoForge 安装依赖本机 Java 与安装器 processors；失败时错误可定位到具体处理器。
- 安装过程有额外下载（安装器 + processors 依赖），首次安装耗时高于原版。
- 旧版 Forge（spec 0）不运行处理器，安装更轻量。
- 处理器输出（client jar）进入共享 `libraries/`，可被多个实例复用。
- 若安装器格式在未来发生变化，processor 规划器与安装器读取器是集中修改点。
