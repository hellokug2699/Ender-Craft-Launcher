# Java 管理

Minecraft 对 Java 版本的要求随版本变化：1.16 及以下通常需要 Java 8，1.17 需要 16，1.18–1.20.4 通常需要 17，1.20.5–1.21.x 需要 21；2026 年起的 26.x 需要 Java 25（版本 JSON 可能声明 26）。Java 管理模块负责“选哪个 Java、能不能用、没有怎么办”。

**ECL 只检查最低 Java 主版本，不限制最高版本。** 例如 1.20.1 使用 Java 8 会被判定过低并提示；1.20.1 使用 Java 25 不会被阻止。

## 1. 选择顺序

1. **实例指定**：`instance.java.executablePath`（实例工作区中的 Java 选择器）。
2. **全局指定**：`java.defaultPath`（设置 → Java → Java 目录 / 路径）。
3. **自动选择**：扫描本机 Java，按版本要求挑选最合适的运行时。
4. **默认 Java**：若没有满足最低要求的运行时，使用探测到的最高版本，并在兼容性检查中提示。
5. **未找到**：真实启动失败并提示下载；UI 可直接点击“下载 Java”。

`java.defaultPath` 支持三种写法：单个 `java.exe`、单个 Java 主目录，或一个包含多个 JDK/JRE 的上级目录（例如 `D:\java`）。多个路径可以用分号或换行分隔。配置为上级目录时，ECL 不会把其中某个 Java 直接当作“全局指定”，而是把目录下的所有运行时加入自动选择，避免因最高版本不兼容而误选。

实例工作区的 Java 选择器包含“自动（默认 Java）”与所有探测到的运行时。选择“自动”会清空实例路径，交由自动逻辑决定。实例库页面只展示实例，Java 与 JVM 设置统一在实例工作区中完成。

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

## 3. 兼容性检查

`JavaCompatibility` 返回三种状态：

| 状态 | 含义 |
| --- | --- |
| `Compatible` | 满足最低要求 |
| `TooOld` | 低于最低版本，例如 1.20.1 使用 Java 8 |
| `Unknown` | 无法解析 Java 版本 |

**不存在“Java 版本过高”的拦截**：高于最低要求一律视为兼容。

行为：

- UI 在实例工作区显示“兼容 / 可能不兼容 / 未找到 Java”。
- 真实启动前，若低于最低要求且检查开启，弹出确认框；用户仍可选择继续。
- 启动流水线的 `ResolveJavaStep` 同时返回 `Warning`，CLI 会看到同样的警告。
- 实例设置 `instance.java.disableCompatibilityCheck` 可关闭该实例的检查；关闭后 UI 不再弹窗，流水线不再警告（但找不到 Java 仍会失败）。
- 实例工作区的 Java 卡片也提供同一开关，便于直接操作。

## 4. 自动下载 Java

当没有可用/兼容的 Java 时，UI“下载 Java”与 CLI `java install <实例ID>` 会调用 `IJavaInstallService`。
若实例的版本 JSON 尚未生成（例如刚创建、加载器安装还未完成），会按实例的 `GameVersion`
推断 Java 要求，避免“先有版本 JSON 才能下载 Java、先有 Java 才能安装加载器”的死循环。

1. 读取所选来源的 Java 运行时列表：
   - `mojang`：`https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json`（镜像受四档下载源策略控制）。
   - `adoptium`：Adoptium API v3（`api.adoptium.net`，JDK zip）。
   - `zulu`：Azul Metadata API（`api.azul.com`，JDK zip/tar.gz）。
   - `liberica`：BellSoft API（`api.bell-sw.com`，JDK zip/tar.gz）。
   来源由 `java.runtimeProvider` 选择，默认 `mojang`。
