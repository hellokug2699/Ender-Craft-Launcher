# Java 管理

Minecraft 对 Java 版本的要求随版本变化：1.16 及以下通常需要 Java 8，1.17 需要 16，1.18–1.20.4 通常需要 17，1.20.5–1.21.x 需要 21；2026 年起的 26.x 需要 Java 25（版本 JSON 可能声明 26）。Java 管理模块负责“选哪个 Java、能不能用、没有怎么办”。

**ECL 默认只检查最低 Java 主版本，不限制最高版本。** 例如 1.20.1 使用 Java 8 会被判定过低；1.20.1 使用 Java 25 不会因为“太新”而被阻止。只有存在明确证据的 `MaximumMajor` 约束时才会产生 `TooNew`。

## 1. 选择顺序

Java 发现、要求聚合、兼容性判断和最终选择都由 Core（`ECL.Java` + `ECL.Minecraft.Java`）负责；WPF 与 CLI 只调用并展示同一个 `IJavaSelectionService`，不各自实现选择规则。

1. **实例指定**：`instance.java.executablePath` 是实例级显式 pin；如果存在，则优先使用该 Java。自动选择不会在后台替换它。即使它低于有效要求，ECL 也会在启动前明确警告并要求风险确认。
2. **全局优先级**：全局 Java 列表按用户配置顺序参与候选排序；第一个不兼容当前实例的 Java 会被跳过。配置项可以指向可执行文件、Java 主目录，也可以指向包含多个 JDK/JRE 的父目录；父目录中的运行时保持发现顺序，不会被压缩成“最高版本”候选。
3. **自动发现**：未列入全局优先级的已发现 Java 作为后备候选，仍按有界发现顺序参与选择。
4. **无兼容运行时**：返回结构化的“没有满足要求的 Java”结果，附带被拒候选及原因；不能为了自动启动而选择一个已知不兼容的运行时。
5. **要求冲突**：返回可解释的冲突结果，不擅自选择“最高版本”掩盖冲突。
6. **未找到 Java**：真实启动失败并提示下载；UI 可直接点击“下载 Java”。

`java.defaultPath` 支持单个 `java.exe`、单个 Java 主目录、包含多个 JDK/JRE 的上级目录，以及用分号或换行分隔的多个路径。多个路径的顺序就是全局 Java 优先级。

实例工作区的 Java 选择器包含“自动”与所有探测到的运行时。选择“自动”会清空实例路径，交由上述优先级与兼容性逻辑决定。实例级显式 Java 是高级用户的固定运行时能力：**兼容性检查仍会执行，但已知不兼容时只发出警告，不阻止真实启动。** 这是有意保留的 escape hatch；ECL 不会替用户强制替换这种明确选择。

## 2. 有效 Java 要求

有效要求由 `EffectiveJavaRequirements` 合并多个来源，每条要求都保留 `Kind`、`Id`、`Evidence`（证据质量）、约束内容和是否硬约束：

1. **Minecraft 版本元数据**：优先使用版本 JSON 的 `javaVersion.majorVersion` / `javaVersion.component`。
2. **Loader 要求**：合并后的版本 JSON 明显高于原版基线时，按 Loader 元数据记录来源（例如 Cleanroom 对 1.12.2 声明 Java 25）。
3. **Mod 要求**：扫描已启用 Mod jar 的 base class 文件版本，得到**可证明的 Java 下界**。class file 65（Java 21）不可能在 Java 17 下加载；但字节码只是下界证据，不能证明更高 Java 一定完全兼容。多版本 jar 的 `META-INF/versions/**` 不参与下界计算，避免把仅用于新运行时的类误当成基础要求。
4. **实例显式约束**：为将来保留的扩展点；当前实例 pin 是运行时选择，不是版本要求。
5. **未知要求**：无法从元数据或版本号确定要求时记录 `Unknown` 来源。未知不等于不兼容，选择器不会据此拒绝候选，只会在状态中明确提示。

要求合并规则：

- 硬约束的最低版本取最大值；最高版本取最小值。
- `MinimumMajor > MaximumMajor` 时产生结构化冲突，并列出冲突来源。
- 组件名只从“真正决定有效最低版本”的来源继承；Mod 抬高要求时不会错误复用 Minecraft 的旧组件（例如要求 Java 25 时不会仍安装 `java-runtime-gamma`）。
- 每条来源都进入启动计划，便于回答“为什么是这个 Java”。

版本推断只在版本 JSON 缺少 `javaVersion` 时使用：

