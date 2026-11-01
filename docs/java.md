# Java 管理

Minecraft 对 Java 版本的要求随版本变化：1.16 及以下通常需要 Java 8，1.17 需要 16，1.18–1.20.4 通常需要 17，1.20.5–1.21.x 需要 21；2026 年起的 26.x 需要 Java 25（版本 JSON 可能声明 26）。Java 管理模块负责“选哪个 Java、能不能用、没有怎么办”。

**ECL 只检查最低 Java 主版本，不限制最高版本。** 例如 1.20.1 使用 Java 8 会被判定过低；1.20.1 使用 Java 25 不会因为“太新”而被阻止。

## 1. 选择顺序

1. **实例指定**：`instance.java.executablePath` 是实例级显式 pin；如果存在，则优先使用该 Java，但仍必须通过兼容性检查。
2. **全局优先级**：`java.defaultPath` 中的 Java 按用户配置顺序参与候选排序；第一个不兼容当前实例的 Java 会被跳过。
3. **自动发现**：未列入全局优先级的已发现 Java 作为后备候选，仍按发现顺序参与选择。
4. **无兼容运行时**：返回结构化的“没有满足要求的 Java”结果；不能为了启动而自动选择一个已知不兼容的运行时。
5. **未找到 Java**：真实启动失败并提示下载；UI 可直接点击“下载 Java”。

`java.defaultPath` 支持单个 `java.exe`、单个 Java 主目录、包含多个 JDK/JRE 的上级目录，以及用分号或换行分隔的多个路径。多个路径的顺序就是全局 Java 优先级；父目录会把其下已发现的运行时作为该位置的候选组，而不会把父目录本身锁定为某一个 Java。

实例工作区的 Java 选择器包含“自动”与所有探测到的运行时。选择“自动”会清空实例路径，交由上述优先级与兼容性逻辑决定。实例级显式 Java 仍用于高级用户的固定运行时场景，但不能绕过硬兼容性要求。

## 2. 版本要求

`JavaRequirement` 包含：

- `MinimumMajor`：最低 Java 主版本。
- `Component`：Mojang 运行时组件名，例如 `java-runtime-gamma`。

来源优先级：

1. **版本 JSON 的 `javaVersion.majorVersion` / `javaVersion.component` 是唯一权威来源**，覆盖 1.x、26.x 以及未来版本号格式。
2. 只有旧版本 JSON 缺少该字段时才按版本号推断：
   - `1.0`–`1.16.x` → Java 8。
   - `1.17.x` → Java 16。
   - `1.18`–`1.20.4` → Java 17。
   - `1.20.5`–`1.21.x` → Java 21。
   - `26.x` → Java 25。
   - 快照：`24w+` → 21，`26w+` → 25，更早按年份推断。
   - 无法识别的自定义版本回退到 21。

Cleanroom 的 `version.json` 声明 `javaVersion.majorVersion = 25`，因此 1.12.2 Cleanroom 实例会被要求使用 Java 25，而不是原版 1.12.2 的 Java 8。

后续应继续将 Loader、Mod 等运行时约束合并到实例的有效 Java 要求中；单独一个 Minecraft 版本要求不能代表整个实例的最终要求。

## 3. 兼容性检查

`JavaCompatibility` 返回三种状态：

| 状态 | 含义 |
| --- | --- |
| `Compatible` | 满足最低要求 |
| `TooOld` | 低于最低版本，例如 1.20.1 使用 Java 8 |
| `Unknown` | 无法解析 Java 版本 |

**不存在“Java 版本过高”的拦截**：高于最低要求一律视为兼容，除非未来引入有证据支持的更具体约束。

Java 兼容性检查是启动前置条件：

- UI 在实例工作区显示兼容状态。
- 已知不兼容的 Java 不会被自动选择。
- 实例显式指定了不兼容 Java 时，启动流水线会拒绝真实启动；不会再提供“忽略兼容性检查后继续”的绕过路径。
- 旧配置中的 `instance.java.disableCompatibilityCheck` 不再改变 Core 的启动行为；后续 UI 与配置文档应移除该选项。
- Dry-run 可以报告不兼容状态供用户诊断，但真实启动必须阻止。

## 4. 自动下载 Java

当没有可用/兼容的 Java 时，UI“下载 Java”与 CLI `java install <实例ID>` 会调用 `IJavaInstallService`。
若实例的版本 JSON 尚未生成（例如刚创建、加载器安装还未完成），会按实例的 `GameVersion` 推断 Java 要求，避免“先有版本 JSON 才能下载 Java、先有 Java 才能安装加载器”的死循环。

1. 读取所选来源的 Java 运行时列表：
   - `mojang`：`https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json`（镜像受四档下载源策略控制）。
   - `adoptium`：Adoptium API v3（`api.adoptium.net`，JDK zip）。
   - `zulu`：Azul Metadata API（`api.azul.com`，JDK zip/tar.gz）。
   - `liberica`：BellSoft API（`api.bell-sw.com`，JDK zip/tar.gz）。
   来源由 `java.runtimeProvider` 选择，默认 `mojang`。
