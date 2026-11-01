# ADR 0006：Java 选择、兼容性检查与自动下载

- Status: Completed
- Date: 2026-10-26
- Scope: ECL 架构与实现

## 背景

不同 Minecraft 版本对 Java 主版本要求差异大（8/16/17/21）。用户可能没有合适的 Java，也可能手动选了明显不兼容的版本。当时 Microsoft OAuth 尚未接入，因此优先把 Java 管理做完整；账户认证现状以 [authentication.md](../authentication.md) 为准。

## 决策

1. **选择顺序**：实例路径 → 全局路径 → 自动选择 → 探测到的最高版本兜底。
2. **版本要求**：优先读取版本 JSON `javaVersion`（权威来源，覆盖 1.x、26.x 及未来版本号）；缺失时按游戏版本推断（1.16- → Java 8，1.17 → 16，1.18–1.20.4 → 17，1.20.5–1.21.x → 21，26.x → 25）。
3. **兼容性检查**：`JavaCompatibility` 返回 Compatible/TooOld/Unknown；**只检查最低版本，不限制最高版本**。默认只警告，不阻止启动；UI 在真实启动前弹窗确认。实例级 `instance.java.disableCompatibilityCheck` 可关闭。
4. **无 Java**：真实启动失败并提示下载；UI 可直接点击“下载 Java”，CLI 提供 `java install <实例ID>`。
5. **自动下载**：初始决策使用 Mojang Java 运行时索引（受四档下载源策略控制），按组件/主版本选择，下载到 `<data>/java/<component>`；SHA-1 校验、续传、并发、符号链接与执行位处理。后续实现已扩展 Adoptium、Zulu、Liberica，当前行为以 [java.md](../java.md) 为准。
6. **平台**：运行时平台与架构支持取决于所选供应商；Windows DPAPI 凭据限制与 Java 平台支持是两个独立问题。

## 后果

- 优点：用户无需手动寻找 Java；旧版本自动下载 Java 8；新版本优先精确主版本；误选时仍有提示但可继续。
- 缺点：Mojang 运行时索引与组件名是外部契约，需要维护；第三方供应商的 API、归档布局与许可证也需要单独维护。
- 风险：Java 版本只设下限，较新的 Java 可能仍与旧游戏不兼容；ECL 不再拦截或提示“过高”，由用户自行判断/切换。26.x 的 Java 需求以版本 JSON 为准，缺失时回退 Java 25。
