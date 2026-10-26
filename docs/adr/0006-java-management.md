# ADR 0006：Java 选择、兼容性检查与自动下载

- 状态：已采纳
- 日期：2026-10-26

## 背景

不同 Minecraft 版本对 Java 主版本要求差异大（8/16/17/21）。用户可能没有合适的 Java，也可能手动选了明显不兼容的版本。v0.3 账户体系因 Microsoft OAuth 审核限制暂缓，因此优先把 v0.4 Java 管理做完整。

## 决策

1. **选择顺序**：实例路径 → 全局路径 → 自动选择 → 探测到的最高版本兜底。
2. **版本要求**：优先读取版本 JSON `javaVersion`；缺失时按游戏版本推断（1.16- → Java 8，1.17 → 16，1.18–1.20.4 → 17，1.20.5+ → 21）。
3. **兼容性检查**：`JavaCompatibility` 返回 Compatible/TooOld/TooNew/Unknown。默认只警告，不阻止启动；UI 在真实启动前弹窗确认。实例级 `instance.java.disableCompatibilityCheck` 可关闭。
4. **无 Java**：真实启动失败并提示下载；UI 可直接点击“下载 Java”，CLI 提供 `java install <实例ID>`。
5. **自动下载**：使用 Mojang Java 运行时索引（受四档下载源策略控制），按组件/主版本选择，下载到 `<data>/java/<component>`；SHA-1 校验、续传、并发、符号链接与执行位处理。
6. **平台**：优先支持 Windows x64/x86/arm64、Linux x64/x86、macOS x64/arm64；无 Mojang 运行时的平台明确失败。

## 后果

- 优点：用户无需手动寻找 Java；旧版本自动下载 Java 8；新版本优先精确主版本；误选时仍有提示但可继续。
- 缺点：Mojang 运行时索引与组件名是外部契约，需要维护；仅支持 Mojang 运行时。
- 风险：Java 25 等过新版本对部分旧版本可能不兼容；当前只对 ≤Java 8 设置上限，后续可扩展版本范围。