2. 按当前平台选择架构与包类型：Windows zip，Linux/macOS tar.gz；ECL 不接受 32 位 Java 作为 Minecraft 启动运行时。
3. Mojang 优先使用版本 JSON 指定的 `component`；第三方按 `MinimumMajor` 选择对应 feature 版本。
4. 第三方下载到 `<data>/cache/java/`，校验 SHA-1/SHA-256（提供方给出时），再解压到 `<data>/java/<provider>-<component>/`；解压后会重新探测 `bin/java`，找不到可执行文件则判定失败。
5. Mojang 仍走 manifest + `file` 条目流程，创建 `link` 条目（Linux/macOS），设置可执行位。
6. 用 `IJavaLocator` 重新探测，返回可用的 `JavaRuntime`。

Linux ARM64 在 Mojang 来源下会返回“平台不支持”；切换到 Adoptium/Zulu/Liberica 可以下载 ARM64 构建。

**用户无需预先安装 Java。** ECL 当前采用按需下载，而不是把 Java 二进制直接塞进启动器安装包：缺少运行时后，用户可在实例详情页手动下载。运行时安装到 ECL 数据目录并由启动器扫描管理；不同实例可以复用兼容的本机运行时。

## 5. 32/64 位与内存校验

Minecraft 启动运行时不支持 32 位 Java。Java discovery 应明确识别 32 位运行时并排除它们，而不是让它们进入正常候选列表。

`JavaPlatformAdvisor` 仍可在 Java 选择后提供平台与内存方面的诊断：

- 64 位系统 + 32 位 Java：应作为不可用候选，而不是普通 Warning。
- 32 位系统 + 64 位 Java：提示很可能无法启动。
- ARM64 系统 + x64 Java：提示可能需要模拟层。
- 分配内存 > 系统可用内存：提示可能卡顿或启动失败。

## 6. JVM 参数预设

`java.jvmPreset`（全局）与 `instance.java.jvmPreset`（实例覆盖）支持。全局默认值为 `g1`（推荐）；实例留空时继承全局预设：

| 预设 | 内容 |
| --- | --- |
| `default` | 不额外注入 |
| `g1` | G1 GC + 基础停顿/引用处理参数 |
| `zgc` | ZGC；Java 21–23 追加 `-XX:+ZGenerational`，24+ 不再使用该参数 |
| `aikar` | Aikar's flags（G1 大堆调优） |

预设只包含 GC/堆行为参数，`-Xms`/`-Xmx` 仍由内存设置统一生成。

### 自定义 / 导入预设

`java.customJvmPreset`（全局）与 `instance.java.customJvmPreset`（实例覆盖）可以指向一个 JSON 文件：

```json
{
  "name": "My preset",
  "arguments": ["-XX:+UseG1GC", "-XX:MaxGCPauseMillis=200"]
}
```

也支持直接使用字符串数组。加载时会拒绝 `-Xmx`、`-Xms`、`-cp`/`-classpath` 和空参数；文件不存在或格式错误时，启动命令步骤给出 Warning，并继续使用内置预设。

## 7. 数据目录

```text
<data>/java/
  java-runtime-delta/          # Mojang 组件名
    bin/java(.exe)
    release
    ...
  adoptium-java-runtime-delta/ # 第三方运行时：<provider>-<component>
  zulu-java-21/
  liberica-java-21/
```

`JavaLocator` 会扫描该目录（深度 2），因此下载完成后无需手工导入。它还会扫描全局设置 `java.defaultPath` 指定的目录：如果该目录是 `D:\java` 这类父目录，会在有限深度内递归查找 `bin/java(.exe)`，并跳过符号链接，避免误选磁盘根目录时长时间扫描。

## 8. UI 与 CLI

实例工作区：

- Java 下拉框：自动 / 本机运行时。
- “刷新”：重新扫描。
- “下载 Java”：按当前实例版本要求下载。
- Java 状态展示当前实际选择结果及兼容性原因。
- 下载运行时同时登记统一任务中心的 `Java` 任务（阶段、字节、速度、失败归因、取消/重试），见 [operations.md](operations.md)。

实例库只展示实例名称、Minecraft 版本、加载器与最近启动时间，并提供“打开工作区”入口；Java、JVM、账户、模组与诊断等设置都集中在实例工作区。

CLI：

```bash
ecl java list
ecl java install <实例ID>
ecl launch <实例ID> --dry-run
```

`ecl java install` 的进度来自统一任务中心，写入标准错误；标准输出只保留最终结果。

## 9. 限制与后续

- 当前全局 Java 优先级仍复用 `java.defaultPath` 的有序路径表示；后续 UI 应将其升级为真正的可排序 Java 列表，并提供导入、移出管理、打开文件夹等操作。
- Mod/Loader 的 Java 需求需要进入有效要求聚合器，不能只看 Minecraft 版本。
- 32 位 Java 不应进入 Minecraft 启动候选。
- Mojang 运行时在不支持的平台/架构上可能不可用；可切换 Adoptium、Zulu 或 Liberica，具体构建取决于供应商发布情况。
- Microsoft 账户登录需要 Windows DPAPI 安全凭据存储；非 Windows 平台暂不支持该凭据持久化。认证流程详见 [authentication.md](authentication.md)。
