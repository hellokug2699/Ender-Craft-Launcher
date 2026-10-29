# CurseForge 集成

v0.7 补全了 CurseForge for Minecraft API 的接入：搜索、文件解析、一键安装、必需依赖安装，以及 CurseForge 整合包中远程 `files[]` 的下载。实现位于 `ECL.Minecraft/CurseForge/`，WPF 与 CLI 共用同一套服务。

## 1. API key 放在哪里

CurseForge 的 key 是**私有凭据**，不进入 public 仓库：

| 位置 | 用途 |
| --- | --- |
| `Ender-Craft-Launcher-Core/credentials/curseforge.json` | 私库中的唯一持久副本 |
| 环境变量 `ECL_CURSEFORGE_API_KEY` | 本地开发、CI、发布构建时注入 |
| 程序集元数据 `Ecl.CurseForge.ApiKey` | 构建时由环境变量写入发布版，供最终用户使用 |

读取优先级：

1. 进程环境变量 `ECL_CURSEFORGE_API_KEY`；
2. `ECL.Minecraft.dll` 中的构建时元数据；
3. 都没有 → `CurseForge` 功能明确返回 `curseforge.not_configured`，UI 显示配置提示，但启动器其他功能不受影响。

public 仓库的普通构建（不设置环境变量）**不包含任何 key**，仍然可以完整构建和测试：

```powershell
dotnet restore ECL.sln
dotnet build ECL.sln -c Release
dotnet test tests/ECL.Tests/ECL.Tests.csproj -c Release
```

发布版构建：

```powershell
# 在 public 仓库根目录执行；key 从同级私有仓库读取，只注入当前进程
$env:ECL_CURSEFORGE_API_KEY = (Get-Content ..\Ender-Craft-Launcher-Core\credentials\curseforge.json -Raw -Encoding UTF8 | ConvertFrom-Json).apiKey
dotnet publish src/ECL.App/ECL.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64
```

注意：

- 环境变量只影响当前 PowerShell 进程，不会写入用户环境变量。
- 改变 key 后建议先 `dotnet clean` 再构建，避免旧的程序集元数据残留。
- key 一旦嵌入客户端二进制，就无法真正保密；这是第三方启动器使用 CurseForge API 的固有限制。泄露或滥用时按 [repository-policy.md](repository-policy.md) 轮换。
- 日志、CLI 输出、诊断包都不会输出 key；只显示“环境变量 / 发布版内置 / 未配置”。
- 不要在 public 仓库的 `.env`、脚本、测试或 CI 日志中写入 key。`.gitignore` 已忽略 `secrets/`、`.env` 等路径作为纵深防御。

## 2. 搜索与文件解析

`ICurseForgeClient` 封装 REST 调用，统一处理：

- `x-api-key` 认证、`User-Agent`、30 秒单请求超时；
- HTTP 401/403 → `curseforge.unauthorized`；
- 404 → `curseforge.not_found`；
- 429 → 读取 `Retry-After`，单请求最多尝试 3 次；
- 5xx / 网络错误 → 指数退避重试；
- 响应 JSON 解析失败 → `curseforge.invalid_response`；
- 磁盘缓存（`<data>/cache/curseforge/`）：搜索 10 分钟，项目/文件元数据 6 小时。

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

如果包内包含远程文件但未配置 key，导入会直接失败并返回 `curseforge.not_configured`，不会静默导入一个残缺整合包；只有 overrides 的旧式包仍可导入。

## 5. CLI

```powershell
# 查看配置与连通性
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

所有命令支持 `--json`，错误码稳定，便于脚本判断。

## 6. WPF

**Mods** 页面新增 CurseForge 区域：

- 自动使用当前实例的 Minecraft 版本与加载器搜索；
- 显示名称、简介、项目 ID、下载量、更新时间；
- “安装所选 Mod”下载并刷新 Mod 列表；
- “打开页面”在浏览器打开 CurseForge 项目页；
- 未配置 key 或原版实例时给出明确提示，不会把失败伪装成成功。

## 7. 使用的 API

| 端点 | 用途 |
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
- CurseForge 的 key 会随发布版二进制分发，无法做到真正保密。
- `curseforge.unauthorized`：key 无效或没有该游戏权限。
- `curseforge.rate_limited`：等待后重试；批量端点已把整合包导入的请求数压到最低。
- `curseforge.no_compatible_file`：项目没有对应游戏版本/加载器的文件。
- `curseforge.distribution_restricted`：作者未授权第三方下载。
- 如果 CurseForge 页面显示已更新但客户端仍拿到旧文件，删除 `<data>/cache/curseforge/` 后重试。

测试不访问真实 API：`CurseForgeClientTests` 使用自定义 `HttpMessageHandler` 验证请求头、查询参数、缓存与错误映射；`CurseForgeServiceTests` 与 `ModpackServiceTests` 使用假客户端和假下载服务验证安装、依赖与整合包流程。