- `1.0`–`1.16.x` → Java 8。
- `1.17.x` → Java 16。
- `1.18`–`1.20.4` → Java 17。
- `1.20.5`–`1.21.x` → Java 21。
- `26.x` → Java 25。
- 快照：`24w+` → 21，`26w+` → 25，更早按年份推断。
- 无法识别的自定义版本 → `Unknown`，不再制造一个看似确定的最低版本。

Cleanroom 的 `version.json` 声明 `javaVersion.majorVersion = 25`，因此 1.12.2 Cleanroom 实例会被要求使用 Java 25，而不是原版 1.12.2 的 Java 8。

## 3. 兼容性检查与风险确认

`JavaCompatibilityStatus` 区分：

| 状态 | 含义 |
| --- | --- |
| `Compatible` | 满足所有已知硬要求 |
| `TooOld` | 低于最低版本 |
| `TooNew` | 高于明确的最高版本约束 |
| `UnsupportedArchitecture` | 32 位 Java；始终拒绝 |
| `Unknown` | 无法解析 Java 版本 |
| `UnknownRequirement` | 无法确定实例要求；不据此判定不兼容 |
| `RequirementsConflict` | 有效要求互相冲突 |

Java 兼容性检查始终执行，但自动选择与显式实例选择的策略不同：

- UI 在实例工作区显示兼容状态、要求来源与被拒候选原因。
- 已知不兼容的 Java 不会被自动选择。
- 实例显式指定了不兼容 Java 时，启动流水线要求显式风险接受；用户确认后仍保留真实兼容状态，不会伪装成 `Compatible`。
- 风险确认绑定稳定风险身份：**规范化 Java 可执行文件路径 + 检测到的 Java 版本 + 有效要求指纹（最低/最高主版本、组件与未知状态）**。路径、版本或实际生效的要求变化后，旧确认自动失效并重新提示。
- 风险确认只是一次有记录的例外，不会关闭兼容性检测，也不会影响自动选择。
- 旧配置中的 `instance.java.disableCompatibilityCheck` 已从配置定义中移除；兼容迁移不得恢复旧的全局豁免语义。
- Dry-run 同样报告真实状态，但不会因为显式 pin 而把它伪装成 `Compatible`。

## 4. 自动下载 Java

当没有可用/兼容的 Java 时，UI“下载 Java”与 CLI `java install <实例ID>` 会调用统一的 `IJavaSelectionService.ResolveRequirementsAsync` 获取有效要求，再交给 `IJavaInstallService`。若实例的版本 JSON 尚未生成（例如刚创建、加载器安装还未完成），会按实例的 `GameVersion` 推断 Java 要求，避免“先有版本 JSON 才能下载 Java、先有 Java 才能安装加载器”的死循环。有效要求冲突时不会下载，而是返回冲突说明。

1. 读取所选来源的 Java 运行时列表：
   - `mojang`：`https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json`（镜像受四档下载源策略控制）。
   - `adoptium`：Adoptium API v3（`api.adoptium.net`，JDK zip）。
   - `zulu`：Azul Metadata API（`api.azul.com`，JDK zip/tar.gz）。
   - `liberica`：BellSoft API（`api.bell-sw.com`，JDK zip/tar.gz）。
   来源由 `java.runtimeProvider` 选择，默认 `mojang`。
2. 按当前平台选择架构与包类型：Windows zip，Linux/macOS tar.gz；ECL 不接受 32 位 Java 作为 Minecraft 启动运行时。
3. Mojang 优先使用能真正满足有效最低版本的 `component`；如果声明的组件版本低于有效要求（例如 Mod 要求 Java 25），会继续按主版本选择，而不是安装旧组件。
4. 第三方下载到 `<data>/cache/java/`，校验 SHA-1/SHA-256（提供方给出时），再解压到 `<data>/java/<provider>-<component>/`；解压后会重新探测 `bin/java`，找不到可执行文件则判定失败。
5. Mojang 仍走 manifest + `file` 条目流程，创建 `link` 条目（Linux/macOS），设置可执行位。
6. 用 `IJavaLocator` 重新探测，返回可用的 `JavaRuntime`。

Linux ARM64 在 Mojang 来源下会返回“平台不支持”；切换到 Adoptium/Zulu/Liberica 可以下载 ARM64 构建。

**用户无需预先安装 Java。** ECL 当前采用按需下载，而不是把 Java 二进制直接塞进启动器安装包：缺少运行时后，用户可在实例详情页手动下载。运行时安装到 ECL 数据目录并由启动器扫描管理；不同实例可以复用兼容的本机运行时。

