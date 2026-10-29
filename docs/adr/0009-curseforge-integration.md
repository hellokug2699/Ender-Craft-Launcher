# ADR 0009：CurseForge API 集成与凭据注入

- 状态：已被 [ADR 0010](0010-curseforge-private-proxy.md) 取代
- 日期：2026-10-29

> 2026-10-30 更新：CurseForge 已改为经私有代理转发，客户端不再持有 API key。
> 本 ADR 保留作为历史记录，当前架构以 ADR 0010 为准。

## 背景

v0.6 的 CurseForge 整合包导入只解压 overrides，远程 `files[]` 因缺少 API key 无法下载；WPF/CLI 也没有 Mod 搜索与一键安装。CurseForge 提供官方 REST API（`https://api.curseforge.com`），要求每个请求携带 `x-api-key`。key 是私有凭据，不能进入 public 仓库，但最终发布版又必须在用户机器上访问 API。

## 决策

1. **凭据只存 Core 仓库**：`Ender-Craft-Launcher-Core/credentials/curseforge.json`；public 仓库只包含读取逻辑与文档。
2. **双通道注入**：
   - 运行时环境变量 `ECL_CURSEFORGE_API_KEY` 优先，供开发、CI 与轮换；
   - 构建时环境变量写入 `AssemblyMetadata("Ecl.CurseForge.ApiKey", ...)`，供发布版使用；
   - 两者都没有时，CurseForge 功能返回 `curseforge.not_configured`，启动器其余功能正常。
3. **不把 key 放进元数据驱动配置**：不写 `global.json`、不渲染到设置页、不进入诊断包；避免普通配置同步/导出泄漏。
4. **API 客户端独立于 UI**：`ICurseForgeClient` 负责认证、查询、缓存、限流与错误映射；`ICurseForgeService` 负责实例兼容性、文件选择与依赖解析；WPF 与 CLI 共用。
5. **整合包使用批量文件端点**：`POST /v1/mods/files` 按 50 个一批解析 `fileID`，避免每个文件一次请求触发限流。
6. **下载源白名单**：只接受 CurseForge 官方 CDN 的 URL，防止被篡改的 API 响应或代理把下载引向任意地址。
7. **失败显式化**：缺少 key、加载器不兼容、无兼容文件、作者禁止分发、限流分别返回稳定错误码，不静默导入残缺包。

## 后果

- 优点：public 构建与测试不需要 key；发布版开箱即用；key 轮换不需要改源码；整合包导入请求数可控。
- 缺点：key 嵌入二进制后可以被提取，无法真正保密；发布构建必须显式注入 key，否则发布版 CurseForge 功能不可用；依赖 Core 或 CI Secret 的发布流程需要额外步骤。
- 约束：public 仓库的普通构建与 CI 永不设置 `ECL_CURSEFORGE_API_KEY`；发布日志不得输出 key；发现滥用时立即轮换。

## 相关

- [curseforge.md](../curseforge.md)
- [repository-policy.md](../repository-policy.md)
- [ADR 0003](0003-public-private-repository-split.md)
