# ADR 0010：CurseForge 改由私有代理转发

- 状态：已采纳
- 日期：2026-10-30

## 背景

[ADR 0009](0009-curseforge-integration.md) 让 ECL 从环境变量或程序集元数据读取 CurseForge API key。发布版因此必须把 key 嵌入二进制：key 可以被提取，轮换 key 需要重新发布客户端，Core 仓库与发布流程也始终承担 key 注入职责。与此同时，CurseForge 3rd Party API 条款对 API 数据缓存有限制，客户端磁盘缓存不应继续保留。

## 决策

1. **单一入口**：ECL 的所有 CurseForge API 请求固定发往 ECL 自建的代理，由代理注入 `x-api-key` 后转发到 `https://api.curseforge.com/v1/...`。
2. **客户端不持有 key**：删除 `CurseForgeApiKeyProvider`，不再读取 `ECL_CURSEFORGE_API_KEY`，不再写入 `AssemblyMetadata`，也不再发送 `x-api-key`；`CurseForgeStatus.Source` 固定为 `Proxy`。
3. **代理实现不公开**：代理的源码、路由、凭据与部署材料只存在于 private Core 仓库。public 仓库只保留客户端中必须存在的代理域名常量，以及“CurseForge 请求经自建代理转发”的说明——代理地址公开后，任何人都能无鉴权地耗尽 key 配额。
4. **只代理 API，不代理下载**：代理只转发 API 请求；实际 `.jar` / `.zip` 下载仍由 ECL 直接访问 `edge.forgecdn.net` 等官方 CDN。
5. **移除客户端缓存**：关闭 `CurseForgeClient` 的磁盘缓存，遵循 CurseForge 对 API 数据缓存的限制。
6. **移除“未配置 key”分支**：`CurseForgeService`、`ModpackService`、WPF 与 CLI 不再检查客户端 key；代理不可用时返回真实的网络/HTTP 错误。
7. **测试锁定架构**：客户端测试断言请求主机来自 `CurseForgeClient.BaseAddress`、路径为 `/v1/...`，且请求头中不存在 `x-api-key`。

## 后果

- 优点：key 不再随客户端分发；轮换 key 不需要重新发布启动器；Core 仓库不再参与构建；API 缓存合规风险降低；代理地址与实现不进入公开视野。
- 缺点：CurseForge 功能新增对自建代理与其托管平台的可用性依赖；代理需要单独部署、监控与轮换 Secret；需要额外维护一个私有代理项目。
- 约束：不得在 ECL 中重新引入读取/发送/构建时注入 key 的逻辑；不得在任何 public 文件、日志或截图中出现代理地址、路由或部署配置；代理侧必须配置限流与滥用防护。

## 修订（2026-10-30）

初版把代理工程放在 public 仓库，并让客户端请求 `/curseforge/v1/...`；实际部署的代理只接受官方 `/v1/*` 路径，带前缀的请求不命中代理，表现为 403。现统一为：客户端使用官方 `/v1/...` 路径，代理工程整体移入 private Core 仓库，public 仓库不再包含任何代理源码、部署说明或地址细节。此前“从私有仓库注入构建”的 `CurseForgeApiKeyProvider` / `AssemblyMetadata` 方案不再保留，客户端与构建过程中都不存在任何 key 注入代码。

## 相关

- [curseforge.md](../curseforge.md)
- [repository-policy.md](../repository-policy.md)
- [ADR 0009](0009-curseforge-integration.md)
