# Java 管理

Minecraft 对 Java 版本的要求随版本变化：1.16 及以下通常需要 Java 8，1.17 需要 16，1.18–1.20.4 通常需要 17，1.20.5+ 需要 21。Java 管理模块负责“选哪个 Java、能不能用、没有怎么办”。

## 1. 选择顺序

1. **实例指定**：`instance.java.executablePath`（实例设置中的 Java 选择器）。
2. **全局指定**：`java.defaultPath`（设置 → Java → 默认 Java 路径）。
3. **自动选择**：扫描本机 Java，按版本要求挑选最合适的运行时。
4. **默认 Java**：若没有完全匹配的运行时，使用探测到的最高版本，并在兼容性检查中提示。
5. **未找到**：真实启动失败并提示下载；UI 可直接点击“下载 Java”。

实例页的 Java 选择器包含“自动（默认 Java）”与所有探测到的运行时。选择“自动”会清空实例路径，交由自动逻辑决定。

## 2. 版本要求

`JavaRequirement` 包含：

- `MinimumMajor`：最低 Java 主版本。
- `MaximumMajor`：仅旧版本使用，例如 1.16 及以下限制为 Java 8。
- `Component`：Mojang 运行时组件名，例如 `java-runtime-gamma`。

来源优先级：

1. 版本 JSON 的 `javaVersion.majorVersion` / `javaVersion.component`。
2. 无该字段时，根据游戏版本推断：
   - `1.0`–`1.16.x` → Java 8（最高 8）。
   - `1.17.x` → Java 16。
   - `1.18`–`1.20.4` → Java 17。
   - `1.20.5+`、`1.21.x` → Java 21。
   - 快照按年份粗略推断。

## 3. 兼容性检查

`JavaCompatibility` 返回四种状态：

| 状态 | 含义 |
| --- | --- |
| `Compatible` | 满足要求 |
| `TooOld` | 低于最低版本，例如 1.20.1 使用 Java 8 |
| `TooNew` | 高于旧版本上限，例如 1.16.5 使用 Java 21 |
| `Unknown` | 无法解析 Java 版本 |

行为：

- UI 在实例页显示“兼容 / 可能不兼容 / 未找到 Java”。
- 真实启动前，若不兼容且检查开启，弹出确认框；用户仍可选择继续。
- 启动流水线的 `ResolveJavaStep` 同时返回 `Warning`，CLI 会看到同样的警告。
- 实例设置 `instance.java.disableCompatibilityCheck` 可关闭该实例的检查；关闭后 UI 不再弹窗，流水线不再警告（但找不到 Java 仍会失败）。
- 实例页 Java 卡片也提供同一开关，便于直接操作。

## 4. 自动下载 Java

当没有可用/兼容的 Java 时，UI“下载 Java”与 CLI `java install <实例ID>` 会调用 `IJavaInstallService`：

1. 读取 Mojang Java 运行时索引：
   `https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json`
   镜像：`https://bmclapi2.bangbang93.com/...`（受下载源四档策略控制）。
2. 按当前平台选择索引键：
   - `windows-x64` / `windows-x86` / `windows-arm64`
   - `linux` / `linux-i386`
   - `mac-os` / `mac-os-arm64`
3. 优先使用版本 JSON 指定的 `component`；否则按 `MinimumMajor` 选择匹配组件。
4. 下载运行时 manifest，再下载全部 `file` 条目，校验 SHA-1、支持续传与并发。
5. 解压到 `<data>/java/<component>`；创建 `link` 条目（Linux/macOS），设置可执行位。
6. 用 `IJavaLocator` 重新探测，返回可用的 `JavaRuntime`。

当前只支持 Mojang 提供的 Java 运行时；Adoptium/其他发行版的下载将在后续扩展。Linux ARM64 等没有 Mojang 运行时的平台会返回“平台不支持”。

## 5. 数据目录

```text
<data>/java/
  java-runtime-delta/        # 组件名
    bin/java(.exe)
    release
    ...
  jre-legacy/
  ...
```

`JavaLocator` 会扫描该目录（深度 2），因此下载完成后无需手工导入。

## 6. UI 与 CLI

UI 实例页：

- Java 下拉框：自动 / 本机运行时。
- “刷新”：重新扫描。
- “下载 Java”：按当前实例版本要求下载。
- “关闭 Java 兼容性检验”：实例级开关。
- 状态文本 + 下载进度。

CLI：

```bash
ecl java list
ecl java install <实例ID>
ecl launch <实例ID> --dry-run
```

## 7. 限制与后续

- 尚未提供 JVM 参数预设（G1/ZGC/Aikar flags）。
- 尚未校验 32/64 位与游戏内存需求是否匹配。
- 尚未支持 Adoptium、Zulu、Liberica 等第三方运行时下载。
- Microsoft 账户体系（原 v0.3）因 OAuth 审核限制暂缓，不影响本地离线启动。
