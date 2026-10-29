# CurseForge 集成

v0.7 补全了 CurseForge for Minecraft API 的接入：搜索、文件解析、一键安装、必需依赖安装，以及 CurseForge 整合包中远程 `files[]` 的下载。实现位于 `ECL.Minecraft/CurseForge/`，WPF 与 CLI 共用同一套服务。

## 1. 请求路径与 API key

ECL **不持有 CurseForge API key**。客户端只把请求发往 ECL 自建的 CurseForge 代理，由代理注入 key 后转发到官方 API：

```text
ECL ── GET /v1/... ──▶ ECL 代理 ── x-api-key ──▶ https://api.curseforge.com/v1/...
```

- 代理的源码、路由、部署与凭据材料全部在 private Core 仓库，**不在本仓库**；本仓库只包含客户端与“经代理访问”的说明。
- 代理地址由 `CurseForgeClient` 中的内置常量决定；客户端路径与 CurseForge 官方 API 一致（`/v1/...`）。
- 实际 `.jar` / `.zip` 下载不经过代理，仍由 ECL 直接访问 `edge.forgecdn.net` 等官方 CDN。

关键约束：

- ECL 的源码、构建、发布产物、配置文件、日志与诊断包都不包含 key。
- 构建与运行都不读取任何 API key：没有环境变量、没有程序集元数据，也没有“从私库注入构建”的步骤。
- 客户端不存在“未配置 key”的状态；`ICurseForgeClient.Status.Source` 固定为 `Proxy`。
- 更换 key 只需要在代理服务端更新，不需要重新发布启动器。

## 2. 搜索与文件解析

`ICurseForgeClient` 封装 REST 调用，统一处理：

- 请求发往代理，不携带 `x-api-key`、`Authorization` 或 Cookie，认证完全由代理负责；
- 30 秒单请求超时；
- HTTP 401/403 → `curseforge.unauthorized`（通常是代理侧 key 无效或没有权限）；
- 404 → `curseforge.not_found`；
- 429 → 读取 `Retry-After`，否则指数退避，单请求最多尝试 3 次；
- 5xx / 网络错误 → 指数退避重试；
- 响应 JSON 解析失败 → `curseforge.invalid_response`；
- 不做磁盘缓存：CurseForge 3rd Party API 条款对 API 数据的缓存有限制。

搜索固定使用 `gameId=432`、`classId=6`（Mod），默认按热度排序，`pageSize` 上限 50（API 限制）。按实例过滤时同时传 `gameVersion` 与 `modLoaderType`：

| 实例加载器 | `modLoaderType` |
| --- | --- |
| Fabric | 4 |
| Forge / Cleanroom | 1 |
| NeoForge | 6 |
| Quilt | 5（仅搜索；ECL 不安装 Quilt） |

文件选择规则：

1. 优先使用项目 `latestFilesIndexes` 中游戏版本与加载器都匹配的文件；
2. 同版本优先 `releaseType=1`（Release），没有稳定版才选最新的 Beta/Alpha；
3. 显式 `--file-id` 时直接校验该文件；
4. 最后回退到 `GET /v1/mods/{modId}/files?gameVersion=...&modLoaderType=...`。

## 3. 一键安装

`ICurseForgeService.InstallAsync` 负责把搜索结果落到实例：

1. 校验实例存在且不是原版；
2. 解析项目（数字 ID、slug 或 `curseforge.com/.../mc-mods/<slug>` 链接）；
3. 选择兼容文件，校验游戏版本、加载器与文件名；
4. 递归解析 `relationType=3`（RequiredDependency）的必需依赖，最多 6 层，跳过已安装项目并防止循环；
5. 只使用 CurseForge CDN 的下载地址；`downloadUrl` 为空时尝试 `download-url` 端点，仍为空则报告 `curseforge.distribution_restricted`；
6. 交给 `IDownloadService` 批量下载，校验 SHA-1 与大小，支持并发、续传与暂停；
7. 在 `minecraft/.ecl-curseforge.json` 记录项目 ID / 文件 ID / 文件名，供后续更新与去重。

可选依赖（`relationType=2`）不会自动安装；不兼容关系（`relationType=5`）目前只作为信息保留。

## 4. CurseForge 整合包

`modpack import` 同时识别 Modrinth `.mrpack` 与 CurseForge `.zip`。CurseForge 流程：