## 5. 32/64 位与内存校验

Minecraft 启动运行时不支持 32 位 Java。ECL 在发现阶段记录位数证据（`release` 的 `OS_ARCH`，缺失时读取 PE/ELF 头），并：

- 自动选择始终跳过 32 位候选，并在启动计划中记录拒绝原因。
- 实例显式固定的 32 位 Java 会被直接阻止，风险确认不能绕过该禁止。
- 未知位数不会被当作 64 位放行，也不会被当作 32 位拒绝；但显式固定时仍会提示无法确认架构。

`JavaPlatformAdvisor` 仍可在 Java 选择后提供平台与内存方面的诊断：

- 64 位系统 + 32 位 Java：作为不可用候选，而不是普通 Warning。
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
<data>/cache/java/
  mod-requirements.json        # Mod class 版本缓存（路径 + 长度 + 修改时间）
```

`JavaLocator` 会扫描 `<data>/java`（深度 2），因此下载完成后无需手工导入。它还会扫描全局设置 `java.defaultPath` 指定的目录：如果该目录是 `D:\JavaRuntimes` 这类父目录，会在有限深度内递归查找 `bin/java(.exe)`，并跳过符号链接，避免误选磁盘根目录时长时间扫描。

Mod class 版本扫描有界（最多 512 个 jar、每个 jar 最多 20000 个 class、并发 4）并可取消；结果按文件长度/修改时间缓存，单个损坏 jar 不会中断其它候选。

## 8. 启动计划与诊断

每次启动（包括 Dry Run）都会在 `LaunchReport.JavaPlan` 中记录：

- 最终 Java 路径、home、版本、架构、厂商；
- 有效要求摘要、详细来源与证据、要求指纹；
- 选择原因（显式固定 / 全局优先级 / 发现默认顺序）与兼容状态；
- 被拒候选及机器可读原因；
- 是否已记录风险接受。

同一份计划写入 `<data>/logs/launch/launch-<实例ID>-<时间>-<launch|dryrun>.json`，并进入诊断包的 `launch/` 目录；CLI 的 `launch` 输出也会展示该计划。这样可以直接回答“为什么用这个 Java”并复现问题。

## 9. UI 与 CLI

实例工作区：

- Java 下拉框：自动 / 本机运行时。
- “刷新”：重新扫描。
- “下载 Java”：按当前实例有效要求下载。
- Java 状态展示当前实际选择结果、兼容性原因、未知要求与冲突；显式 pin 与自动选择的状态要区分。
- 显式固定不兼容 Java 时，UI 在真实启动前明确要求风险确认；不得提供或恢复全局“忽略兼容性检查”开关。
- 下载运行时同时登记统一任务中心的 `Java` 任务（阶段、字节、速度、失败归因、取消/重试），见 [operations.md](operations.md)。

Java 管理页：

- 按全局优先级排序，可调整顺序、导入、重新扫描、打开目录；
- 取消勾选只从优先级列表移除，不删除 Java 文件；
- 显示版本、厂商、架构与 32 位/未知架构状态。

实例库只展示实例名称、Minecraft 版本、加载器与最近启动时间，并提供“打开工作区”入口；Java、JVM、账户、模组与诊断等设置都集中在实例工作区。

CLI：

```bash
ecl java list [--json]
ecl java install <实例ID>
ecl launch <实例ID> --dry-run
ecl launch <实例ID> --accept-java-risk
```

`ecl java list` 按全局优先级展示运行时，并标记 32 位/未知架构。`ecl launch` 对实例固定 Java 的已知不兼容默认失败；`--accept-java-risk` 是一次显式风险接受，会写入与 UI 相同的风险确认记录。`ecl java install` 的进度来自统一任务中心，写入标准错误；标准输出只保留最终结果。

## 10. 限制与后续

- 当前全局 Java 优先级仍复用 `java.defaultPath` 的有序路径表示；Java 管理页已提供可排序列表。
- Mod Java 要求目前来自 class 文件下界与版本元数据；完整的 Mod 元数据 Provider、可信社区数据与崩溃证据归因仍是后续工作。
- 32 位 Java 不应进入 Minecraft 启动候选；显式选择也不能把明确禁止的平台架构变成可用。
- Mojang 运行时在不支持的平台/架构上可能不可用；可切换 Adoptium、Zulu 或 Liberica，具体构建取决于供应商发布情况。
- Microsoft 账户登录需要 Windows DPAPI 安全凭据存储；非 Windows 平台暂不支持该凭据持久化。认证流程详见 [authentication.md](authentication.md)。
