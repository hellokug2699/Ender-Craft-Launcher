# ADR 0005：四档下载源策略

- 状态：已采纳
- 日期：2026-10-26

## 背景

Mojang 官方源在部分网络环境下不稳定；BMCLAPI 是国内常用镜像。用户希望同时保留官方优先、镜像优先和强制单源的选择，而不是简单的“官方/镜像”二元开关。

## 决策

1. 配置项 `network.downloadSource` 提供四档：
   - `official-only`
   - `official-first`（默认）
   - `mirror-first`
   - `mirror-only`
2. 所有下载请求都使用“官方 URL 候选列表”，由 `DownloadSourceResolver` 按模式展开为有序候选：
   - 官方 URL 保留。
   - 可映射 URL 追加 BMCLAPI 镜像 URL。
3. 镜像映射规则：
   - `piston-meta` / `piston-data` / `launchermeta` / `launcher` → `bmclapi2.bangbang93.com` + 原路径。
   - `libraries.minecraft.net` → `/libraries` 前缀。
   - `resources.download.minecraft.net` → `/assets` 前缀。
4. 无镜像映射的 URL 在 `mirror-only` 下直接失败，避免静默回退官方。
5. 下载器按候选顺序尝试；HTTP 4xx 不重试，5xx/超时/IO 错误重试，哈希不匹配则清理断点文件并尝试下一候选。

## 后果

- 优点：用户可精确控制来源；官方优先保证新鲜度，镜像优先/单镜像适配受限网络；下载层与业务层解耦。
- 缺点：镜像可能缓存滞后或缺少个别文件；`mirror-only` 下失败需要明确提示。
- 风险：BMCLAPI 端点或映射规则变化时需更新 `DownloadSourceResolver`；集中在单一类中，便于维护。