1. 读取根目录 `manifest.json`；
2. 校验 `minecraft.version`、`minecraft.modLoaders[].id`（`forge-*`、`fabric-*`、`neoforge-*`、`cleanroom-*`）；
3. 批量调用 `POST /v1/mods/files`（每批 50 个）解析 `files[].fileID`；
4. `required=false` 的文件跳过并给出警告；
5. 按 `mods/<fileName>` 下载，校验 SHA-1 与大小；
6. 解压 `overrides`（manifest 可自定义目录名）与 `client-overrides/`，所有路径做目录穿越检查；
7. 写入 `.ecl-modpack.json`，记录 `sourceKind`、下载数与跳过数。

客户端不检查“有没有配置 API key”。代理不可用时，导入会返回真实的网络/HTTP 错误（例如 `curseforge.network_error`、`curseforge.unauthorized`、`curseforge.http_error`），而不是伪造一个配置错误；只有 overrides 的旧式包仍可导入。

## 5. CLI

```powershell
# 查看代理端点与连通性
ecl curseforge status --check

# 搜索（按实例版本/加载器手动过滤）
ecl curseforge search jei --game-version 1.20.1 --loader forge --page-size 10

# 查看某项目的文件，选择 --file-id
ecl curseforge files jei --game-version 1.20.1 --loader forge

# 安装到实例（默认安装必需依赖）
ecl curseforge install survival-a1b2c3 jei

# 指定文件、跳过依赖
ecl curseforge install survival-a1b2c3 238222 --file-id 9352806 --no-dependencies

# 导入完整 CurseForge 整合包
ecl modpack import survival-a1b2c3 "C:\packs\example.zip"
```

`curseforge status` 显示客户端内置的代理端点；`--json` 输出包含 `configured`、`source`（固定为 `proxy`）、`endpoint`、`checkSucceeded` 与 `checkError`。所有命令支持 `--json`，错误码稳定，便于脚本判断。

## 6. WPF

**Mods** 页面新增 CurseForge 区域：

- 自动使用当前实例的 Minecraft 版本与加载器搜索；
- 显示名称、简介、项目 ID、下载量、更新时间；
- “安装所选 Mod”下载并刷新 Mod 列表；
- “打开页面”在浏览器打开 CurseForge 项目页；
- 状态栏显示“通过 ECL 代理访问”，不再出现配置 API key 的提示；
- 原版实例仍会明确提示需要带加载器的实例。

## 7. 使用的 API

客户端只使用 CurseForge 官方 v1 接口（代理原样转发，不做路径改写）：

| 路径 | 用途 |
| --- | --- |
| `GET /v1/mods/search` | 搜索项目、slug 精确查询 |
| `GET /v1/mods/{modId}` | 项目详情与 `latestFilesIndexes` |
| `GET /v1/mods/{modId}/files` | 某项目的文件列表 |
| `GET /v1/mods/{modId}/files/{fileId}` | 单个文件详情 |
| `POST /v1/mods/files` | 按文件 ID 批量解析（整合包导入） |
| `GET /v1/mods/{modId}/files/{fileId}/download-url` | `downloadUrl` 为空时的兜底 |

## 8. 限制与排错

- 不做 Mod 更新检查、依赖版本范围严格匹配、可选依赖选择界面。
- 作者禁止第三方分发（`downloadUrl` 为空且端点无返回值）的文件无法下载；整合包会跳过并警告。
- `curseforge.unauthorized`：代理侧 key 无效、过期或没有该游戏权限；轮换代理的 key 后重试。
- `curseforge.not_found`：项目/文件不存在。
- `curseforge.rate_limited`：等待后重试；批量端点已把整合包导入的请求数压到最低。
- `curseforge.no_compatible_file`：项目没有对应游戏版本/加载器的文件。
- `curseforge.distribution_restricted`：作者未授权第三方下载。
- `curseforge.network_error` / HTTP 5xx：先用 `curseforge status --check` 确认代理连通性，再检查本机网络与代理服务状态。

测试不访问真实 API：`CurseForgeClientTests` 使用自定义 `HttpMessageHandler` 验证请求走代理主机（断言取自客户端常量）、**请求头中没有 `x-api-key`**、查询参数、无缓存与错误映射；`CurseForgeServiceTests` 与 `ModpackServiceTests` 使用假客户端和假下载服务验证安装、依赖与整合包流程。