2. 按当前平台选择架构与包类型：Windows zip，Linux/macOS tar.gz；支持 x64、x86（有构建时）与 arm64。
3. Mojang 优先使用版本 JSON 指定的 `component`；第三方按 `MinimumMajor` 选择对应 feature 版本。
4. 第三方下载到 `<data>/cache/java/`，校验 SHA-1/SHA-256（提供方给出时），再解压到 `<data>/java/<provider>-<component>/`；解压后会重新探测 `bin/java`，找不到可执行文件则判定失败。
5. Mojang 仍走 manifest + `file` 条目流程，创建 `link` 条目（Linux/macOS），设置可执行位。
6. 用 `IJavaLocator` 重新探测，返回可用的 `JavaRuntime`。

Linux ARM64 在 Mojang 来源下会返回“平台不支持”；切换到 Adoptium/Zulu/Liberica 可以下载 ARM64 构建。

**用户无需预先安装 Java。** ECL 当前采用按需下载，而不是把 Java 二进制直接塞进启动器安装包：缺少运行时后，用户可在启动确认中同意下载，也可在实例详情页手动下载。运行时安装到 ECL 数据目录并由启动器扫描管理；不同实例可以复用兼容的本机运行时。

默认 Mojang 来源使用 Minecraft 官方运行时清单；Adoptium、Zulu、Liberica 是可选的 OpenJDK 发行版来源。Temurin 等第三方二进制随包提供其许可证与 NOTICE 信息，ECL 应保留这些文件。若未来改为随 ECL 安装包预置 Java，需要针对具体发行版和版本核对再分发条款，不能把“OpenJDK”名称本身视为统一的再分发授权。

## 5. 32/64 位与内存校验

`JavaPlatformAdvisor` 在 Java 选择后给出警告（不直接阻止启动）：

- 64 位系统 + 32 位 Java：提示改用 64 位 Java。
- 32 位系统 + 64 位 Java：提示很可能无法启动。
- ARM64 系统 + x64 Java：提示可能需要模拟层。
- 32 位 Java + 内存 > 1024 MB：提示超出 32 位 Java 的实用上限。
- 分配内存 > 系统可用内存：提示可能卡顿或启动失败。

警告会出现在实例页 Java 状态和启动流水线的 `java` 步骤中。

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
- “关闭 Java 兼容性检验”：实例级开关。
- 状态文本 + 下载进度。

实例库只展示实例名称、Minecraft 版本、加载器与最近启动时间，并提供“打开工作区”入口；Java、JVM、账户、模组与诊断等设置都集中在实例工作区。

CLI：

```bash
ecl java list
ecl java install <实例ID>
ecl launch <实例ID> --dry-run
```

## 9. 限制与后续

- 内置预设之外可通过自定义 JSON 文件加载参数；尚不支持在 UI 中编辑并保存新的命名预设。
- 32/64 位与内存校验只给出警告，不强制阻止启动。
- Microsoft 账户体系（原 v0.3）因 OAuth 审核限制暂缓，不影响本地离线启动。


## Microsoft 账户接入配置（预留）

当前版本尚未实现 Microsoft OAuth / Xbox Live / Minecraft Services 登录链路。设置页的
**账户与身份验证 → Microsoft OAuth Client ID** 可保存 Microsoft Entra 应用注册的
Application (client) ID，但它目前不会启动登录，也不会创建 Microsoft 账户。此字段是为后续接入预留，
不是“已经支持微软登录”的承诺。

准备接入时建议使用 **Mobile and desktop applications / Public client** 类型，并采用
Authorization Code + PKCE（桌面应用不应内置 Client Secret）。配置桌面端回环重定向 URI
（例如 `http://localhost`），并启用公共客户端流程。Minecraft 登录还需要后续完成 Xbox Live、
XSTS 与 Minecraft Services 的令牌交换和正版档案校验；仅有 OAuth access token 并不足以启动正版 Minecraft。

配置位置：**设置 → 全局 → 账户与身份验证 → Microsoft OAuth Client ID**。
Application (client) ID 是公开标识，不是密码；不要把 Client Secret 提交到仓库、配置文件或客户端程序中。
